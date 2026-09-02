<#
.SYNOPSIS
    Snapshot the live BT HID stack + filter chain across ALL paired Apple
    devices. Compares v1 vs v3 Magic Mouse + Apple Keyboard binding state.

    Output: bt-stack-snapshot.json + bt-stack-snapshot.txt (human-readable)
#>
[CmdletBinding()]
param(
    [string]$OutDir = '.'
)

$ErrorActionPreference = 'Continue'

if (-not (Test-Path $OutDir)) { New-Item -Path $OutDir -ItemType Directory -Force -ErrorAction Stop | Out-Null }
$ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'

# All BTHENUM HID-class children
$bthenum = Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'BTHENUM\*' }

# Look up the parent BTH device to get the MAC
$results = @()
foreach ($d in $bthenum) {
    $rec = [ordered]@{
        InstanceId = $d.InstanceId
        FriendlyName = $d.FriendlyName
        Status = $d.Status
        Class = $d.Class
        Manufacturer = $d.Manufacturer
        Service = $d.Service
        ProblemCode = $d.Problem
    }

    # Pull device parameters (LowerFilters, Service overrides) via registry
    # InstanceId format: BTHENUM\Dev_<mac>\<...>  or BTHENUM\{guid}_VID...PID...\<...>
    # The Driver Reg key is HKLM\SYSTEM\CCS\Enum\BTHENUM\<dev>\<inst>
    $enumPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($d.InstanceId.Replace('\','\'))"
    if (Test-Path $enumPath) {
        try {
            $k = Get-Item -LiteralPath $enumPath
            $rec.HardwareID = ($k.GetValue('HardwareID', @()) -join ' | ')
            $rec.Service_REG = $k.GetValue('Service', '')
            $rec.ClassGUID = $k.GetValue('ClassGUID', '')
            # Get Device Parameters\* (where LowerFilters live)
            $dparam = Join-Path $enumPath 'Device Parameters'
            if (Test-Path $dparam) {
                $dpk = Get-Item -LiteralPath $dparam
                $names = $dpk.GetValueNames()
                $params = @{}
                foreach ($n in $names) {
                    $v = $dpk.GetValue($n)
                    if ($v -is [string[]]) {
                        $params[$n] = ($v -join ' | ')
                    } elseif ($v -is [byte[]]) {
                        $params[$n] = ('hex:' + ([System.BitConverter]::ToString($v) -replace '-',' '))
                    } else {
                        $params[$n] = $v
                    }
                }
                $rec.DeviceParameters = $params
            }
            # Driver class node - LowerFilters can be class-level too
            if ($k.GetValue('Driver', '')) {
                $cls = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$($k.GetValue('Driver', ''))"
                if (Test-Path $cls) {
                    $clsk = Get-Item -LiteralPath $cls
                    $cnames = $clsk.GetValueNames()
                    $cparams = @{}
                    foreach ($n in $cnames) {
                        $v = $clsk.GetValue($n)
                        if ($v -is [string[]]) {
                            $cparams[$n] = ($v -join ' | ')
                        } elseif ($v -is [byte[]]) {
                            $cparams[$n] = ('hex:' + ([System.BitConverter]::ToString($v) -replace '-',' '))
                        } else {
                            $cparams[$n] = $v
                        }
                    }
                    $rec.DriverClassParameters = $cparams
                }
            }
        } catch {
            $rec.RegistryReadError = $_.ToString()
        }
    } else {
        $rec.NoEnumKey = $true
    }
    $results += [pscustomobject]$rec
}

# Also enumerate the Mouse class children (where the filtered HID wraps to Mouse)
$mouseChildren = Get-PnpDevice -Class Mouse -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'HID\*' }

# Snapshot tray-relevant pieces
$batteryReadings = @()
foreach ($d in $bthenum) {
    try {
        # Get-PnpDeviceProperty DEVPKEY_Device_BatteryLevel (255 if N/A)
        $bp = Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_BatteryLevel' -ErrorAction SilentlyContinue
        $val = if ($bp) { $bp.Data } else { $null }
        $batteryReadings += [pscustomobject]@{
            InstanceId = $d.InstanceId
            FriendlyName = $d.FriendlyName
            BatteryLevel_DEVPKEY = $val
        }
    } catch {}
}

# applewirelessmouse service
$svc = Get-Service -Name 'applewirelessmouse' -ErrorAction SilentlyContinue
$svcInfo = @{
    Exists = ($null -ne $svc)
    Status = if ($svc) { $svc.Status.ToString() } else { 'absent' }
    StartType = if ($svc) { $svc.StartType.ToString() } else { '' }
}
# Driver file presence
$driverPath = "$env:SystemRoot\System32\drivers\applewirelessmouse.sys"
$svcInfo.DriverFileExists = (Test-Path $driverPath)
if (Test-Path $driverPath) {
    $fi = Get-Item -LiteralPath $driverPath
    $svcInfo.DriverFileSize = $fi.Length
    $svcInfo.DriverFileModified = $fi.LastWriteTime.ToString('o')
}

# applewirelessmouse INF lookup (which PIDs does it match?)
$infs = Get-WindowsDriver -Online -ErrorAction SilentlyContinue |
    Where-Object { $_.OriginalFileName -like '*applewirelessmouse*' -or $_.Driver -like '*applewirelessmouse*' }

$snapshot = [ordered]@{
    Captured = $ts
    BTHENUMHIDChildren = $results
    MouseClassChildren = ($mouseChildren | Select-Object InstanceId, FriendlyName, Status, ProblemCode, Service)
    BatteryReadings = $batteryReadings
    AppleFilterService = $svcInfo
    AppleFilterINF = ($infs | Select-Object Driver, OriginalFileName, Inbox, ClassName, ProviderName, Date, Version, BootCritical)
}

$jsonOut = Join-Path $OutDir 'bt-stack-snapshot.json'
$txtOut = Join-Path $OutDir 'bt-stack-snapshot.txt'
$snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonOut -Encoding UTF8 -ErrorAction Stop

# Human-readable summary
$lines = @()
$lines += "=== BT HID stack snapshot @ $ts ==="
$lines += ""
$lines += "applewirelessmouse service:"
$lines += "  Exists: $($svcInfo.Exists)  Status: $($svcInfo.Status)  StartType: $($svcInfo.StartType)"
$lines += "  DriverFileExists: $($svcInfo.DriverFileExists)  Size: $($svcInfo.DriverFileSize)  Modified: $($svcInfo.DriverFileModified)"
$lines += ""
$lines += "applewirelessmouse INF entries:"
foreach ($i in $infs) {
    $lines += "  $($i.Driver) | $($i.OriginalFileName) | Inbox=$($i.Inbox)"
}
$lines += ""
$lines += "BTHENUM HIDClass children:"
foreach ($r in $results) {
    $lines += ""
    $lines += "  [$($r.Status)] $($r.InstanceId)"
    $lines += "    FriendlyName: $($r.FriendlyName)"
    $lines += "    Service: $($r.Service)  ServiceREG: $($r.Service_REG)"
    $lines += "    Manufacturer: $($r.Manufacturer)"
    if ($r.HardwareID) { $lines += "    HardwareID: $($r.HardwareID)" }
    if ($r.DeviceParameters) {
        $lf = $r.DeviceParameters['LowerFilters']
        $uf = $r.DeviceParameters['UpperFilters']
        if ($lf) { $lines += "    LowerFilters: $lf" }
        if ($uf) { $lines += "    UpperFilters: $uf" }
        foreach ($k in $r.DeviceParameters.Keys) {
            if ($k -notin 'LowerFilters','UpperFilters') {
                $lines += "    DevParam.$k = $($r.DeviceParameters[$k])"
            }
        }
    }
    if ($r.DriverClassParameters) {
        $clf = $r.DriverClassParameters['LowerFilters']
        if ($clf) { $lines += "    DriverClass.LowerFilters: $clf" }
    }
}
$lines += ""
$lines += "Battery readings (DEVPKEY_Device_BatteryLevel):"
foreach ($b in $batteryReadings) {
    $lines += "  $($b.FriendlyName) -> $($b.BatteryLevel_DEVPKEY)"
}

$lines | Set-Content -LiteralPath $txtOut -Encoding UTF8 -ErrorAction Stop

Write-Host "[bt-snapshot] OK -> $jsonOut" -ForegroundColor Green
Write-Host "[bt-snapshot] OK -> $txtOut" -ForegroundColor Green
exit 0
