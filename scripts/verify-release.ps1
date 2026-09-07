#Requires -Version 5
# Gate a Magic Tray publish folder and the portable ZIP: required artifacts,
# metadata, ZIP layout, and SHA256SUMS.
# Exit 0 on pass, 1 on any failure. Does not call gh or create a release.
param(
  [Parameter(Mandatory)][string]$PublishDir,
  [string]$Tag = $env:GITHUB_REF_NAME,
  # Portable ZIP to gate. Empty: auto-discover dist/MagicTray-*-win-x64.zip.
  # A path that does not exist is a failure, never a skip.
  [string]$ZipPath = '',
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
$failed = 0

function Write-Check {
  param(
    [bool]$Ok,
    [Parameter(Mandatory)][string]$Name,
    [string]$Detail = ''
  )
  if ($Ok) {
    if ($Detail) { Write-Host "PASS  ${Name}: $Detail" }
    else { Write-Host "PASS  $Name" }
  } else {
    $script:failed++
    if ($Detail) { Write-Host "FAIL  ${Name}: $Detail" }
    else { Write-Host "FAIL  $Name" }
  }
}

function Get-CsprojVersion {
  param([Parameter(Mandatory)][string]$Path)
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "csproj not found: $Path"
  }
  [xml]$xml = Get-Content -LiteralPath $Path -Raw
  $versions = @(
    @($xml.Project.PropertyGroup) |
      ForEach-Object { $_.Version } |
      Where-Object { $_ }
  )
  if ($versions.Count -eq 0) {
    throw "no <Version> in $Path"
  }
  return [string]$versions[0]
}

function Get-PeMachine {
  param([Parameter(Mandatory)][string]$Path)
  $stream = [IO.File]::OpenRead($Path)
  try {
    $reader = New-Object IO.BinaryReader $stream
    if ($reader.ReadUInt16() -ne 0x5A4D) { return [uint16]0 }
    [void]$stream.Seek(0x3C, [IO.SeekOrigin]::Begin)
    $pe = $reader.ReadInt32()
    if ($pe -lt 0 -or ($pe + 6) -gt $stream.Length) { return [uint16]0 }
    [void]$stream.Seek($pe, [IO.SeekOrigin]::Begin)
    if ($reader.ReadUInt32() -ne 0x00004550) { return [uint16]0 }
    return $reader.ReadUInt16()
  } finally {
    $stream.Dispose()
  }
}

# SHA256 one ZIP entry without buffering it. The exe entry is >50 MiB, so it is
# streamed through the hash rather than materialised as a byte[].
function Get-ZipEntryHash {
  param([Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry)
  $stream = $Entry.Open()
  try {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
      return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    } finally {
      $sha.Dispose()
    }
  } finally {
    $stream.Dispose()
  }
}

# Text of a small ZIP entry (SHA256SUMS, README.txt). Never called for the exe.
function Get-ZipEntryText {
  param([Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry)
  $stream = $Entry.Open()
  try {
    $reader = New-Object IO.StreamReader $stream
    try {
      return $reader.ReadToEnd()
    } finally {
      $reader.Dispose()
    }
  } finally {
    $stream.Dispose()
  }
}

try {
  if (-not (Test-Path -LiteralPath $PublishDir)) {
    Write-Host "FAIL  publish dir: not found $PublishDir"
    exit 1
  }
  $PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
  $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

  $exeName = 'MagicMouseTray.exe'
  $ps1Name = 'kbd-patch-cachedservices.ps1'
  $cmdName = 'Install-KeyboardBattery.cmd'
  $shipNames = @($exeName, $ps1Name, $cmdName, 'capture-state.ps1', 'diagnose-driver.ps1', 'mm-bt-stack-snapshot.ps1')
  $exePath = Join-Path $PublishDir $exeName
  $ps1Path = Join-Path $PublishDir $ps1Name
  $cmdPath = Join-Path $PublishDir $cmdName

  # 1. Required ship files exist.
  $missing = @($shipNames | Where-Object { -not (Test-Path -LiteralPath (Join-Path $PublishDir $_) -PathType Leaf) })
  Write-Check -Ok ($missing.Count -eq 0) -Name 'artifacts' -Detail $(
    if ($missing.Count -gt 0) { "missing $($missing -join ', ')" } else { $shipNames -join ', ' }
  )

  # 2. Exe length 50-300 MiB.
  if (Test-Path -LiteralPath $exePath -PathType Leaf) {
    $len = (Get-Item -LiteralPath $exePath).Length
    Write-Check -Ok (($len -ge 50MB) -and ($len -le 300MB)) -Name 'exe size' -Detail (
      '{0:N0} bytes (require 50-300 MiB)' -f $len
    )
  } else {
    Write-Check -Ok $false -Name 'exe size' -Detail 'MagicMouseTray.exe missing'
  }

  # 3. ProductName + FileVersion vs tag/csproj.
  $csprojPath = Join-Path $RepoRoot (Join-Path 'MagicMouseTray' 'MagicMouseTray.csproj')
  $csprojVersion = $null
  try {
    $csprojVersion = Get-CsprojVersion $csprojPath
  } catch {
    Write-Check -Ok $false -Name 'FileVersionInfo' -Detail "$_"
  }

  $tagText = [string]$Tag
  $tagMatch = [regex]::Match($tagText, '^v(\d+\.\d+\.\d+)$')
  $expectedProductVersion = $null
  if ($null -ne $csprojVersion) {
    if ($tagMatch.Success) {
      $expectedProductVersion = $tagMatch.Groups[1].Value
    } else {
      $expectedProductVersion = $csprojVersion
    }
  }
  $expectedFileVersion = $null
  if ($expectedProductVersion -match '^\d+\.\d+\.\d+$') {
    $expectedFileVersion = "$expectedProductVersion.0"
  } elseif ($expectedProductVersion) {
    $expectedFileVersion = $expectedProductVersion
  }

  if ($null -ne $csprojVersion) {
    $reasons = @()
    if ($tagMatch.Success -and $csprojVersion -ne $expectedProductVersion) {
      $reasons += "csproj <Version>$csprojVersion</Version> != $expectedProductVersion (tag $tagText)"
    }
    if (Test-Path -LiteralPath $exePath -PathType Leaf) {
      $vi = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $exePath).Path)
      $productName = [string]$vi.ProductName
      $fileVersion = [string]$vi.FileVersion
      if ($productName.Trim() -ne 'Magic Tray') {
        $reasons += "ProductName='$productName' (expect 'Magic Tray')"
      }
      if (-not $expectedFileVersion -or $fileVersion.Trim() -ne $expectedFileVersion) {
        $reasons += "FileVersion='$fileVersion' (expect '$expectedFileVersion')"
      }
    } else {
      $reasons += 'MagicMouseTray.exe missing'
    }
    Write-Check -Ok ($reasons.Count -eq 0) -Name 'FileVersionInfo' -Detail $(
      if ($reasons.Count -gt 0) { $reasons -join '; ' } else { "ProductName='Magic Tray' FileVersion='$expectedFileVersion'" }
    )
  }

  # 4. PE machine AMD64 (COFF Machine 0x8664).
  if (Test-Path -LiteralPath $exePath -PathType Leaf) {
    $machine = Get-PeMachine $exePath
    Write-Check -Ok ($machine -eq 0x8664) -Name 'PE machine' -Detail (
      '0x{0:X4} (require AMD64 0x8664)' -f $machine
    )
  } else {
    Write-Check -Ok $false -Name 'PE machine' -Detail 'MagicMouseTray.exe missing'
  }

  # 5. Install-KeyboardBattery.cmd: -Mac guard and same-folder ps1 reference.
  # Read text only — do not execute (pause + UAC).
  if (Test-Path -LiteralPath $cmdPath -PathType Leaf) {
    $cmdText = Get-Content -LiteralPath $cmdPath -Raw
    $hasMacGuard = $cmdText -match 'HASMAC'
    $hasPatchRef = $cmdText -match 'kbd-patch-cachedservices\.ps1'
    $sameFolder = $cmdText -match '%~dp0'
    $cmdReasons = @()
    if (-not $hasMacGuard) { $cmdReasons += 'missing HASMAC -Mac guard' }
    if (-not $hasPatchRef) { $cmdReasons += 'missing kbd-patch-cachedservices.ps1 reference' }
    if (-not $sameFolder) { $cmdReasons += 'ps1 not referenced in the same folder (%~dp0)' }
    Write-Check -Ok ($cmdReasons.Count -eq 0) -Name 'Install-KeyboardBattery.cmd' -Detail $(
      if ($cmdReasons.Count -gt 0) { $cmdReasons -join '; ' } else { 'HASMAC guard, same-folder kbd-patch-cachedservices.ps1' }
    )
  } else {
    Write-Check -Ok $false -Name 'Install-KeyboardBattery.cmd' -Detail 'file missing'
  }

  # 6. kbd-patch-cachedservices.ps1 non-empty with param / -Mac.
  if (Test-Path -LiteralPath $ps1Path -PathType Leaf) {
    $ps1Len = (Get-Item -LiteralPath $ps1Path).Length
    $ps1Text = Get-Content -LiteralPath $ps1Path -Raw
    $ps1Reasons = @()
    if ($ps1Len -le 0 -or [string]::IsNullOrWhiteSpace($ps1Text)) { $ps1Reasons += 'empty' }
    if ($ps1Text -notmatch 'param') { $ps1Reasons += 'missing param' }
    if ($ps1Text -notmatch '-Mac') { $ps1Reasons += 'missing -Mac' }
    Write-Check -Ok ($ps1Reasons.Count -eq 0) -Name 'kbd-patch-cachedservices.ps1' -Detail $(
      if ($ps1Reasons.Count -gt 0) { $ps1Reasons -join '; ' } else { 'non-empty, param, -Mac' }
    )
  } else {
    Write-Check -Ok $false -Name 'kbd-patch-cachedservices.ps1' -Detail 'file missing'
  }

  # 7. SHA256 of the three ship files (not the sums file).
  if ($missing.Count -eq 0) {
    $lines = foreach ($name in $shipNames) {
      $hash = (Get-FileHash -LiteralPath (Join-Path $PublishDir $name) -Algorithm SHA256).Hash.ToLowerInvariant()
      '{0}  {1}' -f $hash, $name
    }
    $sumsPath = Join-Path $PublishDir 'SHA256SUMS'
    $ascii = [Text.Encoding]::ASCII
    [IO.File]::WriteAllLines($sumsPath, [string[]]$lines, $ascii)
    $wrote = (Test-Path -LiteralPath $sumsPath -PathType Leaf) -and ((Get-Item -LiteralPath $sumsPath).Length -gt 0)
    Write-Check -Ok $wrote -Name 'SHA256SUMS' -Detail $sumsPath
  } else {
    Write-Check -Ok $false -Name 'SHA256SUMS' -Detail 'ship files missing'
  }

  # 8. Portable ZIP: it exists, its tree matches the layout contract exactly,
  # every payload file is non-empty, nothing stray rode along (.pdb, a nested
  # second exe), its SHA256SUMS verifies against real entry bytes, and the
  # packaged exe is byte-identical to the one in publish (so a signed exe was
  # the one packaged). Every failure detail names the offending file.
  $expectedEntries = @(
    'MagicMouseTray.exe',
    'README.txt',
    'SHA256SUMS',
    'scripts/Install-KeyboardBattery.cmd',
    'scripts/capture-state.ps1',
    'scripts/diagnose-driver.ps1',
    'scripts/kbd-patch-cachedservices.ps1',
    'scripts/mm-bt-stack-snapshot.ps1'
  )
  $expectedScripts = @($expectedEntries | Where-Object { $_ -like 'scripts/*' })

  # Resolve the ZIP. An explicit -ZipPath that is not there is a failure, never
  # a skip; with no -ZipPath, exactly one dist/MagicTray-*-win-x64.zip must exist.
  $zip = [string]$ZipPath
  $zipOk = $true
  if ([string]::IsNullOrWhiteSpace($zip)) {
    $searchDir = Join-Path $RepoRoot 'dist'
    $found = @()
    if (Test-Path -LiteralPath $searchDir -PathType Container) {
      $found = @(
        Get-ChildItem -LiteralPath $searchDir -Filter 'MagicTray-*-win-x64.zip' -File |
          Sort-Object -Property Name
      )
    }
    if ($found.Count -eq 1) {
      $zip = $found[0].FullName
    } elseif ($found.Count -eq 0) {
      $zipOk = $false
      Write-Check -Ok $false -Name 'zip' -Detail "no MagicTray-*-win-x64.zip in $searchDir"
    } else {
      $zipOk = $false
      Write-Check -Ok $false -Name 'zip' -Detail (
        "expected one MagicTray-*-win-x64.zip in ${searchDir}, found $($found.Count): " +
        (($found | ForEach-Object { $_.Name }) -join ', ')
      )
    }
  } elseif (-not (Test-Path -LiteralPath $zip -PathType Leaf)) {
    $zipOk = $false
    Write-Check -Ok $false -Name 'zip' -Detail "not found $zip"
  }

  if ($zipOk) {
    $zip = (Resolve-Path -LiteralPath $zip).Path
    $zipFile = Split-Path -Leaf $zip
    Write-Check -Ok $true -Name 'zip' -Detail $zip

    if ($tagMatch.Success) {
      $expectedZipName = "MagicTray-$tagText-win-x64.zip"
      Write-Check -Ok ($zipFile -eq $expectedZipName) -Name 'zip name' -Detail $(
        if ($zipFile -eq $expectedZipName) { $zipFile } else { "$zipFile (expect $expectedZipName)" }
      )
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
      # Directory entries have an empty Name; only files count against the tree.
      $entries = @($archive.Entries | Where-Object { $_.Name })
      $actual = @($entries | ForEach-Object { $_.FullName -replace '\\', '/' } | Sort-Object)

      $missingInZip = @($expectedEntries | Where-Object { $actual -notcontains $_ })
      $strayInZip = @($actual | Where-Object { $expectedEntries -notcontains $_ })
      $layoutReasons = @()
      if ($missingInZip.Count -gt 0) { $layoutReasons += "missing $($missingInZip -join ', ')" }
      if ($strayInZip.Count -gt 0) { $layoutReasons += "stray $($strayInZip -join ', ')" }
      Write-Check -Ok ($layoutReasons.Count -eq 0) -Name 'zip layout' -Detail $(
        if ($layoutReasons.Count -gt 0) { "$zipFile -> $($layoutReasons -join '; ')" }
        else { "$($actual.Count) entries, exact match" }
      )

      # Every payload file carries bytes. An empty script is a silent no-op at
      # runtime, so it fails here by name.
      $byPath = @{}
      foreach ($e in $entries) { $byPath[($e.FullName -replace '\\', '/')] = $e }
      $checkedForBytes = @(
        @($expectedScripts + @('README.txt', 'SHA256SUMS', 'MagicMouseTray.exe')) |
          Where-Object { $byPath.ContainsKey($_) }
      )
      $emptyEntries = @($checkedForBytes | Where-Object { $byPath[$_].Length -le 0 })
      Write-Check -Ok ($emptyEntries.Count -eq 0) -Name 'zip payload non-empty' -Detail $(
        if ($emptyEntries.Count -gt 0) { "empty in ${zipFile}: $($emptyEntries -join ', ')" }
        else { "$($checkedForBytes.Count) of $($expectedEntries.Count) expected entries carry bytes" }
      )

      # SHA256SUMS covers every entry except itself, and each hash is real.
      if ($byPath.ContainsKey('SHA256SUMS')) {
        $sumsText = Get-ZipEntryText $byPath['SHA256SUMS']
        $declared = [ordered]@{}
        foreach ($line in ($sumsText -split "`r?`n")) {
          if ([string]::IsNullOrWhiteSpace($line)) { continue }
          $m = [regex]::Match($line.Trim(), '^([0-9a-fA-F]{64})\s+\*?(.+)$')
          if (-not $m.Success) {
            $declared['?malformed?'] = $line.Trim()
            continue
          }
          $declared[($m.Groups[2].Value.Trim() -replace '\\', '/')] = $m.Groups[1].Value.ToLowerInvariant()
        }
        $sumReasons = @()
        if ($declared.Contains('?malformed?')) {
          $sumReasons += "unparsable line '$($declared['?malformed?'])'"
        }
        $shouldCover = @($actual | Where-Object { $_ -ne 'SHA256SUMS' })
        foreach ($rel in $shouldCover) {
          if (-not $declared.Contains($rel)) {
            $sumReasons += "$rel not listed in SHA256SUMS"
            continue
          }
          $realHash = Get-ZipEntryHash $byPath[$rel]
          if ($realHash -ne $declared[$rel]) {
            $sumReasons += "$rel hash mismatch (SHA256SUMS $($declared[$rel]), actual $realHash)"
          }
        }
        foreach ($rel in @($declared.Keys)) {
          if ($rel -eq '?malformed?') { continue }
          if ($shouldCover -notcontains $rel) {
            $sumReasons += "$rel listed in SHA256SUMS but not in the zip"
          }
        }
        Write-Check -Ok ($sumReasons.Count -eq 0) -Name 'zip SHA256SUMS' -Detail $(
          if ($sumReasons.Count -gt 0) { $sumReasons -join '; ' }
          else { "$($shouldCover.Count) entries verified" }
        )
      } else {
        Write-Check -Ok $false -Name 'zip SHA256SUMS' -Detail "SHA256SUMS missing from $zipFile"
      }

      # The packaged exe is the published (and, when secrets exist, signed) one.
      if ($byPath.ContainsKey('MagicMouseTray.exe') -and (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        $zipExeHash = Get-ZipEntryHash $byPath['MagicMouseTray.exe']
        $publishExeHash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Check -Ok ($zipExeHash -eq $publishExeHash) -Name 'zip exe matches publish' -Detail $(
          if ($zipExeHash -eq $publishExeHash) { $zipExeHash }
          else { "MagicMouseTray.exe differs: zip $zipExeHash, publish $publishExeHash" }
        )
      } else {
        Write-Check -Ok $false -Name 'zip exe matches publish' -Detail 'MagicMouseTray.exe missing from the zip or from publish'
      }
    } finally {
      $archive.Dispose()
    }

    # 9. Published checksum for the download itself.
    $sidecar = "$zip.sha256"
    if (Test-Path -LiteralPath $sidecar -PathType Leaf) {
      $zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
      $sidecarText = (Get-Content -LiteralPath $sidecar -Raw).Trim()
      $sm = [regex]::Match($sidecarText, '^([0-9a-fA-F]{64})\s+\*?(.+)$')
      $sideReasons = @()
      if (-not $sm.Success) {
        $sideReasons += "$(Split-Path -Leaf $sidecar) unparsable: '$sidecarText'"
      } else {
        if ($sm.Groups[1].Value.ToLowerInvariant() -ne $zipHash) {
          $sideReasons += "$zipFile hash mismatch (sidecar $($sm.Groups[1].Value.ToLowerInvariant()), actual $zipHash)"
        }
        if ($sm.Groups[2].Value.Trim() -ne $zipFile) {
          $sideReasons += "sidecar names '$($sm.Groups[2].Value.Trim())' (expect $zipFile)"
        }
      }
      Write-Check -Ok ($sideReasons.Count -eq 0) -Name 'zip sha256 sidecar' -Detail $(
        if ($sideReasons.Count -gt 0) { $sideReasons -join '; ' } else { $zipHash }
      )
    } else {
      Write-Check -Ok $false -Name 'zip sha256 sidecar' -Detail "not found $sidecar"
    }
  }
} catch {
  Write-Host "FAIL  unexpected: $_"
  exit 1
}

if ($failed -gt 0) {
  Write-Host "FAIL  $failed check(s)"
  exit 1
}
Write-Host 'PASS  all checks'
exit 0
