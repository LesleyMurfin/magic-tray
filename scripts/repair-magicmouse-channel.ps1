#Requires -Version 5
<#
.SYNOPSIS
    Force the host-initiated Bluetooth reconnect that re-arms the v3 filter's
    control channel, then VERIFY the battery read instead of assuming it.

    A REPRIEVE, NOT A FIX: the channel is re-armed only for as long as this
    host-initiated L2CAP open lasts, so the next device-initiated reconnect
    (mouse power cycle, sleep, out-of-range) unarms it again and the percent
    goes back to 90 00 00 - and on a filter build carrying
    MmInvalidateClosedChannelStateLocked, every channel close does the same.
    The real fix is the inbound-ACL scratch gate in the driver repo,
    Driver.c:371 (LesleyMurfin/magic-mouse-v3-windows-fix, PR #39); nothing in
    this repo can repair it.

.DESCRIPTION
    The KMDF filter diverts an inbound ACL read onto a 78-byte scratch buffer
    only while its control-channel handle is armed. Armed happens on a
    host-initiated L2CAP open; a device-initiated reconnect never arms it, and
    from then on the GET_REPORT(Input, 0x90) battery pull comes back as
    [90 00 00] with Diag LastAclCapacity = 1 while every layer still reports
    success. A pnputil /restart-device on the live BTHENUM parent makes the
    host open the channel again, which is the whole of this mitigation.

    What it does: discovers the live BTHENUM parent for PID 0323, restarts it,
    waits for the COL02 collection to come back, reads HID Input report 0x90 on
    COL02, and reads the filter's Diag key. What it reports is the percent byte
    it actually got plus LastAclCapacity / LastAclReceived / LastOutHdr - never
    "exit 0 means done".

    What it will never do: install, sign, remove or swap a driver package,
    touch bcdedit / Inf2Cat / signtool / any kmdf-204-sign artefact, unpair the
    mouse, flip LowerFilters, or write a single file anywhere (no logs, no temp
    files, nothing on C:). It restarts one devnode and reports.

    CLI fallback, not a second truth: the in-app path for this restart is
    DeviceRepair.Apply (MagicMouseTray/DeviceRepair.cs:657). Its generated
    script runs the same pnputil /restart-device (DeviceRepair.cs:227) over the
    same live BTHENUM enumeration, skips the same usb\ and hid\vid_ phantoms
    (DeviceRepair.cs:190-195), elevates once through Verb=runas
    (DeviceRepair.cs:595-596) and polls the outcome for 10 s at 500 ms
    (DeviceRepair.cs:236-245). This script is the elevated-CLI sibling of that
    path, for a shell with no tray running - change one and change both.

    Missing key or missing value is reported as "unknown" and never as 0 - 0 is
    a real measurement here, same rule the tray's own reader follows
    (MagicMouseTray/DeviceDiagReader.cs:98-112).

.PARAMETER InstanceId
    Restart exactly this BTHENUM instance instead of the discovered one. Still
    gated: it must be a present BTHENUM HID-service node carrying the Apple VID
    and PID 0323.

.PARAMETER Elevate
    Re-launch this script elevated (Start-Process -Verb RunAs) and return the
    child's exit code. Off by default, because the repo's own convention is to
    refuse and tell the user (scripts/diagnose-and-recover.ps1:330-333); an
    already-elevated shell is the better way to run this, since the elevated
    child gets its own console window and its report is not visible here.

.PARAMETER ElevateTimeoutSeconds
    How long to wait for the elevated child before giving up. The wait is
    always bounded: an unanswered UAC prompt must not hang this script.

.PARAMETER SettleSeconds
    Deadline for the post-restart verification poll, 500 ms between attempts -
    the same 10 s / 500 ms shape DeviceRepair's generated script uses for its
    own post-restart check (MagicMouseTray/DeviceRepair.cs:236-245). Reading
    once would report a failure on a successful repair: the device is still
    re-enumerating right after the restart (measured MOUSE_RID90_FAILED err=21
    seconds before the same probe returned 18%), and the HID pipeline is not
    ready the instant COL02 appears - GLE=121 then GLE=21 then success at
    ~1000 ms (MagicMouseTray/ModeFlip.cs:1744-1746). STILL-BROKEN is only
    reported after this deadline expires.

.EXAMPLE
    .\scripts\repair-magicmouse-channel.ps1 -WhatIf   # plan + current state, no restart
    .\scripts\repair-magicmouse-channel.ps1           # elevated shell: restart + verify
    .\scripts\repair-magicmouse-channel.ps1 -Elevate  # unelevated shell: one UAC prompt

.NOTES
    Exit codes:
      0  REPAIRED           - restart ran and Input 0x90 on COL02 returned a
                              percent in 1..100 (a real read, e.g. 90-04-12 = 18%).
      1  STILL-BROKEN       - restart ran and Input 0x90 on COL02 still came back
                              with a zero percent byte WHILE the filter's own
                              touch-report counter was advancing: the mouse is
                              answering and the percent is being discarded.
      2  NOT-ELEVATED       - no Administrator token, or the UAC prompt was
                              declined/cancelled. Nothing was restarted.
      3  DEVICE-NOT-PRESENT - no present BTHENUM parent for PID 0323. Power the
                              mouse on, or re-pair it; this script never pairs.
      4  RESTART-FAILED     - pnputil /restart-device returned nonzero, or the
                              elevated child did not finish inside the timeout.
      5  UNVERIFIED         - the restart ran but the outcome could not be read:
                              no COL02 interface, the HID read failed, or a zeroed
                              report while the touch counter was NOT advancing -
                              which is exactly what an idle mouse returns
                              (docs/TEST-PLAN.md D23). Unknown, not a verdict.
      6  DRY-RUN            - -WhatIf: the plan and the current state were
                              printed and nothing was restarted.

    Why the verdict is NOT gated on Diag LastAclCapacity = 1: LastAclReceived,
    LastAclCapacity and LastAclBytes are a SINGLE "last inbound" slot shared by
    the HID control channel and the interrupt channel, and the mouse streams
    multitouch at roughly 65 reports/s, so a sample taken any meaningful time
    after the probe shows the interrupt channel's capacity 9 rather than the
    control channel's capacity 1. Measured on BOTH the broken and the restored
    filter builds: LastAclReceived=23, LastAclCapacity=9, LastAclBytes=A1-12-...
    So this script prints those values as opportunistic corroboration, treats
    their presence as confirmation, and never treats their absence as evidence
    of health.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstanceId,
    [switch]$Elevate,
    [int]$ElevateTimeoutSeconds = 120,
    [int]$SettleSeconds = 10
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$TargetPid = '0323'
$AppleBtVids = @('0001004c', '000205ac')
$HidUuidPrefix = '{00001124-0000-1000-8000-00805f9b34fb}'
$FilterService = 'MagicMouseDriver204Scroll'
$DiagSubKey = "SYSTEM\CurrentControlSet\Services\$FilterService\Diag"
$HidInterfaceClass = 'SYSTEM\CurrentControlSet\Control\DeviceClasses\{4d1e55b2-f16f-11cf-88cb-001111000030}'
$BatteryReportId = 0x90
# MouseBatteryDevice.ReadV3Rid90 uses max(InputReportByteLength, 64) and the
# live 0323 answers inside that floor (MagicMouseTray/MouseBatteryDevice.cs:138).
$ReportLength = 64
# Apple firmware reports 1..100; a successful read of exactly 0 is the zeroed
# report, never a level (MagicMouseTray/MouseBatteryDevice.cs:173-184).
$MinValidPercent = 1

$ExitRepaired = 0
$ExitStillBroken = 1
$ExitNotElevated = 2
$ExitDeviceNotPresent = 3
$ExitRestartFailed = 4
$ExitUnverified = 5
$ExitDryRun = 6

function Write-Log {
    param([string]$Text, [string]$Color = '')
    $line = "[repair-channel] $Text"
    if ($Color) { Write-Host $line -ForegroundColor $Color }
    else { Write-Host $line }
}

# Same shape as scripts/diagnose-and-recover.ps1:52-60.
function Test-IsAdmin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal $id
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

# The phantom rule, verbatim from the in-app path's own script
# (MagicMouseTray/DeviceRepair.cs:190-195): a USB charge-cable leftover or an
# HID\VID_ node is not the live Bluetooth stack and must never be restarted in
# its place.
function Test-SkipPath {
    param([string]$Full)
    $low = $Full.ToLowerInvariant()
    if ($low.StartsWith('usb\')) { return $true }
    if ($low.StartsWith('hid\vid_')) { return $true }
    return $false
}

function Test-IsTargetInstance {
    param([string]$Id)
    if ([string]::IsNullOrEmpty($Id)) { return $false }
    $low = $Id.ToLowerInvariant()
    if (Test-SkipPath $low) { return $false }
    # The HID service-class UUID gate keeps the {00001200-...} SDP sibling -
    # same PID, same VID, no stack - out of the target set, the same gate the
    # tray's own in-context derivation uses (MagicMouseTray/ModeFlip.cs:1018).
    if (-not $low.StartsWith('bthenum\' + $HidUuidPrefix)) { return $false }
    if (-not ($low.Contains('pid&' + $TargetPid) -or $low.Contains('pid_' + $TargetPid))) { return $false }
    foreach ($v in $AppleBtVids) {
        if ($low.Contains('vid&' + $v) -or $low.Contains('vid_' + $v)) { return $true }
    }
    return $false
}

# Present-only on purpose: a registered-but-absent devnode cannot be restarted
# into a live channel, and calling that "device not present" is the honest read.
function Get-TargetInstance {
    $devices = @(Get-PnpDevice -PresentOnly -ErrorAction Stop)
    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($d in $devices) {
        $id = [string]$d.InstanceId
        if (Test-IsTargetInstance $id) { [void]$ids.Add($id) }
    }
    return $ids
}

function ConvertTo-UInt32Value {
    param($Value)
    if ($null -eq $Value) { return $null }
    # REG_DWORD marshals to a signed int and these counters run past 2^31
    # (MagicMouseTray/DeviceDiagReader.cs:524-528).
    return [uint32](([int64][int]$Value) -band 0xFFFFFFFFL)
}

function Format-Unknown {
    param($Value)
    if ($null -eq $Value) { return 'unknown' }
    return [string]$Value
}

# Every value is nullable and a missing key or missing value stays $null all
# the way to the report: "unknown" is not 0.
function Get-DiagSnapshot {
    $snap = [ordered]@{
        KeyPresent = $false
        Rid12Count = $null
        AclTranslateCount = $null
        LastAclReceived = $null
        LastAclCapacity = $null
        LastOutHdr = $null
        LastOutBufferSize = $null
        LastAclBytes = $null
        Error = $null
    }
    $key = $null
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($DiagSubKey, $false)
        if ($null -eq $key) { return [pscustomobject]$snap }
        $snap.KeyPresent = $true
        $snap.Rid12Count = ConvertTo-UInt32Value $key.GetValue('Rid12Count', $null)
        $snap.AclTranslateCount = ConvertTo-UInt32Value $key.GetValue('AclTranslateCount', $null)
        $snap.LastAclReceived = ConvertTo-UInt32Value $key.GetValue('LastAclReceived', $null)
        $snap.LastAclCapacity = ConvertTo-UInt32Value $key.GetValue('LastAclCapacity', $null)
        $snap.LastOutHdr = ConvertTo-UInt32Value $key.GetValue('LastOutHdr', $null)
        $snap.LastOutBufferSize = ConvertTo-UInt32Value $key.GetValue('LastOutBufferSize', $null)
        $raw = $key.GetValue('LastAclBytes', $null)
        if ($raw -is [byte[]] -and $raw.Length -gt 0) { $snap.LastAclBytes = $raw }
    } catch {
        $snap.Error = $_.Exception.Message
    } finally {
        if ($null -ne $key) { $key.Dispose() }
    }
    return [pscustomobject]$snap
}

function Format-ByteString {
    param([byte[]]$Bytes, [int]$Count = 8)
    if ($null -eq $Bytes -or $Bytes.Length -eq 0) { return 'unknown' }
    $n = [Math]::Min($Bytes.Length, $Count)
    return [System.BitConverter]::ToString($Bytes, 0, $n)
}

function Write-DiagLine {
    param([string]$Label, $Diag)
    if (-not $Diag.KeyPresent) {
        $why = if ($Diag.Error) { $Diag.Error } else { "no $FilterService\Diag key" }
        Write-Log ("  {0} Diag: unknown ({1})" -f $Label, $why) 'Yellow'
        return
    }
    Write-Log ("  {0} Diag: LastAclCapacity={1} LastAclReceived={2} LastOutHdr={3} LastOutBufferSize={4}" -f `
        $Label,
        (Format-Unknown $Diag.LastAclCapacity),
        (Format-Unknown $Diag.LastAclReceived),
        (Format-Unknown $Diag.LastOutHdr),
        (Format-Unknown $Diag.LastOutBufferSize))
    Write-Log ("  {0} Diag: Rid12Count={1} AclTranslateCount={2} LastAclBytes={3}" -f `
        $Label,
        (Format-Unknown $Diag.Rid12Count),
        (Format-Unknown $Diag.AclTranslateCount),
        (Format-ByteString $Diag.LastAclBytes))
}

# Counter movement is the only positive proof that the mouse is being touched,
# and stillness is unknown rather than health - the same discipline as
# DeviceDiagReader.MultitouchAdvancing (MagicMouseTray/DeviceDiagReader.cs:117-147).
# AclTranslateCount first, Rid12Count as the fallback for a filter build that
# does not publish it (DeviceDiagReader.cs:505-521). $null when either sample is
# unreadable or the counter went BACKWARDS: a driver reinstall resets every Diag
# counter to 0, and a reset is not a stalled stream.
function Test-TouchStreamAdvanced {
    param($Before, $After)
    if ($null -eq $Before -or $null -eq $After) { return $null }
    if (-not $Before.KeyPresent -or -not $After.KeyPresent) { return $null }
    foreach ($name in @('AclTranslateCount', 'Rid12Count')) {
        $a = $Before.$name
        $b = $After.$name
        if ($null -eq $a -or $null -eq $b) { continue }
        if ([uint64]$b -lt [uint64]$a) { return $null }
        return ([uint64]$b -gt [uint64]$a)
    }
    return $null
}

$HidReaderSource = @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class MagicMouseChannelHid
{
    const uint FILE_SHARE_READ  = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint OPEN_EXISTING    = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetInputReport(SafeFileHandle HidDeviceObject,
        byte[] ReportBuffer, int ReportBufferLength);

    // Zero desired access, exactly as the tray opens these interfaces: it reads
    // the report without GENERIC_READ and so without elevation, and avoids err=5
    // on a mouhid-owned interface (MagicMouseTray/MouseBatteryDevice.cs:106-109).
    public static byte[] ReadInputReport(string path, byte reportId, int length, out int error)
    {
        error = 0;
        using (SafeFileHandle handle = CreateFile(path, 0,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
        {
            if (handle.IsInvalid) { error = Marshal.GetLastWin32Error(); return null; }
            byte[] buffer = new byte[length];
            buffer[0] = reportId;
            if (!HidD_GetInputReport(handle, buffer, buffer.Length))
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }
            return buffer;
        }
    }
}
'@

function Initialize-HidReader {
    if (-not ('MagicMouseChannelHid' -as [type])) {
        Add-Type -TypeDefinition $HidReaderSource -Language CSharp
    }
}

# The COL02 device-interface paths for this PID, read from the HID interface
# class key: a subkey named ##?#HID#...&Col02#...#{guid} IS the symbolic link
# with '#' for '\', so no SetupDi enumeration is needed to build it.
function Get-Col02InterfacePath {
    $paths = New-Object System.Collections.Generic.List[string]
    $root = $null
    try {
        $root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($HidInterfaceClass, $false)
        if ($null -eq $root) { return $paths }
        foreach ($name in $root.GetSubKeyNames()) {
            $low = $name.ToLowerInvariant()
            if (-not $low.StartsWith('##?#hid#')) { continue }
            if (-not $low.Contains('col02')) { continue }
            if (-not ($low.Contains('pid&' + $TargetPid) -or $low.Contains('pid_' + $TargetPid))) { continue }
            $vidOk = $false
            foreach ($v in $AppleBtVids) {
                if ($low.Contains('vid&' + $v) -or $low.Contains('vid_' + $v)) { $vidOk = $true }
            }
            if (-not $vidOk) { continue }
            [void]$paths.Add($name.Replace('#', '\'))
        }
    } catch {
        Write-Log ("  COL02 interface lookup failed: {0}" -f $_.Exception.Message) 'Yellow'
    } finally {
        if ($null -ne $root) { $root.Dispose() }
    }
    return $paths
}

# Returns Percent (1..100), Zeroed (a well-formed [90 00 00]), Bytes, Path and
# Error. A failed read stays a failed read - it is never reported as 0%.
function Read-Col02Battery {
    param([int]$BudgetSeconds)
    $result = [pscustomobject]@{
        Percent = $null
        Zeroed = $false
        Bytes = $null
        Path = $null
        Error = $null
    }
    Initialize-HidReader
    $deadline = (Get-Date).AddSeconds([Math]::Max($BudgetSeconds, 1))
    $lastError = 'no COL02 interface for PID 0323'
    while ($true) {
        foreach ($path in @(Get-Col02InterfacePath)) {
            $err = 0
            $buf = $null
            try {
                $buf = [MagicMouseChannelHid]::ReadInputReport($path, [byte]$BatteryReportId, $ReportLength, [ref]$err)
            } catch {
                $lastError = $_.Exception.Message
                continue
            }
            if ($null -eq $buf) {
                $lastError = "HidD_GetInputReport/CreateFile err=$err on $path"
                continue
            }
            $result.Bytes = $buf
            $result.Path = $path
            if ($buf[0] -ne [byte]$BatteryReportId) {
                $lastError = ("report id 0x{0:X2}, expected 0x90" -f $buf[0])
                continue
            }
            $pct = [int]$buf[2]
            if ($pct -ge $MinValidPercent -and $pct -le 100) {
                $result.Percent = $pct
                return $result
            }
            # A well-formed report whose percent byte is 0 - the truncation
            # signature, and also what an idle mouse produces. The caller needs
            # the Diag fingerprint to tell those apart.
            $result.Zeroed = $true
            $lastError = 'zeroed 0x90 report'
        }
        if ((Get-Date) -ge $deadline) { break }
        Start-Sleep -Milliseconds 500
    }
    $result.Error = $lastError
    return $result
}

# `& $exe @args` followed by `return $LASTEXITCODE` returns the tool's whole
# transcript WITH the code appended, so the caller's `-ne 0` test compares an
# ARRAY and a clean restart reads as a failure. The native output goes to the
# host here and the ONLY thing on the pipeline is the int.
function Invoke-PnpRestart {
    param([string]$Id)
    & pnputil.exe /restart-device "$Id" | ForEach-Object { Write-Log ("  pnputil: {0}" -f $_) }
    $code = $LASTEXITCODE
    if ($null -eq $code) { return -1 }
    return [int]$code
}

function Invoke-SelfElevated {
    $host_ = $null
    try {
        $host_ = (Get-Process -Id $PID).Path
    } catch {
        $host_ = $null
    }
    if ([string]::IsNullOrEmpty($host_)) {
        Write-Log 'Cannot resolve this PowerShell host executable to re-launch it elevated. Open an elevated PowerShell and run this script there.' 'Red'
        return $ExitNotElevated
    }
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
        '-SettleSeconds', [string]$SettleSeconds)
    if ($InstanceId) { $argList += @('-InstanceId', $InstanceId) }

    Write-Log 'Requesting Administrator permission (one UAC prompt). The elevated run reports in its OWN console window.'
    $proc = $null
    try {
        $proc = Start-Process -FilePath $host_ -ArgumentList $argList -Verb RunAs -PassThru -ErrorAction Stop
    } catch [System.ComponentModel.Win32Exception] {
        # ShellExecute reports a declined UAC prompt as native error 1223
        # (ERROR_CANCELLED); the message text is localised, the code is the test
        # (MagicMouseTray/DriverInstaller.cs:951-960).
        if ($_.Exception.NativeErrorCode -eq 1223) {
            Write-Log 'The Administrator prompt was declined, so nothing was restarted. Approve the prompt, or run this script from an already-elevated PowerShell.' 'Red'
        } else {
            Write-Log ("Elevation failed: {0}" -f $_.Exception.Message) 'Red'
        }
        return $ExitNotElevated
    } catch [System.InvalidOperationException] {
        # Observed verbatim in an automated session: InvalidOperationException
        # "The operation was canceled by the user." A session with no interactive
        # desktop can never answer UAC, so this is surfaced as not-elevated and
        # never retried in a loop.
        Write-Log ("Elevation was cancelled: {0}" -f $_.Exception.Message) 'Red'
        Write-Log 'An automated or non-interactive session cannot answer a UAC prompt. Run this script from an already-elevated PowerShell instead of using -Elevate.' 'Red'
        return $ExitNotElevated
    } catch {
        Write-Log ("Elevation failed: {0}" -f $_.Exception.Message) 'Red'
        return $ExitNotElevated
    }
    if ($null -eq $proc) {
        Write-Log 'The Administrator prompt returned no process, so nothing was restarted. Run this script from an already-elevated PowerShell.' 'Red'
        return $ExitNotElevated
    }
    # Always bounded: an unanswered prompt must not hang this script.
    if (-not $proc.WaitForExit($ElevateTimeoutSeconds * 1000)) {
        Write-Log ("The elevated run did not finish within {0}s. It may still be running in its own window; check that window for the verdict." -f $ElevateTimeoutSeconds) 'Red'
        return $ExitRestartFailed
    }
    $code = [int]$proc.ExitCode
    Write-Log ("Elevated run exited {0}." -f $code)
    return $code
}

# --- Report ---------------------------------------------------------------

$dryRun = [bool]$WhatIfPreference
Write-Log ("=== repair-magicmouse-channel {0} ===" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) 'Cyan'
Write-Log 'Temporary mitigation. It re-arms the filter control channel for this connection only; the next device-initiated reconnect unarms it again. Real fix: Driver.c:371, magic-mouse-v3-windows-fix PR #39.'
Write-Log ("Admin={0} DryRun={1} Elevate={2} SettleSeconds={3} ElevateTimeoutSeconds={4}" -f `
    (Test-IsAdmin), $dryRun, [bool]$Elevate, $SettleSeconds, $ElevateTimeoutSeconds)

$before = Get-DiagSnapshot
Write-DiagLine 'before' $before
$batteryBefore = Read-Col02Battery -BudgetSeconds 1
if ($null -ne $batteryBefore.Percent) {
    Write-Log ("  before battery: {0}% bytes={1}" -f $batteryBefore.Percent, (Format-ByteString $batteryBefore.Bytes 3)) 'Green'
} elseif ($batteryBefore.Zeroed) {
    Write-Log ("  before battery: zeroed report bytes={0}" -f (Format-ByteString $batteryBefore.Bytes 3)) 'Yellow'
} else {
    Write-Log ("  before battery: unknown ({0})" -f (Format-Unknown $batteryBefore.Error)) 'Yellow'
}
if ($before.KeyPresent -and $before.LastOutHdr -eq 0x41 -and $before.LastAclCapacity -eq 1 -and
    $null -ne $before.LastAclReceived -and $before.LastAclReceived -ge 4) {
    Write-Log '  fingerprint: battery truncation PRESENT (LastOutHdr=0x41, LastAclCapacity=1, LastAclReceived>=4) - the percent was on the wire and 1 byte was copied back.' 'Red'
}

# --- Target ---------------------------------------------------------------

$targets = @()
try {
    $targets = @(Get-TargetInstance)
} catch {
    Write-Log ("Device enumeration failed: {0}" -f $_.Exception.Message) 'Red'
    Write-Log 'Presence is UNKNOWN, and unknown is not a verdict. Nothing was restarted.' 'Red'
    exit $ExitUnverified
}
if ($InstanceId) {
    if (-not (Test-IsTargetInstance $InstanceId)) {
        Write-Log ("REFUSE {0} - not a present BTHENUM HID node for PID {1} with an Apple VID." -f $InstanceId, $TargetPid) 'Red'
        exit $ExitDeviceNotPresent
    }
    if ($targets -notcontains $InstanceId) {
        Write-Log ("{0} is not present right now. Power the mouse on and try again." -f $InstanceId) 'Red'
        exit $ExitDeviceNotPresent
    }
    $targets = @($InstanceId)
}
if ($targets.Count -eq 0) {
    Write-Log ("No present BTHENUM parent for PID {0}. Power the mouse on; if it is not paired at all, use Windows Settings -> Bluetooth -> Add device. This script never pairs and never unpairs." -f $TargetPid) 'Red'
    exit $ExitDeviceNotPresent
}
foreach ($t in $targets) { Write-Log ("  target: {0}" -f $t) }

# --- Elevation ------------------------------------------------------------

if (-not $dryRun -and -not (Test-IsAdmin)) {
    if ($Elevate) {
        exit (Invoke-SelfElevated)
    }
    Write-Log 'ERROR: this script requires an elevated PowerShell - pnputil /restart-device cannot run without it. Nothing was restarted.' 'Red'
    Write-Log 'Run it from an elevated PowerShell, or pass -Elevate to get one UAC prompt. Use -WhatIf to see the plan without elevation.' 'Red'
    exit $ExitNotElevated
}

# --- Restart --------------------------------------------------------------

$restarted = 0
$failed = $false
foreach ($id in $targets) {
    Write-Log ("  pnputil /restart-device {0}" -f $id)
    if (-not $PSCmdlet.ShouldProcess($id, 'pnputil /restart-device')) { continue }
    $code = Invoke-PnpRestart $id
    if ($code -ne 0) {
        Write-Log ("    exit={0}" -f $code) 'Red'
        $failed = $true
    } else {
        Write-Log '    exit=0' 'Green'
        $restarted = $restarted + 1
    }
}

if ($dryRun) {
    Write-Log 'DRY RUN: nothing was restarted and nothing was written. Re-run elevated without -WhatIf to apply the reprieve.' 'Cyan'
    exit $ExitDryRun
}
if ($restarted -eq 0) {
    if ($failed) {
        Write-Log 'restart-device failed on every target; the channel was not re-armed.' 'Red'
        exit $ExitRestartFailed
    }
    Write-Log 'No restart was performed (the action was declined), so nothing changed.' 'Yellow'
    exit $ExitDryRun
}

# --- Verify ---------------------------------------------------------------

Write-Log ("Restarted {0} instance(s); waiting up to {1}s for COL02 to answer." -f $restarted, $SettleSeconds)
$battery = Read-Col02Battery -BudgetSeconds $SettleSeconds
# Sampled IMMEDIATELY after the probe: that is the only moment the control
# channel's own capacity can still be sitting in the shared last-inbound slot.
$after = Get-DiagSnapshot
Write-DiagLine 'after' $after

if ($null -ne $battery.Percent) {
    Write-Log ("REPAIRED: battery {0}% bytes={1} (Input 0x90 on COL02)." -f $battery.Percent, (Format-ByteString $battery.Bytes 6)) 'Green'
    Write-Log 'Reprieve only: the next device-initiated reconnect unarms the channel again and the percent returns to 90 00 00. The fix is Driver.c:371 (magic-mouse-v3-windows-fix PR #39).' 'Yellow'
    if ($failed) { Write-Log '  (one or more other instances failed to restart; the battery still reads, so this is reported as repaired.)' 'Yellow' }
    exit $ExitRepaired
}

if (-not $battery.Zeroed) {
    Write-Log ("UNVERIFIED: the restart ran but the battery could not be read ({0}). Unknown is not a verdict - re-run once the mouse has reconnected." -f (Format-Unknown $battery.Error)) 'Yellow'
    if ($failed) { Write-Log '  one or more instances also failed to restart.' 'Red' }
    exit $ExitUnverified
}

# A zeroed 0x90 alone decides nothing: an idle mouse returns the same bytes
# (docs/TEST-PLAN.md D23). The discriminator is whether the touch stream is
# advancing while the percent comes back zero.
Write-Log ("Zeroed report bytes={0}. Sampling the filter's touch counter for 3s - KEEP TOUCHING THE MOUSE (rest two fingers on it), because a still counter on an untouched mouse proves nothing." -f (Format-ByteString $battery.Bytes 3)) 'Yellow'
Start-Sleep -Seconds 3
$touchAfter = Get-DiagSnapshot
$advancing = Test-TouchStreamAdvanced $after $touchAfter

if ($advancing -eq $true) {
    Write-Log ("STILL BROKEN: bytes={0} while the touch counter advanced (AclTranslateCount={1} Rid12Count={2}) - the mouse is streaming and the percent is being discarded. The restart did not re-arm the control channel." -f `
        (Format-ByteString $battery.Bytes 3), (Format-Unknown $touchAfter.AclTranslateCount), (Format-Unknown $touchAfter.Rid12Count)) 'Red'
    if ($after.LastOutHdr -eq 0x41 -and $after.LastAclCapacity -eq 1) {
        Write-Log ("  corroborated: LastOutHdr=0x41 with LastAclCapacity=1 and LastAclReceived={0}, caught in the shared slot right after the probe." -f (Format-Unknown $after.LastAclReceived)) 'Red'
    } else {
        Write-Log ("  no corroboration in the shared last-inbound slot (LastAclCapacity={0}) - expected on a live link, and NOT evidence of health." -f (Format-Unknown $after.LastAclCapacity)) 'Yellow'
    }
    Write-Log 'Nothing in this repo can fix that. The gate is Driver.c:371 in LesleyMurfin/magic-mouse-v3-windows-fix (PR #39).' 'Red'
    exit $ExitStillBroken
}

$why = if ($null -eq $advancing) { 'the touch counter could not be read, or it went backwards (a driver reinstall resets every Diag counter)' } else { 'the touch counter did not advance, so the mouse was not being touched' }
Write-Log ("UNVERIFIED: bytes={0} but {1}. An idle mouse returns the same zeroed report (docs/TEST-PLAN.md D23), so this is unknown and not a verdict - rest two fingers on the mouse and re-run." -f (Format-ByteString $battery.Bytes 3), $why) 'Yellow'
if ($failed) { Write-Log '  one or more instances also failed to restart.' 'Red' }
exit $ExitUnverified
