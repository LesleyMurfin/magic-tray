<#
.SYNOPSIS
    Capture Magic Tray device/driver state for every catalog PID, diagnose
    common failures, and optionally restart a stopped-but-bound BT filter.

.DESCRIPTION
    Default is capture + diagnose only (no mutation). Filter services are
    DISCOVERED from the registry (every service whose name starts with
    MagicMouseDriver or applewirelessmouse), and the bound filter for a PID is
    read from that device's real LowerFilters, so a renamed or side-by-side
    filter build (for example MagicMouseDriver204Scroll) is never mistaken for
    the stock one. Use -Repair (elevated) to pnputil /restart-device the live
    BTHENUM parent when the bound filter is not running, or when it IS running
    but is absent from the live DEVPKEY_Device_Stack (registered but not
    attached: correct driver, RUNNING service, dead wheel). A PnP lower filter is
    loaded by PnP while the device stack is built, so sc start on it fails with
    WIN32_EXIT 31 by design and is never attempted here. Never unpairs, never
    flips LowerFilters, never touches the Bluetooth radio, never enables or
    restarts USB/HID phantom nodes.

    Keep catalog PIDs in sync with MouseBatteryDevice.KnownMice and
    KeyboardBatteryDevice.KnownKeyboards.

    Every logged line goes through a redaction sink: Bluetooth addresses are
    replaced with <mac>, a USB iSerialNumber with <serial>, and the words of a
    FriendlyName that are not device model words with <name>. That is not the
    same as safe to paste - the sink covers those three things only, and the
    file still names the config.ini, debug.log and output paths it read, which
    on a default install sit under the account's profile. Read it before you
    attach it.

.EXAMPLE
    .\diagnose-and-recover.ps1
    .\diagnose-and-recover.ps1 -Repair
    .\diagnose-and-recover.ps1 -DevicePid 0323 -Repair
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Repair,
    [string]$OutDir = '.',
    [string]$ConfigPath = (Join-Path $env:APPDATA 'MagicMouseTray\config.ini'),
    [string]$LogPath = (Join-Path $env:APPDATA 'MagicMouseTray\debug.log'),
    [string]$DevicePid
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Continue'

# Unique 4-hex PIDs from KnownMice + KnownKeyboards.
$CatalogPids = @(
    '0323', '030d', '0269', '0310', '0265', '030e', '0324',
    '0239', '023a', '023b', '024f', '0250', '0255', '0256', '0257',
    '0267', '026c', '029a', '029c', '029f', '0320', '0321', '0322'
)
$KmdfPrefix = 'MagicMouseDriver'
$ApplePrefix = 'applewirelessmouse'
$FilterPrefixes = @($KmdfPrefix, $ApplePrefix)
$ServicesKey = 'HKLM:\SYSTEM\CurrentControlSet\Services'

function Test-IsAdmin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal $id
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

function Get-PidNeedle {
    param([string]$Hex)
    $h = $Hex.ToLowerInvariant()
    return @{
        Pid = $h
        A = "PID_$h"
        B = "PID&$h"
    }
}

function Test-InstanceMatchesPid {
    param([string]$InstanceId, [string]$Hex)
    $n = Get-PidNeedle $Hex
    $low = $InstanceId.ToLowerInvariant()
    # A PID alone is not unique across vendors: BTHENUM\{00001124-...}_VID&0000045e_
    # PID&030d is a Microsoft mouse carrying the Magic Mouse v1 PID, and the app
    # rejects it too (DeviceEnable.MatchesInstance, DeviceEnable.cs:100-121;
    # fixture DeviceEnableTests.cs:58-60). So a match needs an Apple vendor id
    # PRESENT, not merely no other vendor's - VID_05AC on USB/HID, _VID&0001004c_
    # or _VID&000205ac_ on BTHENUM, which are the only three VidPattern values in
    # the whole catalog (MouseBatteryDevice.KnownMice, KnownKeyboards). Requiring
    # presence loses nothing: Windows emits the PID token only in the joint
    # VID_xxxx&PID_xxxx / _VID&xxxxxxxx_PID&xxxx form, so every id that reaches
    # the vendor test already carries a vendor id. The VID-less BTHENUM forms
    # (Dev_<MAC>, _LOCALMFG&000f) carry no PID either and never matched.
    $vid = [regex]::Match(
        $low,
        '(?:^|[^a-z0-9])vid(?:_|&)([0-9a-f]+)(?=[^0-9a-f]|$)')
    if (-not $vid.Success -or
        @('05ac', '0001004c', '000205ac') -notcontains $vid.Groups[1].Value) {
        return $false
    }
    return ($low.Contains($n.A.ToLowerInvariant()) -or $low.Contains($n.B.ToLowerInvariant()))
}

function Test-IsUsbOrPhantomPath {
    param([string]$InstanceId)
    $low = $InstanceId.ToLowerInvariant()
    if ($low.StartsWith('usb\')) { return $true }
    if ($low.StartsWith('hid\vid_')) { return $true }
    return $false
}

function Test-IsBthenumPath {
    param([string]$InstanceId)
    return $InstanceId.StartsWith('BTHENUM\', [StringComparison]::OrdinalIgnoreCase)
}

# --- Redaction ---------------------------------------------------------------
# This script logs a line per PnP node, and a BTHENUM InstanceId carries the
# device's Bluetooth address while a paired FriendlyName routinely carries the
# owner's given name. The file is saved beside the working directory AND copied
# into %APPDATA%\MagicMouseTray, which is where a bug reporter is told to look
# for things to attach, so the scrub belongs at the sink rather than at each of
# the forty-odd call sites.
#
# DUPLICATED on purpose, not dot-sourced. ConvertTo-MacSafeText below is the
# same function as the one in mm-hid-report.ps1 and mm-bt-stack-snapshot.ps1. A
# shared helper file cannot be reached from every layout this script ships in:
# stage-publish.ps1 copies it FLAT beside MagicMouseTray.exe,
# .github/workflows/release.yml uploads that flat copy as a standalone release
# asset, and package-release.ps1 puts it under scripts/ inside the ZIP. Someone
# who downloads the single .ps1 asset has no sibling helper at all, so a
# dot-source would either abort a script that works today or run on with no
# sink - a worse outcome than the leak it replaced, because the sink would be
# claimed and absent. The cost of duplicating is drift, so the rule is: the
# three address rules are shared with MagicMouseTray/Logger.cs (RedactMacs) and
# MagicMouseTray/BugReport.cs. They are not to be reordered, reworded or
# widened here. Change them there first.

# 12-hex runs that are the SAME on every Windows PC, so they are not anybody's
# address and redacting them only destroys the field a maintainer reads first.
# 00805f9b34fb is the Bluetooth SIG base UUID tail, carried by every BTHENUM
# service GUID this script logs ({00001124-...} HID, {00001200-...} PnP
# information); 001111000030 is the tail of the HID device interface class GUID
# {4d1e55b2-f16f-11cf-88cb-001111000030}. The list is a WHITELIST of named
# constants, never a pattern that tries to recognise an address: a real address
# can look like anything, so the default stays redact.
$script:MacSafeConstants = @('00805f9b34fb', '001111000030')

# The rules of Logger.RedactMacs, in Logger.RedactMacs' order - the punctuated
# pair forms, the Dev_ form, then the bare 12-hex belt with the constants above
# carved out - followed by the fourth rule mm-hid-report.ps1 adds: the third
# node of a USB instance id is the unit's iSerialNumber, which is not a MAC
# shape and so matched none of the three. That rule earns its place here,
# because a Lightning Magic Mouse 2 enumerates over USB while it charges and
# this script logs every USB and phantom node it matched. It runs LAST and
# consumes an already-redacted <mac> token only when that token is the WHOLE
# node, so a serial that is the unit's address with the separators stripped
# keeps the <mac> spelling and still correlates with debug.log.
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
# Redacting the whole value would be safe and useless. FriendlyName is the one
# field on the node line that says which of four Apple children a reader is
# looking at, and a child still named "Bluetooth Low Energy HID Device" IS the
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

# Every logged line goes through the address rules here, so the console text and
# the saved text are the same text and neither can carry an address - the
# argument Logger.RedactMacs makes for a sink: a policy at the sink is one the
# next line to be written cannot forget. FriendlyName needs the token rule
# instead, and gets it where the row is built, because a whitelist of model
# words cannot be let loose on a whole log line.
#
# What this sink does NOT do, so nobody reads it as a safe-to-paste banner:
# Logger.RedactProfilePaths and BugReport's <user>/<host> pass are absent, and
# this script does log paths that can name an account - the -ConfigPath and
# -LogPath defaults under %APPDATA%, and the "Saved:" line. Bringing those two
# passes across is the next piece of work, not something this change claims.
function Write-Log {
    param([string]$Text, [string]$Color = '')
    $safe = ConvertTo-MacSafeText $Text
    $script:Lines += $safe
    if ($Color) { Write-Host $safe -ForegroundColor $Color }
    else { Write-Host $safe }
}

function Test-IsFilterFamilyName {
    param([string]$Name)
    if (-not $Name) { return $false }
    foreach ($p in $FilterPrefixes) {
        if ($Name.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Get-FilterFamilyPrefix {
    param([string]$Name)
    if (-not $Name) { return '' }
    foreach ($p in $FilterPrefixes) {
        if ($Name.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)) { return $p }
    }
    return ''
}

function Get-DiscoveredFilterServiceName {
    $keys = @()
    try {
        $keys = @(Get-ChildItem -LiteralPath $ServicesKey -ErrorAction Stop)
    } catch {
        return @()
    }
    $found = @()
    foreach ($k in $keys) {
        $leaf = [string]$k.PSChildName
        if (Test-IsFilterFamilyName $leaf) { $found += $leaf }
    }
    return @($found | Sort-Object)
}

function Resolve-DriverImagePath {
    param([string]$ImagePath)
    if (-not $ImagePath) { return '' }
    $p = $ImagePath.Trim().Trim('"')
    if (-not $p) { return '' }
    if ($p.StartsWith('\??\')) { $p = $p.Substring(4) }
    if ($p.StartsWith('\SystemRoot\', [StringComparison]::OrdinalIgnoreCase)) {
        return (Join-Path $env:SystemRoot $p.Substring(12))
    }
    if ($p.Length -ge 2 -and $p.Substring(1, 1) -eq ':') { return $p }
    if ($p.StartsWith('\')) { return ($env:SystemDrive + $p) }
    return (Join-Path $env:SystemRoot $p)
}

function Get-ServiceSnapshot {
    param([string]$Name)
    $snap = [ordered]@{
        Name = $Name
        Exists = $false
        State = 'absent'
        Win32Exit = ''
        StartType = ''
        RegStart = ''
        Binary = ''
        ImagePath = ''
        SysPath = ''
        SysExists = $false
        SysSize = $null
        SysMtime = $null
        Running = $false
    }
    $qc = & sc.exe qc $Name 2>&1 | Out-String
    $query = & sc.exe query $Name 2>&1 | Out-String
    if ($query -match 'STATE\s*:\s*\d+\s+(\w+)') { $snap.State = $Matches[1] }
    if ($query -match 'WIN32_EXIT_CODE\s*:\s*(\d+)') { $snap.Win32Exit = $Matches[1] }
    if ($qc -notmatch 'FAILED|does not exist|1060') { $snap.Exists = $true }
    if ($qc -match 'START_TYPE\s*:\s*\d+\s+(\w+)') { $snap.StartType = $Matches[1] }
    if ($qc -match 'BINARY_PATH_NAME\s*:\s*([^\r\n]+)') { $snap.Binary = $Matches[1].Trim() }
    if ($query -match '1060') {
        $snap.Exists = $false
        $snap.State = 'absent'
    }
    # ImagePath is authoritative: the .sys file name does NOT have to match the
    # service name (MagicMouseDriver204Scroll -> MagicMouseDriver-kmdf-204-scroll.sys).
    $regPath = Join-Path $ServicesKey $Name
    if (Test-Path -LiteralPath $regPath) {
        $snap.Exists = $true
        try {
            $rk = Get-Item -LiteralPath $regPath
            $ip = [string]$rk.GetValue('ImagePath', '')
            if ($ip) { $snap.ImagePath = $ip }
            $st = $rk.GetValue('Start', $null)
            if ($null -ne $st) { $snap.RegStart = [string]$st }
        } catch { Write-Verbose "GetServiceSnapshot registry read failed: $_" }
    }
    if (-not $snap.ImagePath -and $snap.Binary) { $snap.ImagePath = $snap.Binary }
    if ($snap.ImagePath) {
        $snap.SysPath = Resolve-DriverImagePath $snap.ImagePath
    } else {
        $snap.SysPath = Join-Path $env:SystemRoot "System32\drivers\$Name.sys"
    }
    if ($snap.SysPath -and (Test-Path -LiteralPath $snap.SysPath -PathType Leaf)) {
        try {
            $fi = Get-Item -LiteralPath $snap.SysPath
            $snap.SysExists = $true
            $snap.SysSize = $fi.Length
            $snap.SysMtime = $fi.LastWriteTime.ToString('o')
        } catch { Write-Verbose "GetServiceSnapshot file read failed: $_" }
    }
    $snap.Running = ($snap.State -eq 'RUNNING')
    return [pscustomobject]$snap
}

function Get-FilterSnapshot {
    param([string]$Name)
    if (-not $Name) { return $null }
    $key = $Name.ToLowerInvariant()
    if ($script:svcSnaps.ContainsKey($key)) { return $script:svcSnaps[$key] }
    $s = Get-ServiceSnapshot $Name
    $script:svcSnaps[$key] = $s
    return $s
}

function Test-IsBoundFilterCandidate {
    param([string]$Name, [string[]]$KnownNames)
    if (-not $Name) { return $false }
    foreach ($k in @($KnownNames)) {
        if (-not $k) { continue }
        if ($Name.Equals($k, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return (Test-IsFilterFamilyName $Name)
}

function Resolve-BoundFilter {
    param($Rows, [string[]]$KnownNames)
    # Live stack only: USB/HID charge leftovers and phantoms never define the
    # bound filter. LowerFilters wins over the HID-layer Service value.
    $live = @(@($Rows) | Where-Object { $_ -and -not $_.Phantom -and $_.Kind -ne 'USB' })
    $ordered = @($live | Where-Object { $_.Kind -eq 'BTHENUM' }) + @($live | Where-Object { $_.Kind -ne 'BTHENUM' })
    foreach ($r in $ordered) {
        foreach ($n in @($r.LowerFilters)) {
            if (Test-IsBoundFilterCandidate ([string]$n) $KnownNames) { return [string]$n }
        }
    }
    foreach ($r in $ordered) {
        foreach ($n in @($r.Service, $r.ServiceReg)) {
            if (Test-IsBoundFilterCandidate ([string]$n) $KnownNames) { return [string]$n }
        }
    }
    return $null
}

# --- Stack attachment -------------------------------------------------------
# LowerFilters says PnP was TOLD to load the filter. sc query STATE=RUNNING says
# the driver IMAGE is loaded somewhere in the kernel. Neither proves the filter
# is ATTACHED to this mouse's live stack: after a reboot PnP can rebuild the
# BTHENUM stack without it while both of those still read healthy, and then the
# wheel is dead. DEVPKEY_Device_Stack is the discriminator, read exactly the way
# scripts/capture-state.ps1 reads it.

function Get-DeviceStackEntry {
    param([string]$InstanceId)
    # $null = the property could not be read (no evidence). An array otherwise.
    if (-not $InstanceId) { return $null }
    $prop = $null
    try {
        $prop = Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName 'DEVPKEY_Device_Stack' -ErrorAction SilentlyContinue
    } catch {
        return $null
    }
    if (-not $prop) { return $null }
    $data = $null
    try { $data = $prop.Data } catch { return $null }
    if ($null -eq $data) { return $null }
    $entries = @(@($data) | ForEach-Object { [string]$_ } | Where-Object { $_ })
    if ($entries.Count -eq 0) { return $null }
    return $entries
}

function Test-StackNamesFilter {
    param($Stack, [string]$BoundFilterName)
    # Stack entries are driver object paths ("\Driver\HidBth", "\Driver\mouhid",
    # "\Driver\MagicMouseDriver204Scroll") while the bound name is the verbatim
    # service name, so the name can only be a SUBSTRING of an entry. A filter of
    # the same family under ANY name counts as attached - the dead-wheel stack
    # carries no family filter at all.
    foreach ($e in @($Stack)) {
        $entry = [string]$e
        if (-not $entry) { continue }
        if ($BoundFilterName -and $entry.IndexOf($BoundFilterName, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
        $leaf = $entry.Substring($entry.LastIndexOf('\') + 1)
        if (Test-IsFilterFamilyName $leaf) { return $true }
    }
    return $false
}

function Get-BoundFilterStackState {
    param($InstanceIds, [string]$BoundFilterName)
    # Tri-state, on purpose:
    #   $true  = the bound filter (or a same-family filter) is on a live stack
    #   $false = at least one live stack was readable and none of them names it
    #   $null  = nothing readable -> no evidence -> NEVER treated as a fault
    $readable = 0
    foreach ($id in @($InstanceIds)) {
        $stack = Get-DeviceStackEntry ([string]$id)
        if ($null -eq $stack) { continue }
        $readable = $readable + 1
        if (Test-StackNamesFilter $stack $BoundFilterName) { return $true }
    }
    if ($readable -eq 0) { return $null }
    return $false
}

function Format-StackState {
    param($State)
    if ($null -eq $State) { return 'unknown' }
    if ($State) { return 'attached' }
    return 'absent'
}

if ($Repair -and -not (Test-IsAdmin)) {
    Write-Host 'ERROR: -Repair requires an elevated PowerShell.' -ForegroundColor Red
    exit 1
}

$filterPid = $null
if ($DevicePid) {
    $filterPid = ($DevicePid -replace '[^0-9A-Fa-f]', '').ToLowerInvariant()
    if ($filterPid.Length -ne 4) {
        Write-Host 'ERROR: -DevicePid must be 4 hex digits (e.g. 0323, 030d).' -ForegroundColor Red
        exit 1
    }
}

$pids = if ($filterPid) { @($filterPid) } else { $CatalogPids }

if (-not (Test-Path -LiteralPath $OutDir -PathType Container)) {
    New-Item -ItemType Directory -Path $OutDir -Force -ErrorAction Stop | Out-Null
}

$ts = Get-Date -Format 'yyyyMMdd-HHmmss'
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
$Lines = @()
$diagnoses = @()
$repairResults = @()

Write-Log "=== diagnose-and-recover $stamp ===" 'Cyan'
Write-Log "Repair=$Repair  Pid=$(if ($filterPid) { $filterPid } else { 'all catalog' })  Admin=$(Test-IsAdmin)"

# --- Filter services ---
Write-Log ''
Write-Log '=== FILTER SERVICES ===' 'Cyan'
$svcSnaps = @{}
$filterNames = @(Get-DiscoveredFilterServiceName)
Write-Log ("  discovered={0}" -f $(if ($filterNames.Count -gt 0) { $filterNames -join ', ' } else { '(none)' }))
if ($filterNames.Count -eq 0) {
    Write-Log ("  no service key under {0} starts with {1}" -f $ServicesKey, ($FilterPrefixes -join ' or ')) 'Yellow'
}
foreach ($name in $filterNames) {
    $s = Get-FilterSnapshot $name
    Write-Log ("  {0}: exists={1} state={2} win32_exit={3} start={4} regstart={5}" -f $s.Name, $s.Exists, $s.State, $s.Win32Exit, $s.StartType, $s.RegStart)
    Write-Log ("    image={0}" -f $s.ImagePath)
    Write-Log ("    sys exists={0} path={1} size={2} mtime={3}" -f $s.SysExists, $s.SysPath, $s.SysSize, $s.SysMtime)
}

# --- Integrity (best-effort; swallow access denied) ---
Write-Log ''
Write-Log '=== INTEGRITY ===' 'Cyan'
# SystemStartOptions is readable without elevation; bcdedit is not.
$testsigning = 'unread'
$startOptions = ''
try {
    $ctlKey = 'HKLM:\SYSTEM\CurrentControlSet\Control'
    $sso = Get-ItemProperty -LiteralPath $ctlKey -Name SystemStartOptions -ErrorAction Stop
    $startOptions = [string]$sso.SystemStartOptions
    if ($startOptions -match 'TESTSIGNING') { $testsigning = 'on' } else { $testsigning = 'off' }
} catch {
    $testsigning = "unread: $($_.Exception.Message)"
}
Write-Log "  testsigning=$testsigning"
Write-Log "  SystemStartOptions=$startOptions"

$hvci = 'unread'
try {
    $hvciPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity'
    if (Test-Path -LiteralPath $hvciPath) {
        $hvci = [string](Get-ItemProperty -LiteralPath $hvciPath -Name Enabled -ErrorAction Stop).Enabled
    } else {
        $hvci = 'key-missing'
    }
} catch {
    $hvci = "unread: $($_.Exception.Message)"
}
Write-Log "  HVCI Enabled=$hvci"
$testsigningOn = ($testsigning -eq 'on')
$hvciOn = ($hvci -eq '1')
# Trap: the two booleans above collapse three states into two. A failed registry
# read leaves $testsigning as "unread: <message>" (and $hvci likewise), so
# -not $testsigningOn is TRUE for a value nobody read - indistinguishable from a
# measured 'off'. Downstream guards must test $policyUnread explicitly instead of
# trusting the booleans, or they announce a code-integrity block having measured
# no policy at all. 'key-missing' is NOT unread: an absent HVCI scenario key is a
# real reading of HVCI off, and $hvciOn keeps treating it as off.
$policyUnread = ($testsigning -like 'unread*') -or ($hvci -like 'unread*')
if ($testsigningOn -and -not $hvciOn) {
    Write-Log '  self-signed KMDF filters can load on this machine (test signing on, HVCI off); Test Mode is not the fix here.' 'Green'
}

# --- PnP per PID ---
Write-Log ''
Write-Log '=== PNP BY CATALOG PID ===' 'Cyan'
$allDev = @()
try {
    $allDev = @(Get-PnpDevice -ErrorAction Stop)
} catch {
    Write-Log "  Get-PnpDevice failed: $($_.Exception.Message)" 'Red'
}
Write-Log ("  Get-PnpDevice count={0}" -f $allDev.Count)
$perPid = @{}
$perPidBound = @{}
$perPidStack = @{}

foreach ($hex in $pids) {
    $devMatches = @($allDev | Where-Object { $_.InstanceId -and (Test-InstanceMatchesPid $_.InstanceId $hex) })
    $rows = @()
    foreach ($d in $devMatches) {
        $id = [string]$d.InstanceId
        $kind = if (Test-IsBthenumPath $id) { 'BTHENUM' }
            elseif (Test-IsUsbOrPhantomPath $id) { 'USB' }
            elseif ($id.StartsWith('HID\', [StringComparison]::OrdinalIgnoreCase)) { 'HID' }
            else { 'OTHER' }
        $problem = [string]$d.Problem
        $phantom = ($problem -match 'PHANTOM') -or ($kind -eq 'USB' -and [string]$d.Status -eq 'Unknown')
        $lf = @()
        $svcReg = ''
        $enumPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\$id"
        if (Test-Path -LiteralPath $enumPath) {
            try {
                $k = Get-Item -LiteralPath $enumPath
                $svcReg = [string]$k.GetValue('Service', '')
                $cls = [string]$k.GetValue('Driver', '')
                foreach ($src in @($k)) {
                    $v = $src.GetValue('LowerFilters')
                    if ($v -is [string[]]) { $lf += $v }
                    elseif ($v) { $lf += [string]$v }
                }
                $dp = Join-Path $enumPath 'Device Parameters'
                if (Test-Path -LiteralPath $dp) {
                    $v = (Get-Item -LiteralPath $dp).GetValue('LowerFilters')
                    if ($v -is [string[]]) { $lf += $v }
                    elseif ($v) { $lf += [string]$v }
                }
                $parentPath = Split-Path -Parent $enumPath
                if ($parentPath -and (Test-Path -LiteralPath $parentPath)) {
                    $pv = (Get-Item -LiteralPath $parentPath).GetValue('LowerFilters')
                    if ($pv -is [string[]]) { $lf += $pv }
                    elseif ($pv) { $lf += [string]$pv }
                }
                if ($cls) {
                    $ck = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$cls"
                    if (Test-Path -LiteralPath $ck) {
                        $cv = (Get-Item -LiteralPath $ck).GetValue('LowerFilters')
                        if ($cv -is [string[]]) { $lf += $cv }
                        elseif ($cv) { $lf += [string]$cv }
                    }
                }
            } catch { Write-Verbose "Enumerate BTHENUM registry failed: $_" }
        }
        $rows += [pscustomobject]@{
            # InstanceId stays as Windows spells it: pnputil /restart-device is
            # handed this value, and a redacted one addresses nothing. Write-Log
            # is what scrubs it on the way out.
            InstanceId = $id
            Status = [string]$d.Status
            Problem = $problem
            Class = [string]$d.Class
            # FriendlyName is only ever logged, so it is scrubbed once here
            # rather than at each line that prints it.
            FriendlyName = (ConvertTo-SafeFriendlyName ([string]$d.FriendlyName))
            Service = [string]$d.Service
            ServiceReg = $svcReg
            Kind = $kind
            Phantom = $phantom
            LowerFilters = ($lf | Select-Object -Unique)
            Col01 = ($id -match 'COL01')
            Col02 = ($id -match 'COL02')
        }
    }
    $perPid[$hex] = $rows
    $bound = Resolve-BoundFilter $rows $filterNames
    $perPidBound[$hex] = $bound
    $bt = @($rows | Where-Object { $_.Kind -eq 'BTHENUM' })
    $usb = @($rows | Where-Object { $_.Kind -eq 'USB' -or $_.Phantom })
    $col01 = @($rows | Where-Object { $_.Col01 }).Count -gt 0
    $col02 = @($rows | Where-Object { $_.Col02 }).Count -gt 0
    Write-Log ("  PID {0}: {1} node(s)  BTHENUM={2} USB/phantom={3} COL01={4} COL02={5}" -f $hex.ToUpperInvariant(), $rows.Count, $bt.Count, $usb.Count, $col01, $col02)
    $stackState = $null
    if ($bound) {
        $liveBt = @($rows | Where-Object { $_.Kind -eq 'BTHENUM' -and -not $_.Phantom } | Select-Object -ExpandProperty InstanceId -Unique)
        if ($liveBt.Count -gt 0) { $stackState = Get-BoundFilterStackState $liveBt $bound }
    }
    $perPidStack[$hex] = $stackState
    if ($bound) {
        $bs = Get-FilterSnapshot $bound
        Write-Log ("    bound={0} state={1} win32_exit={2} pkg={3} stack={4}" -f $bound, $bs.State, $bs.Win32Exit, $bs.SysExists, (Format-StackState $stackState))
    } elseif ($rows.Count -gt 0) {
        Write-Log ("    bound=(none) - no LowerFilters/HID service in the {0} family on the live nodes" -f ($FilterPrefixes -join '/'))
    }
    foreach ($r in $rows) {
        $tag = if ($r.Phantom) { ' phantom' } else { '' }
        Write-Log ("    [{0}/{1}{2}] {3}" -f $r.Status, $r.Kind, $tag, $r.InstanceId)
        Write-Log ("      {0}  svc={1} lf={2}" -f $r.FriendlyName, $r.Service, ($r.LowerFilters -join ','))
    }
}

# --- config.ini ---
Write-Log ''
Write-Log '=== TRAY CONFIG ===' 'Cyan'
$configEnabled = @{}
$configRecycle = ''
if (Test-Path -LiteralPath $ConfigPath) {
    Write-Log "  path=$ConfigPath"
    foreach ($line in Get-Content -LiteralPath $ConfigPath) {
        Write-Log "    $line"
        if ($line -match '^enabled_([0-9a-fA-F]{4})=(true|false)$') {
            $configEnabled[$Matches[1].ToLowerInvariant()] = $Matches[2]
        }
        if ($line -match '^enable_v3_recycle=(.+)$') { $configRecycle = $Matches[1] }
    }
} else {
    Write-Log "  missing $ConfigPath"
}
Write-Log "  enable_v3_recycle=$configRecycle"

# --- debug.log tail ---
Write-Log ''
Write-Log '=== DEBUG.LOG (last 50) ===' 'Cyan'
if (Test-Path -LiteralPath $LogPath) {
    Write-Log "  path=$LogPath"
    Get-Content -LiteralPath $LogPath -Tail 50 | ForEach-Object { Write-Log "    $_" }
} else {
    Write-Log "  missing $LogPath"
}

# --- Diagnoses ---
Write-Log ''
Write-Log '=== DIAGNOSES ===' 'Yellow'

foreach ($hex in $pids) {
    $rows = @($perPid[$hex])
    $btOk = @($rows | Where-Object { $_.Kind -eq 'BTHENUM' -and $_.Status -eq 'OK' })
    $anyBt = @($rows | Where-Object { $_.Kind -eq 'BTHENUM' })
    $usbPh = @($rows | Where-Object { $_.Phantom -or $_.Kind -eq 'USB' })
    $enabledFlag = if ($configEnabled.ContainsKey($hex)) { $configEnabled[$hex] } else { 'default-true' }

    # Bound filter = the service actually named in this device's LowerFilters
    # (or the HID-layer Service), verbatim. Never a hardcoded constant.
    $boundName = $null
    if ($perPidBound.ContainsKey($hex)) { $boundName = $perPidBound[$hex] }
    $boundSnap = $null
    if ($boundName) { $boundSnap = Get-FilterSnapshot $boundName }
    $famPrefix = Get-FilterFamilyPrefix $boundName

    if ($anyBt.Count -gt 0 -and $boundName -and $boundSnap) {
        if (-not $boundSnap.Exists) {
            $msg = "PID $($hex.ToUpperInvariant()): LowerFilters names $boundName but no such service key exists under CurrentControlSet\Services (orphaned filter reference). Reinstall the filter package; do not hand-edit LowerFilters."
            $diagnoses += $msg
            Write-Log "  ! $msg" 'Red'
        } elseif (-not $boundSnap.SysExists) {
            $msg = "PID $($hex.ToUpperInvariant()): bound filter $boundName has a service key but its driver file is missing ($($boundSnap.SysPath)). Reinstall the filter package."
            $diagnoses += $msg
            Write-Log "  ! $msg" 'Red'
        } elseif (-not $boundSnap.Running) {
            $msg = "PID $($hex.ToUpperInvariant()): bound filter $boundName is $($boundSnap.State) win32_exit=$($boundSnap.Win32Exit) while still named on the live stack (stopped-but-bound). Pointer/battery can work; scroll is dead until the BTHENUM parent is restarted. Fix: pnputil /restart-device on the BTHENUM instance - sc start cannot load a PnP lower filter. USB charge leftovers are not the live stack."
            $diagnoses += $msg
            Write-Log "  ! $msg" 'Red'
            # This script never reads $boundName's Authenticode signature, so it
            # cannot say the filter is self-signed. Report the measured policy and
            # what that policy rejects; the reader matches it against the build
            # they installed. applewirelessmouse is Apple cross-signed and must
            # not collect a self-signed diagnosis from a policy value alone.
            if ($policyUnread) {
                Write-Log ("  PID {0}: code integrity policy could not be read (testsigning={1} HVCI={2}), so signing enforcement is UNKNOWN here - neither confirmed nor ruled out as the reason {3} is not running. Re-read it elevated (bcdedit /enum plus the DeviceGuard HVCI value) before treating this as a signing problem." -f $hex.ToUpperInvariant(), $testsigning, $hvci, $boundName) 'Yellow'
            } elseif (-not $testsigningOn -or $hvciOn) {
                $msg = "PID $($hex.ToUpperInvariant()): measured code integrity policy (testsigning=$testsigning HVCI=$hvci) rejects any driver that is not cross-signed. This run did not read $boundName's signature, so that is a policy fact and not a diagnosis of this filter. If the installed build is self-signed, enable Test Mode or install a cross-signed build, then restart the device."
                $diagnoses += $msg
                Write-Log "  ! $msg" 'Red'
            }
        } else {
            # RUNNING. Registration (LowerFilters), package (.sys) and image load
            # (sc query) are all healthy, so the only question left is whether the
            # filter is on the live stack. sc query cannot answer it.
            $stackState = $null
            if ($perPidStack.ContainsKey($hex)) { $stackState = $perPidStack[$hex] }
            if ($null -eq $stackState) {
                Write-Log ("  PID {0}: bound filter {1} is RUNNING; DEVPKEY_Device_Stack could not be read on any live BTHENUM instance, so attachment is UNKNOWN. Not treated as a fault." -f $hex.ToUpperInvariant(), $boundName) 'Yellow'
            } elseif ($stackState) {
                Write-Log ("  PID {0}: bound filter {1} is RUNNING and ATTACHED - it appears in DEVPKEY_Device_Stack on the live BTHENUM instance. Nothing to repair." -f $hex.ToUpperInvariant(), $boundName) 'Green'
            } else {
                $msg = "PID $($hex.ToUpperInvariant()): bound filter $boundName is RUNNING and named in LowerFilters but is ABSENT from DEVPKEY_Device_Stack on the live BTHENUM instance (registered but not attached). The wheel is dead even though the service is RUNNING. Fix: pnputil /restart-device on the live BTHENUM instance, then re-read DEVPKEY_Device_Stack - sc query reports RUNNING either way and proves nothing here. Re-run this script elevated with -Repair."
                $diagnoses += $msg
                Write-Log "  ! $msg" 'Red'
            }
        }
    }

    # Informational only: a same-family service that is NOT the bound one is a
    # leftover. Its state is not a diagnosis.
    if ($anyBt.Count -gt 0 -and $boundName -and $famPrefix) {
        foreach ($fn in $filterNames) {
            if ($fn.Equals($boundName, [StringComparison]::OrdinalIgnoreCase)) { continue }
            if (-not $fn.StartsWith($famPrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
            Write-Log ("  note: {0} present but not bound (leftover); bound filter is {1} ({2})" -f $fn, $boundName, $boundSnap.State) 'DarkGray'
        }
    }

    if ($rows.Count -eq 0 -and $configEnabled.ContainsKey($hex)) {
        $msg = "PID $($hex.ToUpperInvariant()): zero PnP/Enum instances. Do not pnputil enable. If this is a mouse you still own, put it in pairing mode and use Windows Bluetooth -> Add device. Tray Enabled on this PC cannot recreate a removed pairing."
        $diagnoses += $msg
        Write-Log "  ! $msg" 'Red'
    }

    if ($enabledFlag -eq 'true' -and $rows.Count -eq 0) {
        $msg = "PID $($hex.ToUpperInvariant()): config enabled_$hex=true but zero instances (config vs PnP desync)."
        $diagnoses += $msg
        Write-Log "  ! $msg" 'Red'
    }
    if ($enabledFlag -eq 'false' -and $btOk.Count -gt 0) {
        $msg = "PID $($hex.ToUpperInvariant()): config enabled_$hex=false but BTHENUM Status=OK (config vs PnP desync). Tray poller skips this PID."
        $diagnoses += $msg
        Write-Log "  ! $msg" 'Yellow'
    }

    if ($hex -eq '0323' -and $usbPh.Count -gt 0 -and $anyBt.Count -gt 0) {
        $msg = "PID 0323: USB/phantom nodes present while BTHENUM is live (typical after USB-C charge). Ignore USB leftovers; do not enable/restart them."
        $diagnoses += $msg
        Write-Log "  ! $msg" 'Yellow'
    }
}

if ($diagnoses.Count -eq 0) {
    Write-Log '  (no incident signatures matched)' 'Green'
}

Write-Log ''
Write-Log '=== PAIRING MODE (when zero instances) ===' 'Cyan'
Write-Log '  Magic Mouse v1 (AA): flip the underside switch off, then on, until the LED blinks.'
Write-Log '  Magic Mouse v3 (USB-C): turn the mouse off/on; if removed in Settings, Add device while it is on.'
Write-Log '  Then: Windows Settings -> Bluetooth and devices -> Add device. Not tray Enabled on this PC.'
Write-Log '  After it appears, set tray Enabled on this PC if config still has enabled_<pid>=false.'

# --- Repair ---
$repairFailed = $false
if ($Repair) {
    Write-Log ''
    Write-Log '=== REPAIR (BTHENUM restart only) ===' 'Cyan'
    Write-Log '  A PnP lower filter is loaded by PnP while the device stack is built, so sc start on it fails with WIN32_EXIT 31 by design. This script never calls sc start; it restarts the live BTHENUM parent and then re-reads the bound service (stopped-but-bound) or DEVPKEY_Device_Stack (running-but-not-attached).'
    foreach ($hex in $pids) {
        $rows = @($perPid[$hex])
        if ($rows.Count -eq 0) {
            Write-Log ("  PID {0}: skip repair - zero instances. Put the device in pairing mode and use Windows Bluetooth -> Add device first." -f $hex.ToUpperInvariant()) 'Yellow'
            continue
        }
        $boundFilter = $null
        if ($perPidBound.ContainsKey($hex)) { $boundFilter = $perPidBound[$hex] }
        if (-not $boundFilter) {
            Write-Log ("  PID {0}: skip repair - no filter service is bound to this device." -f $hex.ToUpperInvariant())
            continue
        }
        $svcSnap = Get-FilterSnapshot $boundFilter
        $stackState = $null
        if ($perPidStack.ContainsKey($hex)) { $stackState = $perPidStack[$hex] }
        # RUNNING is not attachment. The reported fault is a filter whose service
        # is RUNNING while PnP rebuilt the BTHENUM stack without it: correct
        # driver, RUNNING service, dead wheel. Skipping on RUNNING alone declares
        # that machine healthy, so the skip needs the stack to agree.
        $reattach = $false
        if ($svcSnap.Running) {
            if ($null -eq $stackState) {
                Write-Log ("  PID {0}: skip repair - bound filter {1} is RUNNING and DEVPKEY_Device_Stack could not be read on any live BTHENUM instance; attachment is UNKNOWN, and unknown is not a fault." -f $hex.ToUpperInvariant(), $boundFilter) 'Yellow'
                continue
            }
            if ($stackState) {
                Write-Log ("  PID {0}: skip repair - bound filter {1} is RUNNING and present in DEVPKEY_Device_Stack." -f $hex.ToUpperInvariant(), $boundFilter) 'Green'
                continue
            }
            $reattach = $true
        }
        if (-not $svcSnap.Exists) {
            Write-Log ("  PID {0}: skip repair - bound filter {1} has no service key; reinstall the filter package." -f $hex.ToUpperInvariant(), $boundFilter) 'Yellow'
            continue
        }
        $targets = @($rows | Where-Object { $_.Kind -eq 'BTHENUM' -and -not $_.Phantom } | Select-Object -ExpandProperty InstanceId -Unique)
        if ($targets.Count -eq 0) {
            Write-Log ("  PID {0}: skip repair - no live BTHENUM parent." -f $hex.ToUpperInvariant()) 'Yellow'
            continue
        }
        if ($reattach) {
            Write-Log ("  PID {0}: bound filter {1} is RUNNING but ABSENT from DEVPKEY_Device_Stack (registered but not attached - dead wheel); restarting the live BTHENUM parent so PnP rebuilds the stack with the filter on it." -f $hex.ToUpperInvariant(), $boundFilter) 'Yellow'
        } else {
            Write-Log ("  PID {0}: bound filter {1} is {2} win32_exit={3}; restarting the BTHENUM parent so PnP reloads it." -f $hex.ToUpperInvariant(), $boundFilter, $svcSnap.State, $svcSnap.Win32Exit)
        }
        $restarted = 0
        foreach ($id in $targets) {
            if ($id -match 'FLIP:NoFilter|Install-KMDF|Uninstall-KMDF') {
                Write-Log "  REFUSE $id" 'Red'
                $repairFailed = $true
                continue
            }
            if ((Test-IsUsbOrPhantomPath $id) -or -not (Test-IsBthenumPath $id)) {
                Write-Log "  REFUSE $id - not a live BTHENUM instance" 'Red'
                continue
            }
            Write-Log "  pnputil /restart-device $id"
            if ($PSCmdlet.ShouldProcess($id, 'pnputil /restart-device')) {
                & pnputil.exe /restart-device "$id"
                $code = $LASTEXITCODE
                $repairResults += [pscustomobject]@{ Pid = $hex; InstanceId = $id; Exit = $code }
                if ($code -ne 0) {
                    Write-Log "    exit=$code" 'Red'
                    $repairFailed = $true
                } else {
                    Write-Log "    exit=0" 'Green'
                    $restarted = $restarted + 1
                }
            }
        }
        if ($restarted -gt 0) {
            Start-Sleep -Seconds 3
            if ($reattach) {
                # The service was already RUNNING before this restart, so polling
                # it again proves nothing. Re-read the stack: that is the signal
                # that changed, and the only one tied to the wheel.
                $afterStack = Get-BoundFilterStackState $targets $boundFilter
                $perPidStack[$hex] = $afterStack
                if ($null -eq $afterStack) {
                    Write-Log '    DEVPKEY_Device_Stack could not be read after restart-device, so attachment is UNKNOWN. Scroll to check the wheel, then re-run this script.' 'Yellow'
                } elseif ($afterStack) {
                    Write-Log ("    {0} is now present in DEVPKEY_Device_Stack on the live BTHENUM instance - the filter is attached. Confirm the wheel by scrolling." -f $boundFilter) 'Green'
                } else {
                    Write-Log ("    {0} is STILL absent from DEVPKEY_Device_Stack after restart-device; the wheel is still dead. Remaining step, by hand: Windows Settings -> Bluetooth and devices -> Remove device, power the mouse off and on, then Add device. This script never unpairs." -f $boundFilter) 'Red'
                    $repairFailed = $true
                }
            } else {
                $after = Get-ServiceSnapshot $boundFilter
                $svcSnaps[$boundFilter.ToLowerInvariant()] = $after
                if ($after.Running) {
                    Write-Log ("    {0} is now RUNNING (win32_exit={1}). Confirm the wheel by scrolling." -f $after.Name, $after.Win32Exit) 'Green'
                } elseif ($policyUnread) {
                    Write-Log ("    {0} is still {1} after restart-device. Code integrity policy could not be read (testsigning={2} HVCI={3}), so signing enforcement is UNKNOWN - neither confirmed nor ruled out. Check the System event log and setupapi.dev.log for {0}, and re-read the policy elevated before assuming a signing problem." -f $after.Name, $after.State, $testsigning, $hvci) 'Yellow'
                } elseif (-not $testsigningOn -or $hvciOn) {
                    Write-Log ("    {0} is still {1} after restart-device; measured code integrity policy (testsigning={2} HVCI={3}) rejects any driver that is not cross-signed. This run did not read {0}'s signature, so that is a policy fact, not the confirmed cause. If the installed build is self-signed, enable Test Mode or install a cross-signed build, then restart the device again." -f $after.Name, $after.State, $testsigning, $hvci) 'Red'
                } else {
                    Write-Log ("    {0} is still {1} after restart-device even though test signing is on and HVCI={2}. Not a signing problem - check the System event log and setupapi.dev.log for {0}." -f $after.Name, $after.State, $hvci) 'Red'
                }
            }
        }
    }
    Write-Log ''
    Write-Log '=== FILTER SERVICES AFTER REPAIR ===' 'Cyan'
    foreach ($name in $filterNames) {
        $s = Get-ServiceSnapshot $name
        $svcSnaps[$name.ToLowerInvariant()] = $s
        Write-Log ("  {0}: state={1} win32_exit={2}" -f $s.Name, $s.State, $s.Win32Exit)
    }
    Write-Log '  Never unpaired. Never FLIP:NoFilter. Never radio toggle. Never sc start on a PnP filter. USB phantoms left in place.'
}

$txt = Join-Path $OutDir "diagnose-and-recover-$ts.txt"
$Lines | Set-Content -LiteralPath $txt -Encoding UTF8
Write-Log ''
Write-Log "Saved: $txt" 'Green'

$appDir = Join-Path $env:APPDATA 'MagicMouseTray'
if (Test-Path -LiteralPath $appDir -PathType Container) {
    try {
        $copyTo = Join-Path $appDir ("diagnose-and-recover-$ts.txt")
        $srcFull = [IO.Path]::GetFullPath($txt)
        $dstFull = [IO.Path]::GetFullPath($copyTo)
        if (-not $srcFull.Equals($dstFull, [StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $txt -Destination $copyTo -Force
        }
    } catch { Write-Verbose "Failed to copy log file: $_" }
}

if ($Repair -and $repairFailed) { exit 1 }
exit 0
