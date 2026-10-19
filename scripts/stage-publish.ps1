#Requires -Version 5

<#
.SYNOPSIS
    Copies the keyboard patch and diagnostic scripts into a publish directory.

.DESCRIPTION
    The portable ZIP's layout is load-bearing: MagicMouseTray.exe resolves the
    keyboard battery unlock and every Diagnostics menu entry at
    "<exe folder>/scripts/<name>", and scripts/package-release.ps1 builds that
    folder from whatever it finds beside the exe. So the set of files staged
    next to the exe IS the contract, and it belongs in one place - both the
    release workflow and the Verify publish job on every PR call this script,
    rather than each keeping its own Copy-Item list to drift out of step.

    Every file is asserted present after the copy, so a rename in the repo
    fails here instead of shipping a ZIP with a Diagnostics entry that does
    nothing.

.PARAMETER PublishDir
    The `dotnet publish` output directory to stage into.

.EXAMPLE
    pwsh -File scripts/stage-publish.ps1 -PublishDir publish
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string] $PublishDir = 'publish'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    throw "Publish directory '$PublishDir' does not exist. Run dotnet publish first."
}

# Source path in the repo -> file name beside the exe.
$staged = [ordered]@{
    'scripts/kbd-patch-cachedservices.ps1'  = 'kbd-patch-cachedservices.ps1'
    'scripts/Install-KeyboardBattery.cmd'   = 'Install-KeyboardBattery.cmd'
    'scripts/capture-state.ps1'             = 'capture-state.ps1'
    'diagnose-driver.ps1'                   = 'diagnose-driver.ps1'
    'scripts/diagnose-and-recover.ps1'      = 'diagnose-and-recover.ps1'
    'scripts/mm-bt-stack-snapshot.ps1'      = 'mm-bt-stack-snapshot.ps1'
    'scripts/repair-magicmouse-channel.ps1' = 'repair-magicmouse-channel.ps1'
}

foreach ($source in $staged.Keys) {
    Copy-Item -LiteralPath $source -Destination (Join-Path $PublishDir $staged[$source])
}

# The exe is produced by dotnet publish, not by this script, but a ZIP without
# it is useless, so assert it here too - this is the one place that states what
# the archive must contain.
$required = @('MagicMouseTray.exe') + $staged.Values
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $PublishDir $_)) })
if ($missing.Count -gt 0) {
    throw "Missing from ${PublishDir}: $($missing -join ', ')"
}

Write-Host "Staged $($staged.Count) script(s) into $PublishDir; all $($required.Count) required file(s) present."
