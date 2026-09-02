#Requires -Version 5
# Gate a Magic Tray publish folder: required artifacts, metadata, and SHA256SUMS.
# Exit 0 on pass, 1 on any failure. Does not call gh or create a release.
param(
  [Parameter(Mandatory)][string]$PublishDir,
  [string]$Tag = $env:GITHUB_REF_NAME,
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
  $shipNames = @($exeName, $ps1Name, $cmdName)
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
