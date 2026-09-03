# capture-state.ps1 - Snapshot Magic Mouse device state for reboot comparison
# Run before a reboot, then again after, then compare.
#
# Usage:
#   .\capture-state.ps1 -Label "pre-reboot"         -> saves state-pre-reboot.json
#   .\capture-state.ps1 -Label "post-reboot-1"      -> saves state-post-reboot-1.json
#   .\capture-state.ps1 -Compare .\state-pre-reboot.json .\state-post-reboot-1.json
#
# Saved to current directory unless -OutputDir is specified.
# Run elevated (some queries need admin for sc.exe and registry reads).

[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Label = "",
    [switch]$Compare,
    [Parameter(Position = 0)]
    [string]$FileA = "",
    [Parameter(Position = 1)]
    [string]$FileB = "",
    [string]$OutputDir = "."
)

Set-StrictMode -Version 2

# ---- Compare mode ----
if ($Compare) {
    if (-not $FileA -or -not $FileB) {
        Write-Host "Usage: capture-state.ps1 -Compare <fileA.json> <fileB.json>" -ForegroundColor Yellow
        exit 1
    }

    $a = Get-Content $FileA -Raw | ConvertFrom-Json
    $b = Get-Content $FileB -Raw | ConvertFrom-Json

    function Assert-StateSnapshot {
        param($Obj, [string]$Path)
        if ($null -eq $Obj) {
            throw "Invalid snapshot: $Path is empty."
        }
        $required = @(
            'label', 'timestamp',
            'col01Present', 'col02Present', 'filterInStack',
            'lowerFiltersEnumKey', 'lowerFiltersDriverKey',
            'serviceState', 'hidDeviceCount', 'startupRepairLog'
        )
        foreach ($name in $required) {
            if ($null -eq $Obj.PSObject.Properties[$name]) {
                throw "Invalid snapshot ${Path}: missing property '$name'."
            }
        }
        foreach ($name in @('col01Present', 'col02Present', 'filterInStack')) {
            $val = $Obj.$name
            if ($null -ne $val -and $val -isnot [bool]) {
                throw "Invalid snapshot ${Path}: '$name' must be boolean."
            }
        }
        $count = $Obj.hidDeviceCount
        if ($null -ne $count -and -not (
                $count -is [int] -or $count -is [long] -or
                $count -is [decimal] -or $count -is [double])) {
            throw "Invalid snapshot ${Path}: 'hidDeviceCount' must be a number."
        }
        if ($null -ne $Obj.serviceState -and $Obj.serviceState -isnot [string]) {
            throw "Invalid snapshot ${Path}: 'serviceState' must be a string."
        }
    }

    try {
        Assert-StateSnapshot $a $FileA
        Assert-StateSnapshot $b $FileB
    } catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
        exit 1
    }

    Write-Host ""
    Write-Host "=== STATE COMPARISON ===" -ForegroundColor Cyan
    Write-Host "  A: $($a.label)  [$($a.timestamp)]"
    Write-Host "  B: $($b.label)  [$($b.timestamp)]"
    Write-Host ""

    function Diff-Field {
        param($Name, $ValA, $ValB, [switch]$GoodIfTrue, [switch]$GoodIfEqual)
        $changed = ($ValA -ne $ValB) -and ($null -ne $ValA) -and ($null -ne $ValB)
        if ($changed) {
            $color = "Yellow"
            if ($GoodIfTrue  -and $ValB) { $color = "Green" }
            if ($GoodIfTrue  -and !$ValB) { $color = "Red" }
            if ($GoodIfEqual) { $color = "Yellow" }
            Write-Host "  CHANGED  $Name" -ForegroundColor $color
            Write-Host "           A: $ValA"
            Write-Host "           B: $ValB"
        } else {
            Write-Host "  same     $Name = $ValA"
        }
    }

    Diff-Field "col01Present"           $a.col01Present          $b.col01Present          -GoodIfTrue
    Diff-Field "col02Present"           $a.col02Present          $b.col02Present          -GoodIfTrue
    Diff-Field "filterInStack"          $a.filterInStack         $b.filterInStack
    Diff-Field "lowerFiltersEnumKey"    ($a.lowerFiltersEnumKey  -join ',') ($b.lowerFiltersEnumKey  -join ',') -GoodIfEqual
    Diff-Field "lowerFiltersDriverKey"  ($a.lowerFiltersDriverKey -join ',') ($b.lowerFiltersDriverKey -join ',') -GoodIfEqual
    Diff-Field "serviceState"           $a.serviceState          $b.serviceState
    Diff-Field "hidDeviceCount"         $a.hidDeviceCount        $b.hidDeviceCount        -GoodIfTrue

    Write-Host ""
    Write-Host "=== STARTUP-REPAIR LOG (B only - last 10 lines) ===" -ForegroundColor Cyan
    if ($b.startupRepairLog) {
        $b.startupRepairLog | Select-Object -Last 10 | ForEach-Object { Write-Host "  $_" }
    } else {
        Write-Host "  (no log data in B)"
    }
    Write-Host ""
    exit 0
}

# ---- Capture mode ----
if (-not $Label) {
    $Label = "capture-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Write-Host "No -Label specified, using: $Label" -ForegroundColor Yellow
}

$mmPid = "0323"
$state = [ordered]@{
    label              = $Label
    timestamp          = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    col01Present       = $false
    col02Present       = $false
    filterInStack      = $false
    hidDeviceCount     = 0
    hidDevices         = @()
    bthenumInstanceId  = ""
    lowerFiltersEnumKey    = @()
    lowerFiltersDriverKey  = @()
    serviceState       = ""
    startupRepairLog   = @()
}

# Find BTHENUM parent
$btDev = Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match 'BTHENUM' -and
                   $_.InstanceId -match '00001124' -and
                   $_.InstanceId -match "_PID&$mmPid" -and
                   $_.Status -eq 'OK' } |
    Select-Object -First 1

if ($btDev) {
    $state.bthenumInstanceId = $btDev.InstanceId

    # Driver stack
    try {
        $stackProp = Get-PnpDeviceProperty -InstanceId $btDev.InstanceId `
            -KeyName 'DEVPKEY_Device_Stack' -ErrorAction SilentlyContinue
        if ($stackProp -and $stackProp.Data) {
            $stackStr = $stackProp.Data -join ' '
            $state.filterInStack = $stackStr -imatch 'applewirelessmouse'
        }
    } catch {}

    # LowerFilters - Enum key
    $btRegPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\" + $btDev.InstanceId
    try {
        $lf = (Get-ItemProperty -Path $btRegPath -Name LowerFilters -ErrorAction SilentlyContinue).LowerFilters
        if ($lf) { $state.lowerFiltersEnumKey = @($lf) }
    } catch {}

    # LowerFilters - driver instance key
    try {
        $driverKey = (Get-ItemProperty -Path $btRegPath -Name Driver -ErrorAction SilentlyContinue).Driver
        if ($driverKey) {
            $driverInstPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$driverKey"
            $lf2 = (Get-ItemProperty -Path $driverInstPath -Name LowerFilters -ErrorAction SilentlyContinue).LowerFilters
            if ($lf2) { $state.lowerFiltersDriverKey = @($lf2) }
        }
    } catch {}
}

# HID devices with this PID
$hidDevices = Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue |
    Where-Object {
        $_.InstanceId -match ('^HID\\VID_05AC&PID_' + $mmPid) -and
        $_.Status -eq 'OK'
    }
$hidList = @($hidDevices)
$state.hidDeviceCount = $hidList.Count
$state.hidDevices     = $hidList | ForEach-Object { @{ instanceId = $_.InstanceId; status = $_.Status } }
$state.col01Present   = @($hidList | Where-Object { $_.InstanceId -match 'COL01' }).Count -gt 0
$state.col02Present   = @($hidList | Where-Object { $_.InstanceId -match 'COL02' }).Count -gt 0

# Service state
try {
    $scOut = & sc.exe query applewirelessmouse 2>&1
    $state.serviceState = ($scOut | Where-Object { $_ -match 'STATE' } | Select-Object -First 1).Trim()
} catch {}

# startup-repair.log
$logFile = "C:\ProgramData\MagicMouseTray\startup-repair.log"
if (Test-Path $logFile) {
    $state.startupRepairLog = @(Get-Content $logFile -Tail 20)
}

# Save JSON
if (-not (Test-Path -LiteralPath $OutputDir -PathType Container)) {
    New-Item -ItemType Directory -Path $OutputDir -Force -ErrorAction Stop | Out-Null
}
$outFile = Join-Path $OutputDir "state-$Label.json"
$state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $outFile -Encoding UTF8 -ErrorAction Stop

# Print summary
Write-Host ""
Write-Host "=== STATE CAPTURED: $Label ===" -ForegroundColor Cyan
Write-Host "  Timestamp:      $($state.timestamp)"
Write-Host "  COL01 (scroll): $($state.col01Present)" -ForegroundColor $(if ($state.col01Present) {"Green"} else {"Red"})
Write-Host "  COL02 (battery):$($state.col02Present)" -ForegroundColor $(if ($state.col02Present) {"Green"} else {"Red"})
Write-Host "  Filter in stack:$($state.filterInStack)"
Write-Host "  LowerFilters(Enum):   $($state.lowerFiltersEnumKey -join ', ')"
Write-Host "  LowerFilters(Driver): $($state.lowerFiltersDriverKey -join ', ')"
Write-Host "  Service state:  $($state.serviceState)"
Write-Host "  HID device count: $($state.hidDeviceCount)"
if ($state.startupRepairLog.Count -gt 0) {
    Write-Host "  Last log entry: $($state.startupRepairLog | Select-Object -Last 1)"
}
Write-Host ""
Write-Host "Saved: $outFile" -ForegroundColor Green
Write-Host ""
Write-Host "Workflow:" -ForegroundColor Yellow
Write-Host "  1. Run this NOW (before reboot):  capture-state.ps1 -Label pre-reboot"
Write-Host "  2. Reboot"
Write-Host "  3. Run after boot:                capture-state.ps1 -Label post-reboot-1"
Write-Host "  4. Compare:                       capture-state.ps1 -Compare .\state-pre-reboot.json .\state-post-reboot-1.json"
