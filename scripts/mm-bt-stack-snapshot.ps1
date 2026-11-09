<#
.SYNOPSIS
    Snapshot the live BT HID stack + filter chain across ALL paired Apple
    devices. Compares v1 vs v3 Magic Mouse + Apple Keyboard binding state.

    Output: bt-stack-snapshot.json + bt-stack-snapshot.txt (human-readable)

    Both files are written through a redaction sink: Bluetooth addresses are
    replaced with <mac>, a USB iSerialNumber with <serial>, and the words of a
    FriendlyName that are not device model words with <name>. That is not the
    same as safe to paste. The sink covers those three things only; every other
    registry value this script copies out is written exactly as it was read.
    Read the file before you attach it.
#>
[CmdletBinding()]
param(
    [string]$OutDir = '.'
)

$ErrorActionPreference = 'Continue'

# --- Redaction ---------------------------------------------------------------
# SECURITY.md names this script in its reporting scope, so people are pointed at
# it - and every BTHENUM InstanceId carries the device's Bluetooth address while
# a paired FriendlyName routinely carries the owner's given name. Both are
# scrubbed before anything reaches the .json or the .txt.
#
# DUPLICATED on purpose, not dot-sourced. ConvertTo-MacSafeText below is the
# same function as the one in mm-hid-report.ps1. A shared helper file cannot be
# reached from every layout this script ships in: stage-publish.ps1 copies it
# FLAT beside MagicMouseTray.exe, .github/workflows/release.yml uploads that
# flat copy as a standalone release asset, and package-release.ps1 puts it under
# scripts/ inside the ZIP. Someone who downloads the single .ps1 asset has no
# sibling helper at all, so a dot-source would either abort a script that works
# today or run on with no sink - a worse outcome than the leak it replaced,
# because the sink would be claimed and absent. The cost of duplicating is
# drift, so the rule is: the three address rules are shared with
# MagicMouseTray/Logger.cs (RedactMacs) and MagicMouseTray/BugReport.cs. They
# are not to be reordered, reworded or widened here. Change them there first.

# 12-hex runs that are the SAME on every Windows PC, so they are not anybody's
# address and redacting them only destroys the fields a maintainer reads first.
# This is a WHITELIST of named constants, never a pattern that tries to
# recognise an address: a real address can look like anything, so the default
# stays redact.
#   00805f9b34fb  Bluetooth SIG base UUID tail, carried by every BTHENUM service
#                 GUID in these paths ({00001124-...} HID, {00001200-...} PnP
#                 information) and by the HardwareID copied out of the Enum key
#   001111000030  tail of the HID device interface class GUID
#                 {4d1e55b2-f16f-11cf-88cb-001111000030}, carved out by
#                 mm-hid-report.ps1 for the same reason
#   00a0c90f57da  tail of the HIDClass setup class GUID
#                 {745a17a0-74d3-11d0-b6fe-00a0c90f57da}. Only this script
#                 writes it, as ClassGUID; redacting it would leave the record
#                 unable to say which setup class the node is in. mm-hid-report
#                 does not carry this entry because it never emits that value.
$script:MacSafeConstants = @('00805f9b34fb', '001111000030', '00a0c90f57da')

# The rules of Logger.RedactMacs, in Logger.RedactMacs' order - the punctuated
# pair forms, the Dev_ form, then the bare 12-hex belt with the constants above
# carved out - followed by the fourth rule mm-hid-report.ps1 adds: the third
# node of a USB instance id is the unit's iSerialNumber, which is not a MAC
# shape and so matched none of the three. It runs LAST and consumes an
# already-redacted <mac> token only when that token is the WHOLE node, so a
# serial that is the unit's address with the separators stripped keeps the <mac>
# spelling and still correlates with debug.log. This script enumerates BTHENUM
# and HID nodes rather than USB ones, so that rule fires here only on a registry
# value that quotes a USB path; it is kept so the function is one function with
# one behaviour in all three scripts.
function ConvertTo-MacSafeText {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $t = [regex]::Replace($Text, '(?i)(?<![0-9a-f])([0-9a-f]{2}[:-]){5}[0-9a-f]{2}(?![0-9a-f])', '<mac>')
    $t = [regex]::Replace($t, '(?i)Dev_[0-9a-f]{12}', 'Dev_<mac>')
    $t = [regex]::Replace($t, '(?i)(?<![0-9a-f])[0-9a-f]{12}(?![0-9a-f])', {
        param($m)
        if ($script:MacSafeConstants -contains $m.Value.ToLowerInvariant()) { return $m.Value }
        return '<mac>'
    })
    return [regex]::Replace($t, '(?i)(USB\\VID_[0-9a-f]{4}&PID_[0-9a-f]{4}(&MI_[0-9a-f]{2})?\\)(<mac>)?(?:(?!#\{)[^<\\\s,;\)\]])+', '$1<serial>')
}

# Words Windows and Apple put in a device FriendlyName. Same policy as
# $MacSafeConstants and for the same reason: a whitelist of constants, default
# redact, never a pattern that tries to recognise a person's name.
$script:FriendlyNameWords = @(
    'apple', 'magic', 'mouse', 'mice', 'trackpad', 'keyboard', 'keypad',
    'numeric', 'with', 'wireless', 'bluetooth', 'low', 'energy', 'le', 'hid',
    'hid-compliant', 'compliant', 'device', 'input', 'pointing', 'usb',
    'generic', 'standard', 'port'
)

# The fifth rule, and the one the four address rules cannot do: a given name is
# neither a MAC shape nor a serial shape. Windows takes the name the peripheral
# advertises, and an Apple peripheral paired from a Mac or an iPhone advertises
# one built from the account holder's name - "Lesley's Magic Mouse".
#
# Redacting the whole value would be safe and useless. FriendlyName is how a
# reader tells a Magic Mouse from a Magic Trackpad on a PC carrying four Apple
# nodes, and a child still named "Bluetooth Low Energy HID Device" IS the
# finding: Windows never bound the vendor name. So the model words survive and
# everything else becomes <name>, token by token, with adjacent <name> tokens
# collapsed so "Jane Q Doe's Mouse" does not spell out how many words the name
# had. One- and two-digit tokens survive as generation numbers ("Magic Mouse
# 2"); longer digit runs do not, because a 12-digit run is an address.
#
# What it costs, stated plainly: a device the owner renamed loses its model word
# along with the new name, and a model Apple ships after this list was written
# reads <name> until the word is added here. Both failures are towards
# redaction, which is the side a sink that cannot see its own corpus has to fail
# on.
function ConvertTo-SafeFriendlyName {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $Name }
    $out = @()
    foreach ($token in ($Name -split '\s+')) {
        if ($token.Length -eq 0) { continue }
        $core = ($token -replace '^[^0-9A-Za-z]+', '') -replace '[^0-9A-Za-z]+$', ''
        $keep = $false
        if ($core.Length -eq 0) {
            # Pure punctuation carries nothing to redact.
            $keep = $true
        } elseif ($script:FriendlyNameWords -contains $core.ToLowerInvariant()) {
            $keep = $true
        } elseif ($core -match '^[0-9]{1,2}$') {
            $keep = $true
        }
        if ($keep) { $out += $token }
        elseif ($out.Count -eq 0 -or $out[-1] -ne '<name>') { $out += '<name>' }
    }
    return ($out -join ' ')
}

if (-not (Test-Path $OutDir)) { New-Item -Path $OutDir -ItemType Directory -Force -ErrorAction Stop | Out-Null }
$ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'

# All BTHENUM HID-class children
$bthenum = Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'BTHENUM\*' }

# Look up the parent BTH device to get the MAC
$results = @()
foreach ($d in $bthenum) {
    $rec = [ordered]@{
        # Scrubbed here, not at the write, because $d.InstanceId is still needed
        # unredacted below to build the Enum registry path.
        InstanceId = (ConvertTo-MacSafeText ([string]$d.InstanceId))
        FriendlyName = (ConvertTo-SafeFriendlyName ([string]$d.FriendlyName))
        Status = $d.Status
        Class = $d.Class
        Manufacturer = $d.Manufacturer
        Service = $d.Service
        ProblemCode = $d.Problem
    }

    # Pull device parameters (LowerFilters, Service overrides) via registry
    # InstanceId format: BTHENUM\Dev_<mac>\<...>  or BTHENUM\{guid}_VID...PID...\<...>
    # The Driver Reg key is HKLM\SYSTEM\CCS\Enum\BTHENUM\<dev>\<inst>
    $enumPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($d.InstanceId)"
    if (Test-Path -LiteralPath $enumPath) {
        try {
            $k = Get-Item -LiteralPath $enumPath
            $rec.HardwareID = ($k.GetValue('HardwareID', @()) -join ' | ')
            $rec.Service_REG = $k.GetValue('Service', '')
            $rec.ClassGUID = $k.GetValue('ClassGUID', '')
            # Get Device Parameters\* (where LowerFilters live)
            $dparam = Join-Path $enumPath 'Device Parameters'
            if (Test-Path -LiteralPath $dparam) {
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
                if (Test-Path -LiteralPath $cls) {
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
    $val = $null
    try {
        # Get-PnpDeviceProperty DEVPKEY_Device_BatteryLevel (255 if N/A).
        # -ErrorAction Stop on purpose: most BTHENUM nodes do not carry this
        # property at all, and the catch below is the diagnostic that says so.
        # SilentlyContinue made that handler unreachable.
        $bp = Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_BatteryLevel' -ErrorAction Stop
        # Data is still null when the device is present but reports no level.
        $val = if ($bp) { $bp.Data } else { $null }
    } catch { Write-Verbose "Battery-level property unavailable for $($d.InstanceId): $($_.Exception.Message)" }

    # The row is emitted either way, so the snapshot always lists every device.
    $batteryReadings += [pscustomobject]@{
        InstanceId = (ConvertTo-MacSafeText ([string]$d.InstanceId))
        FriendlyName = (ConvertTo-SafeFriendlyName ([string]$d.FriendlyName))
        BatteryLevel_DEVPKEY = $val
    }
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
# Get-WindowsDriver -Online requires elevation. Record the elevation state and
# any failure so an empty AppleFilterINF is never mistaken for "no Apple INF".
$isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$infs = @()
$infError = $null
if (-not $isElevated) {
    $infError = 'Not elevated: Get-WindowsDriver -Online requires Administrator.'
} else {
    try {
        $infs = @(Get-WindowsDriver -Online -ErrorAction Stop |
            Where-Object { $_.OriginalFileName -like '*applewirelessmouse*' -or $_.Driver -like '*applewirelessmouse*' })
    } catch {
        $infError = $_.ToString()
    }
}

$snapshot = [ordered]@{
    Captured = $ts
    BTHENUMHIDChildren = $results
    MouseClassChildren = @($mouseChildren | ForEach-Object {
        [pscustomobject]@{
            InstanceId = (ConvertTo-MacSafeText ([string]$_.InstanceId))
            FriendlyName = (ConvertTo-SafeFriendlyName ([string]$_.FriendlyName))
            Status = $_.Status
            ProblemCode = $_.Problem
            Service = $_.Service
        }
    })
    BatteryReadings = $batteryReadings
    AppleFilterService = $svcInfo
    AppleFilterINF = ($infs | Select-Object Driver, OriginalFileName, Inbox, ClassName, ProviderName, Date, Version, BootCritical)
    AppleFilterINFElevated = $isElevated
    AppleFilterINFError = $infError
}

$jsonOut = Join-Path $OutDir 'bt-stack-snapshot.json'
$txtOut = Join-Path $OutDir 'bt-stack-snapshot.txt'
# Second pass over the serialised document, so a field added above cannot ship
# an address merely by being forgotten here. The fields built from InstanceId
# are already scrubbed; this catches the ones copied straight out of the
# registry - HardwareID, ClassGUID, the Device Parameters values, and the
# RegistryReadError text, which quotes the Enum path the address is part of.
$snapshot |
    ConvertTo-Json -Depth 8 |
    ForEach-Object { ConvertTo-MacSafeText $_ } |
    Set-Content -LiteralPath $jsonOut -Encoding UTF8 -ErrorAction Stop

# Human-readable summary
$lines = @()
$lines += "=== BT HID stack snapshot @ $ts ==="
$lines += ""
$lines += "applewirelessmouse service:"
$lines += "  Exists: $($svcInfo.Exists)  Status: $($svcInfo.Status)  StartType: $($svcInfo.StartType)"
$lines += "  DriverFileExists: $($svcInfo.DriverFileExists)  Size: $($svcInfo.DriverFileSize)  Modified: $($svcInfo.DriverFileModified)"
$lines += ""
$lines += "applewirelessmouse INF entries:"
if ($infError) { $lines += "  (unavailable: $infError)" }
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

# One sink for the human-readable file, for the same reason as the JSON pass:
# the lines above are assembled from $results and $batteryReadings, whose
# InstanceId and FriendlyName are already scrubbed, but HardwareID and the
# Device Parameters values go straight from the registry into a string.
$lines |
    ForEach-Object { ConvertTo-MacSafeText $_ } |
    Set-Content -LiteralPath $txtOut -Encoding UTF8 -ErrorAction Stop

Write-Host "[bt-snapshot] OK -> $jsonOut" -ForegroundColor Green
Write-Host "[bt-snapshot] OK -> $txtOut" -ForegroundColor Green
exit 0
