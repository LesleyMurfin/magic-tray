#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Apple Wireless Mouse driver diagnostic script.
    Run from PowerShell as Administrator.
#>

$driverName = "applewirelessmouse"
$sysFile    = "C:\Windows\System32\drivers\$driverName.sys"
$logDir     = [Environment]::GetFolderPath('Desktop')
if ([string]::IsNullOrWhiteSpace($logDir) -or -not (Test-Path -LiteralPath $logDir -PathType Container)) {
    $logDir = [System.IO.Path]::GetTempPath()
}
$logFile    = Join-Path $logDir 'apple-mouse-diag.txt'

function Write-Log {
    param([string]$text)
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $text"
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

function Write-Section {
    param([string]$title)
    $bar = "=" * 60
    Write-Log ""
    Write-Log $bar
    Write-Log "  $title"
    Write-Log $bar
}

function Test-AdministratorOwnedFile {
    param([string]$Path)
    try {
        $dir = Split-Path -Parent $Path
        $acl = Get-Acl -LiteralPath $dir
        $owner = $acl.Owner
        return ($owner -like '*\SYSTEM' -or $owner -like '*\Administrators')
    } catch {
        return $false
    }
}

function Get-TrustedSysinternalsExe {
    param([string]$FileName)
    $pf86 = ${env:ProgramFiles(x86)}
    $candidates = @(
        "C:\Tools\$FileName",
        "C:\Sysinternals\$FileName",
        (Join-Path $env:ProgramFiles "Sysinternals\$FileName")
    )
    if ($pf86) {
        $candidates += (Join-Path $pf86 "Sysinternals\$FileName")
    }
    foreach ($path in $candidates) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        if (-not (Test-AdministratorOwnedFile $path)) { continue }
        $sig = Get-AuthenticodeSignature -LiteralPath $path
        if ($sig.Status -ne 'Valid') { continue }
        # A valid chain alone proves nothing: require a Microsoft/Sysinternals
        # publisher before we execute the binary.
        $signer = $sig.SignerCertificate
        if (-not $signer) { continue }
        if ($signer.Subject -notmatch 'O=(Microsoft Corporation|Sysinternals)') { continue }
        return $path
    }
    return $null
}

# Start fresh log
"" | Set-Content $logFile
Write-Log "Apple Wireless Mouse Driver Diagnostic"
Write-Log "Date: $(Get-Date)"

# ── 1. Driver .sys file ──────────────────────────────────────
Write-Section "1. Driver .sys file"
if (Test-Path $sysFile) {
    $file = Get-Item $sysFile
    Write-Log "FOUND: $sysFile"
    Write-Log "  Size:     $($file.Length) bytes"
    Write-Log "  Modified: $($file.LastWriteTime)"

    # Authenticode signature (built-in, no Sysinternals needed)
    $sig = Get-AuthenticodeSignature $sysFile
    Write-Log "  Signature status: $($sig.Status)"
    Write-Log "  Signer:           $($sig.SignerCertificate.Subject)"
} else {
    Write-Log "NOT FOUND: $sysFile  <-- driver binary is missing"
}

# ── 2. Service config ────────────────────────────────────────
Write-Section "2. Service config (sc qc)"
$scOutput = & sc.exe qc $driverName 2>&1
$scOutput | ForEach-Object { Write-Log "  $_" }

Write-Section "2b. Service state (sc query)"
$scQuery = & sc.exe query $driverName 2>&1
$scQuery | ForEach-Object { Write-Log "  $_" }

# ── 3. Autorunsc (Unicode-safe via Select-String) ────────────
Write-Section "3. Autoruns - registered driver entries"
$autorunsPath = Get-TrustedSysinternalsExe 'autorunsc.exe'

if ($autorunsPath) {
    Write-Log "Using: $autorunsPath"
    # -nobanner suppresses header noise; pipe to Select-String handles Unicode
    # One enumeration is slow; run it once and reuse the captured output.
    $autorunsOutput = @(& $autorunsPath -accepteula -nobanner -a d 2>&1)
    $autorunsOutput |
        Select-String -Pattern "apple" -CaseSensitive:$false |
        ForEach-Object { Write-Log "  $_" }

    Write-Log ""
    Write-Log "--- Full driver list (all entries) ---"
    $autorunsOutput | ForEach-Object { Write-Log "  $_" }
} else {
    Write-Log "autorunsc.exe not found in common paths. Skipping."
    Write-Log "Download from: https://learn.microsoft.com/sysinternals/downloads/autoruns"
}

# ── 4. Sigcheck (if available) ───────────────────────────────
Write-Section "4. Sigcheck - deep signature verification"
$sigcheckPath = Get-TrustedSysinternalsExe 'sigcheck.exe'

if ($sigcheckPath -and (Test-Path $sysFile)) {
    & $sigcheckPath -accepteula -i -a $sysFile 2>&1 |
        ForEach-Object { Write-Log "  $_" }
} elseif (-not $sigcheckPath) {
    Write-Log "sigcheck.exe not found. Skipping."
} else {
    Write-Log "Driver .sys not present - skipping sigcheck."
}

# ── 5. pnputil device info ───────────────────────────────────
Write-Section "5. PnP devices - Apple HID/BT entries"
& pnputil /enum-devices 2>&1 |
    Select-String -Pattern "apple|00001124-0000-1000-8000-00805f9b34fb" -CaseSensitive:$false |
    ForEach-Object { Write-Log "  $_" }

Write-Section "5b. INF package info (Apple / applewirelessmouse)"
$driverText = (& pnputil /enum-drivers 2>&1 | Out-String)
$driverBlocks = [regex]::Split($driverText, '(?mi)(?=^\s*Published Name\s*:)')
$matchedPackage = $false
foreach ($block in $driverBlocks) {
    if ($block -notmatch '(?im)Provider Name\s*:.*Apple|Original Name\s*:.*apple') {
        continue
    }
    $matchedPackage = $true
    foreach ($line in ($block -split '\r?\n')) {
        if ($line.Trim().Length -gt 0) {
            Write-Log "  $($line.TrimEnd())"
        }
    }
    Write-Log ""
}
if (-not $matchedPackage) {
    Write-Log "  (no Apple / applewirelessmouse driver packages found)"
}

# ── 6. Windows Event Log errors ──────────────────────────────
Write-Section "6. System event log - driver/service errors (last 48h)"
$since = (Get-Date).AddHours(-48)
Get-WinEvent -FilterHashtable @{
    LogName   = 'System'
    StartTime = $since
    Id        = @(7000, 7001, 7009, 7011, 7022, 7023, 7026, 7034, 7043)
} -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Message -match "apple|wirelessmouse|hid" -or $_.ProviderName -match "apple"
    } |
    Sort-Object TimeCreated |
    ForEach-Object {
        Write-Log "  [$($_.TimeCreated)] ID=$($_.Id) - $($_.Message -replace '\s+',' ')"
    }

Write-Section "7. Registry - LowerFilters entry"
$hidClassRoot = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{745a17a0-74d3-11d0-b6fe-00a0c90f57da}"
try {
    $instances = @(Get-ChildItem -LiteralPath $hidClassRoot -ErrorAction Stop |
        Where-Object { $_.PSChildName -match '^\d{4}$' })
    if ($instances.Count -eq 0) {
        Write-Log "  No numbered HID class instances found"
    }
    foreach ($inst in $instances) {
        $lf = $inst.GetValue('LowerFilters')
        $desc = $inst.GetValue('DriverDesc')
        if ($null -eq $lf) {
            Write-Log "  $($inst.PSChildName) ($desc): (no LowerFilters)"
        } else {
            Write-Log "  $($inst.PSChildName) ($desc): $($lf -join ', ')"
        }
    }
} catch {
    Write-Log "  Could not read HID class registry: $_"
}

# ── Done ─────────────────────────────────────────────────────
Write-Section "DONE"
Write-Log "Full log saved to: $logFile"
Write-Host ""
Write-Host "Log written to: $logFile" -ForegroundColor Cyan
