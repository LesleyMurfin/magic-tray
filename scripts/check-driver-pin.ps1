#Requires -Version 7

<#
.SYNOPSIS
    Checks that the v3 driver package pin in DriverPackageCatalog.cs still
    describes the archive GitHub actually serves.

.DESCRIPTION
    MagicMouseTray/DriverPackageCatalog.cs carries two facts about the package
    the tray downloads, extracts and hands to an elevated cmd.exe:

      V3RepoCommit  - the immutable 40-hex commit the package is taken from.
      V3ZipSha256   - the SHA-256 of the bytes https://github.com/<owner>/
                      <repo>/archive/<commit>.zip serves for that commit.

    The app is fail-closed on the second one: a payload whose digest does not
    match is refused and nothing is extracted or run. That is the right
    behaviour for a tampered or rewritten download, but it means the constant
    itself is load-bearing - a typo in those 64 hex characters, or an upstream
    archive the forge regenerates with different bytes, silently breaks all
    three v3 flows for every installed copy, including the Stock restore a user
    on a patched driver needs to get back.

    `/archive/<sha>.zip` is generated on demand rather than stored, so nothing
    but a download proves the pin still holds. This script is that download: it
    fetches the pinned archive, hashes it, and fails if the result is not the
    digest in the catalogue. Run it in CI and the pin is a checked fact instead
    of a claim in a commit message.

    A transport failure is NOT a pin failure: if the archive cannot be fetched
    after the retries below, the script says so as a ::warning and exits 0,
    because no bytes arrived to contradict the constant. Only bytes that arrive
    and disagree are an error.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of this script's directory.

.EXAMPLE
    pwsh -File scripts/check-driver-pin.ps1
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string] $RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

$catalogRelative = 'MagicMouseTray/DriverPackageCatalog.cs'
$catalogPath = Join-Path -Path $RepoRoot -ChildPath $catalogRelative

function Write-Problem {
    [CmdletBinding()]
    [OutputType([void])]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [int]    $Line,
        [Parameter(Mandatory)] [string] $Message
    )

    Write-Host ('::error file={0},line={1}::{2}' -f $Path, $Line, $Message)
}

function Get-CatalogConstant {
    <#
    .SYNOPSIS
        One `internal const string <Name> = "<value>";` from the catalogue,
        with the line it is declared on. The declaration may wrap, so the
        value is matched on the declaring line or the one after it.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string[]] $Lines,
        [Parameter(Mandatory)] [string]   $Name
    )

    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -notmatch ('const\s+string\s+{0}\s*=' -f [regex]::Escape($Name))) {
            continue
        }
        $window = $Lines[$i]
        if ($i + 1 -lt $Lines.Count) {
            $window = $window + ' ' + $Lines[$i + 1]
        }
        if ($window -match '"([^"]*)"') {
            return @{ Value = $Matches[1]; Line = $i + 1 }
        }
        return @{ Value = ''; Line = $i + 1 }
    }
    return $null
}

if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    Write-Problem -Path $catalogRelative -Line 1 -Message "Cannot read $catalogRelative."
    Write-Host 'Driver pin: FAIL - the catalogue is missing.'
    exit 1
}

$catalogLines = [string[]]@(Get-Content -LiteralPath $catalogPath)

$missing = @()
$constants = @{}
foreach ($name in @('V3RepoOwner', 'V3RepoName', 'V3RepoCommit', 'V3ZipSha256')) {
    $found = Get-CatalogConstant -Lines $catalogLines -Name $name
    if ($null -eq $found -or [string]::IsNullOrWhiteSpace($found.Value)) {
        $missing += $name
        continue
    }
    $constants[$name] = $found
}

if ($missing.Count -gt 0) {
    Write-Problem -Path $catalogRelative -Line 1 -Message (
        'These pin constants are missing or empty: {0}.' -f ($missing -join ', '))
    Write-Host 'Driver pin: FAIL - the catalogue does not carry a usable pin.'
    exit 1
}

$commit = $constants['V3RepoCommit']
$digest = $constants['V3ZipSha256']
$problems = 0

if ($commit.Value -notmatch '^[0-9a-f]{40}$') {
    Write-Problem -Path $catalogRelative -Line $commit.Line -Message (
        'V3RepoCommit is "{0}", which is not a 40-hex commit id. A movable ref must never name the bytes that run elevated.' -f $commit.Value)
    $problems++
}
if ($digest.Value -notmatch '^[0-9a-f]{64}$') {
    Write-Problem -Path $catalogRelative -Line $digest.Line -Message (
        'V3ZipSha256 is "{0}", which is not a 64-hex SHA-256.' -f $digest.Value)
    $problems++
}
if ($problems -gt 0) {
    Write-Host 'Driver pin: FAIL - the pin is malformed; no download was attempted.'
    exit 1
}

$url = 'https://github.com/{0}/{1}/archive/{2}.zip' -f `
    $constants['V3RepoOwner'].Value, $constants['V3RepoName'].Value, $commit.Value
$destination = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath (
    'magic-tray-pin-{0}.zip' -f [guid]::NewGuid().ToString('N'))

Write-Host "Driver pin: fetching $url"

$attempts = 3
$downloaded = $false
$lastError = ''
for ($attempt = 1; $attempt -le $attempts; $attempt++) {
    try {
        Invoke-WebRequest -Uri $url -OutFile $destination -MaximumRedirection 5 -TimeoutSec 120
        $downloaded = $true
        break
    }
    catch {
        $lastError = $_.Exception.Message
        Write-Host ("Driver pin: attempt {0} of {1} failed - {2}" -f $attempt, $attempts, $lastError)
        if ($attempt -lt $attempts) {
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
}

try {
    if (-not $downloaded) {
        # No bytes arrived, so nothing here contradicts the constant. Saying
        # "the pin is wrong" on a network failure would be a claim this run
        # cannot support.
        Write-Host ('::warning file={0},line={1}::Could not download the pinned archive ({2}). The pin was NOT checked on this run.' -f
            $catalogRelative, $digest.Line, $lastError)
        Write-Host 'Driver pin: SKIPPED - the archive could not be downloaded.'
        exit 0
    }

    $actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $digest.Value) {
        Write-Problem -Path $catalogRelative -Line $digest.Line -Message (
            'The pinned archive no longer hashes to V3ZipSha256. Expected {0}, got {1}. Until this is reconciled every v3 driver flow - KMDF, patched Apple and Stock restore - refuses to run for every installed copy.' -f
            $digest.Value, $actual)
        Write-Host 'Driver pin: FAIL - the pinned archive does not match the catalogue digest.'
        exit 1
    }

    Write-Host ('Driver pin: OK - {0}@{1} hashes to {2}.' -f
        $constants['V3RepoName'].Value, $commit.Value, $actual)
    exit 0
}
finally {
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
    }
}
