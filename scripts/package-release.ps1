#Requires -Version 5
# Build the Magic Tray portable ZIP: MagicTray-<tag>-win-x64.zip.
#
# The tray resolves the keyboard battery patch and the Diagnostics menu scripts
# relative to AppContext.BaseDirectory (DriverInstaller.FindKeyboardPatchScript,
# DiagnosticScripts.Find). Probe 1 in both is "<exe dir>/scripts/<name>", so the
# ZIP puts the exe at the root and every script in a sibling scripts/ folder.
# Someone who unzips the whole folder and double-clicks the exe gets a working
# "Fix battery reads" and a working Diagnostics menu.
#
# Emits:
#   <OutDir>/MagicTray-<tag>-win-x64.zip
#   <OutDir>/MagicTray-<tag>-win-x64.zip.sha256
#   <OutDir>/SHA256SUMS                          (one line per uploaded asset)
# Staging happens in <OutDir>/zip-stage, which is wiped first so no stale file
# can ride along into the archive.
[CmdletBinding()]
param(
  # dotnet publish output holding MagicMouseTray.exe.
  [Parameter(Mandatory)][string]$PublishDir,
  # Release tag, e.g. v1.1.0. Defaults to csproj <Version> prefixed with v.
  [string]$Tag = $env:GITHUB_REF_NAME,
  [string]$OutDir = 'dist',
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

# Relative path inside the ZIP -> source path relative to the repo root.
# diagnose-driver.ps1 lives in the repo root but ships under scripts/ so probe 1
# resolves it; DiagnosticScripts.Find checks "<exe dir>/scripts/<name>".
$ScriptPayload = [ordered]@{
  'scripts/kbd-patch-cachedservices.ps1' = 'scripts/kbd-patch-cachedservices.ps1'
  'scripts/Install-KeyboardBattery.cmd'  = 'scripts/Install-KeyboardBattery.cmd'
  'scripts/capture-state.ps1'            = 'scripts/capture-state.ps1'
  'scripts/diagnose-driver.ps1'          = 'diagnose-driver.ps1'
  'scripts/diagnose-and-recover.ps1'     = 'scripts/diagnose-and-recover.ps1'
  'scripts/mm-bt-stack-snapshot.ps1'     = 'scripts/mm-bt-stack-snapshot.ps1'
}

function Get-CsprojVersion {
  param([Parameter(Mandatory)][string]$Path)
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "csproj not found: $Path" }
  [xml]$xml = Get-Content -LiteralPath $Path -Raw
  $versions = @(@($xml.Project.PropertyGroup) | ForEach-Object { $_.Version } | Where-Object { $_ })
  if ($versions.Count -eq 0) { throw "no <Version> in $Path" }
  return [string]$versions[0]
}

# Checksum files are LF even on a Windows runner. `sha256sum -c` takes a
# trailing CR as part of the filename and then cannot find the file, and
# packaging/winget/README.md tells people to run exactly that. (README.txt is
# the deliberate exception below: it is CRLF because it opens in Notepad.)
function Write-SumsFile {
  param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string[]]$Lines
  )
  [IO.File]::WriteAllText($Path, (($Lines -join "`n") + "`n"), [Text.Encoding]::ASCII)
}

$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
  throw "publish dir not found: $PublishDir"
}
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path

$csprojVersion = Get-CsprojVersion (Join-Path $RepoRoot (Join-Path 'MagicMouseTray' 'MagicMouseTray.csproj'))
if ([string]::IsNullOrWhiteSpace($Tag)) { $Tag = "v$csprojVersion" }
# Version shown to the reader: strip a leading v from a real vX.Y.Z tag.
$tagMatch = [regex]::Match([string]$Tag, '^v(\d+\.\d+\.\d+)$')
$displayVersion = if ($tagMatch.Success) { $tagMatch.Groups[1].Value } else { $csprojVersion }

$zipName = "MagicTray-$Tag-win-x64.zip"

if (-not (Test-Path -LiteralPath $OutDir -PathType Container)) {
  New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$stage = Join-Path $OutDir 'zip-stage'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stage 'scripts') -Force | Out-Null

# 1. The exe, at the ZIP root. Copy from publish so a signed exe is picked up.
$exeSource = Join-Path $PublishDir 'MagicMouseTray.exe'
if (-not (Test-Path -LiteralPath $exeSource -PathType Leaf)) {
  throw "MagicMouseTray.exe not found in publish dir: $exeSource"
}
Copy-Item -LiteralPath $exeSource -Destination (Join-Path $stage 'MagicMouseTray.exe') -Force

# 2. Every script, under scripts/.
foreach ($entry in $ScriptPayload.GetEnumerator()) {
  $source = Join-Path $RepoRoot ($entry.Value -replace '/', [IO.Path]::DirectorySeparatorChar)
  if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "payload source missing: $($entry.Value) (needed for ZIP entry $($entry.Key))"
  }
  if ((Get-Item -LiteralPath $source).Length -le 0) {
    throw "payload source is empty: $($entry.Value)"
  }
  $dest = Join-Path $stage ($entry.Key -replace '/', [IO.Path]::DirectorySeparatorChar)
  Copy-Item -LiteralPath $source -Destination $dest -Force
}

# 3. README.txt, rendered from the template with the shipped version.
$templatePath = Join-Path $RepoRoot (Join-Path 'packaging' 'zip-readme.txt')
if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
  throw "README template missing: $templatePath"
}
$readme = (Get-Content -LiteralPath $templatePath -Raw).Replace('{{VERSION}}', $displayVersion)
# CRLF: this is read in Notepad on Windows.
$readme = ($readme -replace "`r`n", "`n") -replace "`n", "`r`n"
[IO.File]::WriteAllText((Join-Path $stage 'README.txt'), $readme, (New-Object Text.UTF8Encoding $false))

# 4. SHA256SUMS over every staged file, generated. Sorted for a stable file.
$staged = @(
  Get-ChildItem -LiteralPath $stage -Recurse -File |
    ForEach-Object { $_.FullName.Substring($stage.Length + 1) -replace '\\', '/' } |
    Sort-Object
)
$sumLines = foreach ($rel in $staged) {
  $hash = (Get-FileHash -LiteralPath (Join-Path $stage ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)) -Algorithm SHA256).Hash.ToLowerInvariant()
  '{0}  {1}' -f $hash, $rel
}
Write-SumsFile -Path (Join-Path $stage 'SHA256SUMS') -Lines $sumLines

# 5. Zip it. ZipFile writes '/' separators and root-level entries, which is what
# the layout contract and the winget NestedInstallerFiles path both need.
$zipPath = Join-Path $OutDir $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
[IO.Compression.ZipFile]::CreateFromDirectory(
  $stage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

# 6. Standalone checksum for the ZIP itself, so people can check the download.
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-SumsFile -Path "$zipPath.sha256" -Lines @('{0}  {1}' -f $zipHash, $zipName)

# 7. SHA256SUMS over every file the release uploads, so someone who downloads a
# single asset can check that one on its own. Bare filenames, lower-case hex,
# two spaces: the format `sha256sum -c` reads, and the one a Get-FileHash result
# can be compared against by eye (Get-FileHash prints the same digest in upper
# case). The loose entries hash the publish copies the workflow attaches, not
# the repo sources, so a signed exe is the one described here.
$assetNames = @('MagicMouseTray.exe') + @($ScriptPayload.Keys | ForEach-Object { Split-Path -Leaf $_ })
$sidecarName = "$zipName.sha256"
$sidecarPath = "$zipPath.sha256"
$sidecarHash = (Get-FileHash -LiteralPath $sidecarPath -Algorithm SHA256).Hash.ToLowerInvariant()
$assetHashes = [ordered]@{
  $zipName     = $zipHash
  $sidecarName = $sidecarHash
}
foreach ($name in $assetNames) {
  $assetPath = Join-Path $PublishDir $name
  if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
    throw "release asset not found in publish dir: $assetPath"
  }
  $assetHashes[$name] = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$sumsPath = Join-Path $OutDir 'SHA256SUMS'
Write-SumsFile -Path $sumsPath -Lines @(
  $assetHashes.GetEnumerator() | ForEach-Object { '{0}  {1}' -f $_.Value, $_.Key })

# The exe is the asset most people click, and the release notes quote its digest
# and length so nobody has to fetch a second file to check the one they got.
$exeHash = [string]$assetHashes['MagicMouseTray.exe']
$exeBytes = (Get-Item -LiteralPath $exeSource).Length
$zipBytes = (Get-Item -LiteralPath $zipPath).Length

Write-Host "packaged $zipPath"
Write-Host "sha256   $zipHash"
Write-Host "sums     $sumsPath"
Write-Host "exe      $exeHash"
Write-Host "exe size $exeBytes bytes"
# $staged was captured before SHA256SUMS was written, so list it explicitly:
# the log should name every entry the archive actually carries.
Write-Host 'contents:'
foreach ($rel in @($staged + 'SHA256SUMS' | Sort-Object)) { Write-Host "  $rel" }

# Machine-readable handoff for the workflow step.
if ($env:GITHUB_OUTPUT) {
  Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "zip=$zipPath"
  Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "zip_name=$zipName"
  Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "zip_sha256=$zipHash"
  Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "zip_bytes=$zipBytes"
  Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "sums=$sumsPath"
  Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "exe_sha256=$exeHash"
  Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "exe_bytes=$exeBytes"
}
exit 0
