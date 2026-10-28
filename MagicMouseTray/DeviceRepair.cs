// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace MagicMouseTray;

// Two auto-fixes for "the wheel is dead", both elevated and both aimed at the
// live BTHENUM stack only.
//
// 1. BuildScript / Apply - the bound lower filter is not running, so the HID
//    stack loaded without scroll. Exactly one stack action:
//      pnputil /restart-device <BTHENUM instance>   for every live instance
//
// 2. BuildRemoveStaleFiltersScript / ApplyRemoveStaleFilters - TWO rival
//    versions of the same vendor filter are registered on one stack (a stale
//    name left on the device key next to the running one on the instance key).
//    A restart alone cannot fix that, because the stale name stays registered:
//    the stale name is removed from the LowerFilters value that carries it -
//    every touched key and its previous values are reported through the status
//    sidecar BEFORE the first write, and the tray copies them out of the
//    sidecar into FilterBackupPath - and only then is the stack restarted. It
//    refuses outright if the working filter would not survive.
//
// There is deliberately no "sc start <filter>" here. A PnP device lower filter
// is loaded by PnP when the device stack is built, never by the SCM, so
// sc start on it legitimately fails with WIN32_EXIT_CODE 31 and proves nothing
// about signing or blocking. Restarting the device is what makes PnP load the
// bound filter; the service state is then only read back as verification.
//
// Deliberately NOT done here: no unpair, no /enable-device or /disable-device,
// no Bluetooth radio work, no driver installers, and nothing is ever aimed at a
// USB\ or HID\VID_ node - those are charge-cable phantoms (CM_PROB_PHANTOM),
// not the live Bluetooth stack. Only BTHENUM is enumerated.
//
// Same elevation shape as DeviceEnable: generated .ps1 in %TEMP%, ShellExecute
// with Verb=runas, and a status sidecar file because ExitCode is unavailable
// once UseShellExecute is true.

// The window this file is judged on: between the first LowerFilters write and
// a completed stack restart the registry names a different filter set than the
// loaded stack, which is worse than the state the repair was called for. So
// the script marks a STAGE and the write counts into the sidecar as it goes,
// the tray maps that report onto an outcome carrying an explicit registry
// dimension, and the script never exits between a write and the restart loop.
internal enum RepairOutcome
{
    Ok,
    NoInstances,
    FilterBlocked,
    Failed,
    // At least one LowerFilters value was - or may have been - rewritten and
    // the pass then stopped, so the registry and the loaded stack can
    // disagree. Never the same answer as a repair that changed nothing.
    StoppedAfterWrite,
    // The poller hit ElevatedCeiling and killed the elevated tree: the script
    // was cut off mid-pass, which is not a script that ran to the end and
    // reported a failure.
    TimedOut,
}

// What the attempt is known to have done to HKLM. Unknown is its own answer
// and is never rounded down to Untouched: "nothing was written" is a promise
// only a never-started script, or one that stopped before its write pass, can
// make.
internal enum RegistryState
{
    Untouched,
    Modified,
    Unknown,
}

// Stage markers the generated scripts write around their passes, in the order
// they are reached. Writing and Written straddle the write pass, so a report
// that stops mid-pass still says which side of the first write it stopped on.
internal enum RepairStage
{
    Running,
    Scanned,
    Writing,
    Written,
    Restarted,
    Verified,
}

// One parsed status report. A null RepairProgress means the script never wrote
// a readable stage - which is NOT evidence that it wrote nothing.
internal readonly record struct RepairProgress(
    string Result,
    RepairStage Stage,
    int KeysPlanned,
    int KeysWritten,
    int WriteFailures,
    int RestartFailures);

// Everything the tray knows about one elevated attempt. Started=false means
// ShellExecute handed back no process at all; TimedOut means the poller killed
// the tree at ElevatedCeiling rather than reading a final report.
//
// HasWritePass is a fact about the SCRIPT that ran, not about its report: the
// restart-only script (BuildScript) contains no Set-ItemProperty and no
// Remove-ItemProperty at all, so for that attempt "nothing was written" is
// provable whatever the report says - or does not say. It defaults to true
// because the conservative answer for a script that might write is Unknown,
// never a promise that HKLM is clean.
internal readonly record struct RepairEvidence(
    bool Started,
    RepairProgress? Progress,
    int? Exit,
    bool TimedOut,
    bool HasWritePass = true);

// The outcome plus the registry dimension, which is independent of it: a
// blocked filter or a ceiling kill can sit on top of a rewritten LowerFilters.
internal readonly record struct RepairResult(
    RepairOutcome Outcome,
    RegistryState Registry,
    string Detail)
{
    internal bool Succeeded => Outcome == RepairOutcome.Ok;
}

// One key the repair touched and the exact LowerFilters list it held before.
// The values are whatever the registry carried, not names this code vetted, so
// they are recorded and read back verbatim.
internal sealed record FilterBackupEntry(string KeyPath, IReadOnlyList<string> PreviousValues);

internal static class DeviceRepair
{
    internal const int NoInstancesExitCode = 2;
    internal const int FilterBlockedExitCode = 3;

    // Named after the ATTEMPT and the PROCESS, not just the device. Both
    // repairs can be in flight for the same PID at once: TrayApp's
    // StartRepairApply and StartStaleFilterRemoval each launch straight into
    // Task.Run, and RunDriverActionAsync does not serialize driver actions -
    // so a second Apply (or a second ApplyRemoveStaleFilters) can overlap the
    // first. Under a PID-only name the poller in LaunchElevated cannot tell
    // whose report it read - it accepts the first recognised token at that
    // path - so it would map another elevated process's result onto this
    // attempt, and the File.Delete in LaunchElevated cannot close that hole
    // because the other attempt is free to write the path after the delete
    // succeeds. Same collision and the same fix as ModeFlip's cycle nonce
    // (ModeFlip.cs:225-237).
    //
    // Two segments, two independent guarantees: AttemptNonce.Next() is
    // monotonic within one process and separates attempts inside this tray,
    // Environment.ProcessId separates two trays that mint in the same
    // millisecond (ModeFlip.cs:253-257 records that race).
    internal static string StatusSidecarPath(string pid, long nonce) =>
        Path.Combine(
            Path.GetTempPath(),
            $"mm-repair-{pid.ToLowerInvariant()}-{ProcIdSegment()}-{ValidateNonce(nonce)}.status");

    internal static string FiltersStatusSidecarPath(string pid, long nonce) =>
        Path.Combine(
            Path.GetTempPath(),
            $"mm-repair-filters-{pid.ToLowerInvariant()}-{ProcIdSegment()}-{ValidateNonce(nonce)}.status");

    // The bound filter name is whatever LowerFilters actually holds on the
    // device - e.g. "MagicMouseDriver204Scroll", a KMDF-family variant of the
    // catalog constant. Accept the KMDF and Apple families by case-insensitive
    // prefix, restrict the characters to what a service key can legally hold,
    // and reject everything else so no unreviewed name reaches an elevated
    // shell or breaks out of the single-quoted PowerShell literal.
    static string ValidateFilterServiceName(string filterServiceName)
    {
        if (string.IsNullOrEmpty(filterServiceName))
            throw new InvalidOperationException("DeviceRepair needs a filter service name.");
        var known =
            filterServiceName.StartsWith(DriverPackageCatalog.PatchedKmdfServiceName, StringComparison.OrdinalIgnoreCase)
            || filterServiceName.StartsWith(DriverPackageCatalog.AppleFilterServiceName, StringComparison.OrdinalIgnoreCase);
        if (!known)
            throw new InvalidOperationException($"DeviceRepair refuses service '{filterServiceName}'.");
        foreach (var c in filterServiceName)
        {
            var ok = c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or '-' or '.';
            if (!ok)
                throw new InvalidOperationException($"DeviceRepair refuses service '{filterServiceName}'.");
        }
        return filterServiceName;
    }

    static string ValidatePid(string pid)
    {
        if (string.IsNullOrEmpty(pid) || pid.Length != 4)
            throw new InvalidOperationException("DeviceRepair needs a 4-hex PID.");
        foreach (var c in pid)
        {
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
                throw new InvalidOperationException($"DeviceRepair needs a 4-hex PID, got '{pid}'.");
        }
        return pid.ToLowerInvariant();
    }

    // The nonce crosses the elevation boundary as part of a file name on both
    // sides, so it is rendered as plain digits and nothing else.
    static string ValidateNonce(long nonce)
    {
        if (nonce <= 0)
            throw new InvalidOperationException($"DeviceRepair needs a positive attempt nonce, got {nonce}.");
        return nonce.ToString();
    }

    // Same rule for the process id: it is the other half of the same file
    // name, so it is validated and rendered the same way.
    static string ProcIdSegment()
    {
        var procId = Environment.ProcessId;
        if (procId <= 0)
            throw new InvalidOperationException($"DeviceRepair needs a positive process id, got {procId}.");
        return procId.ToString();
    }

    // The names to unregister. Every one has to pass the same family + charset
    // gate as the filter that stays, must not be the filter that stays, and
    // must not repeat - a duplicate would mean the caller built the list from
    // something other than the live candidate set.
    static string[] ValidateRemoveList(string keepService, string[]? removeServices)
    {
        if (removeServices is null || removeServices.Length == 0)
            throw new InvalidOperationException("DeviceRepair needs at least one filter name to remove.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(removeServices.Length);
        foreach (var raw in removeServices)
        {
            var name = ValidateFilterServiceName(raw);
            if (name.Equals(keepService, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"DeviceRepair refuses to remove the filter it must keep '{name}'.");
            if (!seen.Add(name))
                throw new InvalidOperationException($"DeviceRepair refuses duplicate remove entry '{name}'.");
            names.Add(name);
        }
        return [.. names];
    }

    static string PowerShellArrayLiteral(IEnumerable<string> values) =>
        string.Join(", ", values.Select(v => "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'"));

    internal static string BuildScript(string pid, long nonce, string filterServiceName)
    {
        pid = ValidatePid(pid);
        var svc = ValidateFilterServiceName(filterServiceName);
        var needles = DeviceEnable.VidNeedlesForPid(pid);
        if (needles.Length == 0)
            throw new InvalidOperationException($"No catalog VID for pid={pid}.");
        var vidLiteral = string.Join(", ", needles.Select(v =>
            "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'"));
        // "$id" is required: BTHENUM instance IDs contain '&'. Unquoted,
        // PowerShell treats '&' as the call operator and pnputil never runs.
        var template = """
$ErrorActionPreference = 'Continue'
$targetPid = '__PID__'
$procId = '__PROCID__'
$nonce = '__NONCE__'
$svc = '__SVC__'
$pidA = 'PID_' + $targetPid
$pidB = 'PID&' + $targetPid
$vidNeedles = @(__VIDS__)
$ids = New-Object System.Collections.Generic.List[string]
$statusFile = Join-Path $env:TEMP ('mm-repair-' + $targetPid + '-' + $procId + '-' + $nonce + '.status')

# The status sidecar is the ONLY channel that outlives this console window:
# UseShellExecute is on, so the tray cannot redirect stdout and every Write-Host
# line below dies with the window. Each progress line is appended, so the stage
# and the counts at the end of the file are the ones this run reached. This
# repair restarts the stack and nothing else, so its write counts stay at zero.
Set-Content -LiteralPath $statusFile -Encoding UTF8 -Value 'stage=running'
function Write-Status([string]$line) {
    Add-Content -LiteralPath $statusFile -Encoding UTF8 -Value $line
}
Write-Status 'result=running'
Write-Status 'planned=0'
Write-Status 'written=0'
Write-Status 'write-failures=0'
Write-Status 'restart-failures=0'

function Test-Vid([string]$n) {
    $low = $n.ToLowerInvariant()
    foreach ($v in $vidNeedles) {
        if ($low.Contains($v.ToLowerInvariant())) { return $true }
    }
    return $false
}

function Test-SkipPath([string]$full) {
    $low = $full.ToLowerInvariant()
    if ($low.StartsWith('usb\')) { return $true }
    if ($low.StartsWith('hid\vid_')) { return $true }
    return $false
}

$root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Enum\BTHENUM')
if ($root) {
    foreach ($sub in $root.GetSubKeyNames()) {
        $low = $sub.ToLowerInvariant()
        if (-not ($low.Contains($pidA.ToLowerInvariant()) -or $low.Contains($pidB.ToLowerInvariant()))) { continue }
        if (-not (Test-Vid $sub)) { continue }
        $dev = $root.OpenSubKey($sub)
        if (-not $dev) { continue }
        foreach ($inst in $dev.GetSubKeyNames()) {
            $full = 'BTHENUM\' + $sub + '\' + $inst
            if (Test-SkipPath $full) { continue }
            [void]$ids.Add($full)
        }
        $dev.Dispose()
    }
    $root.Dispose()
}

Write-Status 'stage=scanned'
if ($ids.Count -eq 0) {
    Write-Host "DEVICE_REPAIR no BTHENUM instances pid=$targetPid"
    Write-Status 'result=no-instances'
    exit 2
}

# The only stack action. PnP rebuilds the device stack and loads the bound
# lower filter; nothing here starts a service, because a device lower filter
# is not startable through the SCM (sc start returns WIN32_EXIT_CODE 31).
$failed = 0
foreach ($id in $ids) {
    Write-Host "DEVICE_REPAIR restart $id"
    & pnputil.exe /restart-device "$id"
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
# Counts first, marker last: the tray reads this file last-line-wins while the
# script is still appending to it, so a read that lands between the two lines
# must not see the new marker beside the header's seeded zero.
Write-Status ("restart-failures=" + $failed)
Write-Status 'stage=restarted'
if ($failed -ne 0) {
    Write-Host "DEVICE_REPAIR restart failures=$failed"
    Write-Status 'result=failed'
    exit 1
}

# PnP loads the filter asynchronously, so poll instead of reading once.
$deadline = (Get-Date).AddSeconds(10)
$queryOut = ''
$running = $false
while ($true) {
    $queryOut = (& sc.exe query "$svc" 2>&1 | Out-String)
    if ($queryOut -match 'RUNNING') { $running = $true; break }
    if ((Get-Date) -ge $deadline) { break }
    Start-Sleep -Milliseconds 500
}
Write-Host "DEVICE_REPAIR sc query svc=$svc running=$running"
Write-Host $queryOut
if ($running) {
    Write-Status 'stage=verified'
    Write-Status 'result=ok'
    exit 0
}

# Still not running. Only now is signing policy relevant: a self-signed KMDF
# filter cannot load without test signing, and HVCI blocks it regardless.
$startOptions = ''
try {
    $startOptions = [string](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name SystemStartOptions -ErrorAction Stop).SystemStartOptions
} catch {
    $startOptions = ''
}
$hvci = 'unavailable'
$hvciOn = $false
try {
    $dg = Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction Stop
    $svcRunning = @($dg.SecurityServicesRunning)
    $hvci = ($svcRunning -join ',')
    if ($svcRunning -contains 2) { $hvciOn = $true }
} catch {
    $hvci = 'unavailable'
}
$testSigning = $startOptions.ToUpperInvariant().Contains('TESTSIGNING')
Write-Host "DEVICE_REPAIR startoptions=$startOptions"
Write-Host "DEVICE_REPAIR testsigning=$testSigning hvci=$hvci hvcion=$hvciOn"
if ((-not $testSigning) -or $hvciOn) {
    Write-Host "DEVICE_REPAIR filter blocked svc=$svc"
    Write-Status 'result=filter-blocked'
    exit 3
}
Write-Host "DEVICE_REPAIR filter not running svc=$svc with testsigning on and hvci off"
Write-Status 'result=failed'
exit 1
""";
        var script = template
            .Replace("__PID__", pid, StringComparison.Ordinal)
            .Replace("__PROCID__", ProcIdSegment(), StringComparison.Ordinal)
            .Replace("__NONCE__", ValidateNonce(nonce), StringComparison.Ordinal)
            .Replace("__SVC__", svc, StringComparison.Ordinal)
            .Replace("__VIDS__", vidLiteral, StringComparison.Ordinal);
        foreach (var name in DeviceEnable.ForbiddenNames)
        {
            if (script.Contains(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"DeviceRepair refuses {name}.");
        }
        return script;
    }

    // Second repair shape: TWO rival versions of the same vendor lower filter
    // are registered on one BTHENUM stack - e.g. a stale "MagicMouseDriver"
    // left on the DEVICE key by an older install next to the running
    // "MagicMouseDriver204Scroll" on the INSTANCE key. Windows applies both
    // values to the same stack, so PnP is told to load a filter whose service
    // is Stopped. Restarting alone cannot fix that: the stale name stays
    // registered. The only repair is to unregister the stale name and rebuild
    // the stack.
    //
    // Bounds of the generated script, all enforced in the script itself and
    // not just here: it touches nothing but LowerFilters values under
    // Enum\BTHENUM for this PID, drops only the names passed in (never mouhid,
    // never HidBth, never an unlisted vendor filter), refuses when keepService
    // would not survive anywhere on the stack, never writes an empty
    // MultiString, and reports every key and its previous values through the
    // status sidecar before the first write - the tray copies those lines into
    // FilterBackupPath, which is where the edit is undone by hand from, since
    // the Write-Host transcript dies with the elevated console window. No
    // unpair, no radio, no FLIP, no /enable-device or /disable-device, no
    // service key, no .sys.
    internal static string BuildRemoveStaleFiltersScript(
        string pid, long nonce, string keepService, string[] removeServices)
    {
        pid = ValidatePid(pid);
        var keep = ValidateFilterServiceName(keepService);
        var remove = ValidateRemoveList(keep, removeServices);
        var needles = DeviceEnable.VidNeedlesForPid(pid);
        if (needles.Length == 0)
            throw new InvalidOperationException($"No catalog VID for pid={pid}.");
        var vidLiteral = PowerShellArrayLiteral(needles);
        var removeLiteral = PowerShellArrayLiteral(remove);
        // "$id" is required: BTHENUM instance IDs contain '&'. Unquoted,
        // PowerShell treats '&' as the call operator and pnputil never runs.
        var template = """
$ErrorActionPreference = 'Continue'
$targetPid = '__PID__'
$procId = '__PROCID__'
$nonce = '__NONCE__'
$keep = '__KEEP__'
$remove = @(__REMOVE__)
$pidA = 'PID_' + $targetPid
$pidB = 'PID&' + $targetPid
$vidNeedles = @(__VIDS__)
$enumPath = 'SYSTEM\CurrentControlSet\Enum\BTHENUM'
$statusFile = Join-Path $env:TEMP ('mm-repair-filters-' + $targetPid + '-' + $procId + '-' + $nonce + '.status')

# The status sidecar is the ONLY channel that outlives this console window:
# UseShellExecute is on, so the tray cannot redirect stdout and every Write-Host
# line below dies with the window. The stage markers, the write counts and the
# previous LowerFilters values all go here, appended as the pass advances, so
# the last stage and counts in the file are the ones this run reached.
Set-Content -LiteralPath $statusFile -Encoding UTF8 -Value 'stage=running'
function Write-Status([string]$line) {
    Add-Content -LiteralPath $statusFile -Encoding UTF8 -Value $line
}
Write-Status 'result=running'
Write-Status 'planned=0'
Write-Status 'written=0'
Write-Status 'write-failures=0'
Write-Status 'restart-failures=0'

function Test-Vid([string]$n) {
    $low = $n.ToLowerInvariant()
    foreach ($v in $vidNeedles) {
        if ($low.Contains($v.ToLowerInvariant())) { return $true }
    }
    return $false
}

function Test-SkipPath([string]$full) {
    $low = $full.ToLowerInvariant()
    if ($low.StartsWith('usb\')) { return $true }
    if ($low.StartsWith('hid\vid_')) { return $true }
    return $false
}

# Only the names this repair was handed are ever dropped. Everything else on
# LowerFilters - mouhid, HidBth, a second vendor filter that is running - is
# copied through untouched.
function Test-Removable([string]$n) {
    if ([string]::IsNullOrEmpty($n)) { return $false }
    foreach ($r in $remove) {
        if ($n.Equals($r, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Test-HasKeep($names) {
    foreach ($n in $names) {
        if ((-not [string]::IsNullOrEmpty($n)) -and $n.Equals($keep, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

$targets = New-Object System.Collections.Generic.List[object]
$ids = New-Object System.Collections.Generic.List[string]

$root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($enumPath)
if ($root) {
    foreach ($sub in $root.GetSubKeyNames()) {
        $low = $sub.ToLowerInvariant()
        if (-not ($low.Contains($pidA.ToLowerInvariant()) -or $low.Contains($pidB.ToLowerInvariant()))) { continue }
        if (-not (Test-Vid $sub)) { continue }
        if (Test-SkipPath ('BTHENUM\' + $sub)) { continue }
        $dev = $root.OpenSubKey($sub)
        if (-not $dev) { continue }
        $devVal = $dev.GetValue('LowerFilters')
        if ($null -ne $devVal) {
            [void]$targets.Add([pscustomobject]@{
                Path     = 'HKLM:\' + $enumPath + '\' + $sub
                Kind     = 'device'
                Previous = @($devVal)
                Filtered = @()
                Changed  = $false
            })
        }
        foreach ($inst in $dev.GetSubKeyNames()) {
            $full = 'BTHENUM\' + $sub + '\' + $inst
            if (Test-SkipPath $full) { continue }
            [void]$ids.Add($full)
            $instKey = $dev.OpenSubKey($inst)
            if ($instKey) {
                $instVal = $instKey.GetValue('LowerFilters')
                if ($null -ne $instVal) {
                    [void]$targets.Add([pscustomobject]@{
                        Path     = 'HKLM:\' + $enumPath + '\' + $sub + '\' + $inst
                        Kind     = 'instance'
                        Previous = @($instVal)
                        Filtered = @()
                        Changed  = $false
                    })
                }
                $instKey.Dispose()
            }
        }
        $dev.Dispose()
    }
    $root.Dispose()
}

Write-Status 'stage=scanned'
if ($ids.Count -eq 0) {
    Write-Host "DEVICE_REPAIR_FILTERS no BTHENUM instances pid=$targetPid"
    Write-Status 'result=no-instances'
    exit 2
}

# Every key and every previous value crosses the elevation boundary through the
# sidecar BEFORE the first write. Write-Host cannot be read back once this
# window closes, so the tray copies these lines into its own backup file and
# that file - not the transcript - is what a hand restore is pasted from.
$changes = 0
$index = 0
foreach ($t in $targets) {
    Write-Host ("DEVICE_REPAIR_FILTERS key=" + $t.Path + " kind=" + $t.Kind)
    Write-Host ("DEVICE_REPAIR_FILTERS previous LowerFilters=[" + (@($t.Previous) -join '|') + "]")
    Write-Status ("key=" + $index + "|" + $t.Path)
    foreach ($n in @($t.Previous)) {
        Write-Status ("prev=" + $index + "|" + [string]$n)
    }
    $index++
    $kept = New-Object System.Collections.Generic.List[string]
    foreach ($n in @($t.Previous)) {
        if (Test-Removable $n) {
            Write-Host ("DEVICE_REPAIR_FILTERS drop=" + $n + " key=" + $t.Path)
            continue
        }
        [void]$kept.Add([string]$n)
    }
    $t.Filtered = @($kept)
    $t.Changed = ($t.Filtered.Count -ne @($t.Previous).Count)
    if ($t.Changed) { $changes++ }
    Write-Host ("DEVICE_REPAIR_FILTERS filtered LowerFilters=[" + ($t.Filtered -join '|') + "] changed=" + $t.Changed)
}
Write-Status ("planned=" + $changes)

if ($changes -eq 0) {
    Write-Host "DEVICE_REPAIR_FILTERS nothing to remove pid=$targetPid keep=$keep"
    Write-Status 'result=nothing-to-do'
    exit 0
}

# BOTH refusals are decided here, before the write pass, and both are decided
# from the scan alone. Refusing from inside the write loop would mean stopping
# with earlier keys already rewritten and the stack never rebuilt.
$keepAnywhere = $false
foreach ($t in $targets) {
    if (Test-HasKeep $t.Filtered) { $keepAnywhere = $true }
}
if (-not $keepAnywhere) {
    Write-Host "DEVICE_REPAIR_FILTERS refuse: keep=$keep would not survive on this stack"
    Write-Status 'result=failed'
    exit 1
}
foreach ($t in $targets) {
    if (-not $t.Changed) { continue }
    if ($t.Filtered.Count -ne 0) { continue }
    # An empty MultiString is not a valid LowerFilters value, so the value
    # itself goes - but only while the keep filter still lives on the other
    # key of this stack (device vs instance).
    $keepElsewhere = $false
    foreach ($o in $targets) {
        if ($o.Path -eq $t.Path) { continue }
        if (Test-HasKeep $o.Filtered) { $keepElsewhere = $true }
    }
    if (-not $keepElsewhere) {
        Write-Host ("DEVICE_REPAIR_FILTERS refuse: emptying " + $t.Path + " would drop keep=" + $keep)
        Write-Status 'result=failed'
        exit 1
    }
}

Write-Status 'stage=writing'
$written = 0
$writeFailures = 0
foreach ($t in $targets) {
    if (-not $t.Changed) { continue }
    if ($t.Filtered.Count -eq 0) {
        Write-Host ("DEVICE_REPAIR_FILTERS delete value LowerFilters key=" + $t.Path)
        try {
            Remove-ItemProperty -LiteralPath $t.Path -Name 'LowerFilters' -Force -ErrorAction Stop
            $written++
        } catch {
            Write-Host ("DEVICE_REPAIR_FILTERS write error key=" + $t.Path + " err=" + $_.Exception.Message)
            $writeFailures++
        }
        continue
    }
    Write-Host ("DEVICE_REPAIR_FILTERS write LowerFilters=[" + ($t.Filtered -join '|') + "] key=" + $t.Path)
    try {
        Set-ItemProperty -LiteralPath $t.Path -Name 'LowerFilters' -Value ([string[]]$t.Filtered) -Type MultiString -ErrorAction Stop
        $written++
    } catch {
        Write-Host ("DEVICE_REPAIR_FILTERS write error key=" + $t.Path + " err=" + $_.Exception.Message)
        $writeFailures++
    }
}
# Counts first, marker last. A reader that lands between these appends - or
# loses one of them to a torn read - then sees written=0 next to the stage it
# came in with, never next to 'written', so it can never read the header's
# seeded zero as proof that the write pass changed nothing.
Write-Status ("written=" + $written)
Write-Status ("write-failures=" + $writeFailures)
Write-Status 'stage=written'

# Once ANY write has landed, the loaded stack no longer matches the registry,
# so the stack is rebuilt even when a later write failed. Stopping between a
# landed write and this loop is what left mutated filters behind a stale cached
# stack - the dead-wheel state this repair exists to clear. Restart only -
# never enable/disable, never unpair.
$restartFailures = 0
if ($written -gt 0) {
    foreach ($id in $ids) {
        Write-Host "DEVICE_REPAIR_FILTERS restart $id"
        & pnputil.exe /restart-device "$id"
        if ($LASTEXITCODE -ne 0) { $restartFailures++ }
    }
    Write-Status ("restart-failures=" + $restartFailures)
    Write-Status 'stage=restarted'
} else {
    Write-Host "DEVICE_REPAIR_FILTERS no write landed, so the stack is left as it was"
}
if ($writeFailures -ne 0 -or $restartFailures -ne 0) {
    Write-Host "DEVICE_REPAIR_FILTERS write failures=$writeFailures restart failures=$restartFailures - any registry edit above is already applied"
    Write-Status 'result=failed'
    exit 1
}

# PnP loads the filter asynchronously, so poll instead of reading once.
$deadline = (Get-Date).AddSeconds(10)
$queryOut = ''
$running = $false
while ($true) {
    $queryOut = (& sc.exe query "$keep" 2>&1 | Out-String)
    if ($queryOut -match 'RUNNING') { $running = $true; break }
    if ((Get-Date) -ge $deadline) { break }
    Start-Sleep -Milliseconds 500
}
Write-Host "DEVICE_REPAIR_FILTERS sc query svc=$keep running=$running"
Write-Host $queryOut
if ($running) {
    Write-Status 'stage=verified'
    Write-Status 'result=ok'
    exit 0
}

# Still not running. Only now is signing policy relevant: a self-signed KMDF
# filter cannot load without test signing, and HVCI blocks it regardless.
$startOptions = ''
try {
    $startOptions = [string](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name SystemStartOptions -ErrorAction Stop).SystemStartOptions
} catch {
    $startOptions = ''
}
$hvci = 'unavailable'
$hvciOn = $false
try {
    $dg = Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction Stop
    $svcRunning = @($dg.SecurityServicesRunning)
    $hvci = ($svcRunning -join ',')
    if ($svcRunning -contains 2) { $hvciOn = $true }
} catch {
    $hvci = 'unavailable'
}
$testSigning = $startOptions.ToUpperInvariant().Contains('TESTSIGNING')
Write-Host "DEVICE_REPAIR_FILTERS startoptions=$startOptions"
Write-Host "DEVICE_REPAIR_FILTERS testsigning=$testSigning hvci=$hvci hvcion=$hvciOn"
if ((-not $testSigning) -or $hvciOn) {
    Write-Host "DEVICE_REPAIR_FILTERS filter blocked svc=$keep"
    Write-Status 'result=filter-blocked'
    exit 3
}
Write-Host "DEVICE_REPAIR_FILTERS filter not running svc=$keep with testsigning on and hvci off"
Write-Status 'result=failed'
exit 1
""";
        var script = template
            .Replace("__PID__", pid, StringComparison.Ordinal)
            .Replace("__PROCID__", ProcIdSegment(), StringComparison.Ordinal)
            .Replace("__NONCE__", ValidateNonce(nonce), StringComparison.Ordinal)
            .Replace("__KEEP__", keep, StringComparison.Ordinal)
            .Replace("__REMOVE__", removeLiteral, StringComparison.Ordinal)
            .Replace("__VIDS__", vidLiteral, StringComparison.Ordinal);
        foreach (var name in DeviceEnable.ForbiddenNames)
        {
            if (script.Contains(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"DeviceRepair refuses {name}.");
        }
        return script;
    }

    // --- the parsed report ------------------------------------------------

    // The window the poller gives an elevated attempt before it kills the
    // tree. Named, not a literal in the poll loop, because the outcome of that
    // kill is explained to the user in the same terms the loop waits in.
    internal static readonly TimeSpan ElevatedCeiling = TimeSpan.FromMinutes(2);

    static RepairStage? StageFor(string token) => token switch
    {
        "running" => RepairStage.Running,
        "scanned" => RepairStage.Scanned,
        "writing" => RepairStage.Writing,
        "written" => RepairStage.Written,
        "restarted" => RepairStage.Restarted,
        "verified" => RepairStage.Verified,
        _ => null,
    };

    // Last line wins: the sidecar is appended to as the script advances, so
    // the stage and the counts at the end of the file are the ones reached.
    // A missing or unknown stage, or a negative count, is not a state -
    // guessing one would let the tray claim the registry is clean on the
    // strength of a truncated file.
    internal static RepairProgress? ParseProgress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        RepairStage? stage = null;
        var result = "";
        int planned = 0, written = 0, writeFailures = 0, restartFailures = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq];
            var val = line[(eq + 1)..];
            switch (key)
            {
                case "stage":
                    stage = StageFor(val);
                    if (stage is null)
                        return null;
                    break;
                case "result":
                    result = val;
                    break;
                case "planned":
                    if (!TryCount(val, out planned))
                        return null;
                    break;
                case "written":
                    if (!TryCount(val, out written))
                        return null;
                    break;
                case "write-failures":
                    if (!TryCount(val, out writeFailures))
                        return null;
                    break;
                case "restart-failures":
                    if (!TryCount(val, out restartFailures))
                        return null;
                    break;
                default:
                    break;
            }
        }
        return stage is { } reached
            ? new RepairProgress(result, reached, planned, written, writeFailures, restartFailures)
            : null;
    }

    static bool TryCount(string value, out int count) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count >= 0;

    // --- what the outcome says about HKLM ----------------------------------

    // The registry dimension. hasWritePass=false means the script that ran has
    // no registry write in it at all, so Untouched is a fact about the script
    // and holds however the attempt ended - including with no report to read.
    // With a write pass, no report at all is Unknown, never Untouched.
    static RegistryState RegistryFor(RepairProgress? progress, bool hasWritePass)
    {
        if (!hasWritePass)
            return RegistryState.Untouched;
        if (progress is not { } p)
            return RegistryState.Unknown;
        if (p.KeysWritten > 0)
            return RegistryState.Modified;
        // Stopped inside the write pass, or with a write that reported an
        // error: no key is known to have landed and none is known to be intact.
        if (p.Stage == RepairStage.Writing || p.WriteFailures > 0)
            return RegistryState.Unknown;
        return RegistryState.Untouched;
    }

    // Total: every end state the two scripts can reach is a value here, and
    // each carries its own sentence because the recovery step differs in every
    // one. Order matters - Started is checked first because UAC is the one
    // thing that can be true before anything ran. Only two things may promise
    // that nothing was written: that branch, and a script with no write pass.
    internal static RepairResult Decide(RepairEvidence evidence)
    {
        if (!evidence.Started)
            return new RepairResult(
                RepairOutcome.Failed,
                RegistryState.Untouched,
                "Windows did not start the elevated repair: the UAC prompt was declined or "
                + "blocked. " + RegistryDetail(RegistryState.Untouched));

        var progress = evidence.Progress;
        var registry = RegistryFor(progress, evidence.HasWritePass);
        switch (progress?.Result)
        {
            case "ok":
            case "nothing-to-do":
                return Report(RepairOutcome.Ok, registry, progress);
            // Both scripts report no-instances - and exit with
            // NoInstancesExitCode - from the scan, before any write pass.
            case "no-instances":
                return Report(RepairOutcome.NoInstances, RegistryState.Untouched, progress);
            case "filter-blocked":
                return Report(RepairOutcome.FilterBlocked, registry, progress);
            case "failed":
                return Report(FailureFor(progress, registry), registry, progress);
            default:
                break;
        }

        // No terminal report. The ceiling kill is its own end state: the
        // script was cut off mid-pass and never reported anything final.
        if (evidence.TimedOut)
            return Report(RepairOutcome.TimedOut, registry, progress);
        if (evidence.Exit == NoInstancesExitCode)
            return Report(RepairOutcome.NoInstances, RegistryState.Untouched, progress);
        if (evidence.Exit == FilterBlockedExitCode)
            return Report(RepairOutcome.FilterBlocked, registry, progress);
        return Report(FailureFor(progress, registry), registry, progress);
    }

    // A failure that reached the write pass is not the same event as one that
    // refused before it: only a report that got far enough to touch - or to
    // possibly touch - a key maps onto StoppedAfterWrite.
    static RepairOutcome FailureFor(RepairProgress? progress, RegistryState registry) =>
        progress is not null && registry != RegistryState.Untouched
            ? RepairOutcome.StoppedAfterWrite
            : RepairOutcome.Failed;

    static RepairResult Report(RepairOutcome outcome, RegistryState registry, RepairProgress? progress) =>
        new(outcome, registry, OutcomeDetail(outcome, progress) + " " + RegistryDetail(registry));

    static string OutcomeDetail(RepairOutcome outcome, RepairProgress? progress)
    {
        if (outcome == RepairOutcome.TimedOut)
            return $"The elevated repair was still running after "
                + $"{ElevatedCeiling.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes, "
                + "so the tray stopped it mid-pass.";
        if (progress is not { } p)
            return "The elevated repair ended without reporting how far it got.";
        return outcome switch
        {
            RepairOutcome.Ok when p.Result.Equals("nothing-to-do", StringComparison.Ordinal) =>
                "Nothing needed changing: this stack already named only the working filter.",
            RepairOutcome.Ok when p.KeysWritten > 0 =>
                "The leftover filter name was removed and the device stack was restarted.",
            RepairOutcome.Ok =>
                "The device stack was restarted and the scroll driver came back up.",
            RepairOutcome.NoInstances =>
                "No live Bluetooth instance for this device was found, so the repair had "
                + "nothing to work on.",
            RepairOutcome.FilterBlocked =>
                "Windows is refusing to load the scroll driver: it is self-signed, so test "
                + "signing has to be on and memory integrity off.",
            RepairOutcome.StoppedAfterWrite when p.RestartFailures > 0 =>
                "The filter list was rewritten, but the device stack would not restart, so "
                + "the loaded stack still uses the old list.",
            RepairOutcome.StoppedAfterWrite when p.KeysWritten > 0 =>
                $"The repair rewrote {p.KeysWritten.ToString(CultureInfo.InvariantCulture)} of "
                + $"{p.KeysPlanned.ToString(CultureInfo.InvariantCulture)} keys and then stopped.",
            RepairOutcome.StoppedAfterWrite =>
                "The repair stopped with a registry write in flight.",
            _ when p.RestartFailures > 0 =>
                "The device stack would not restart.",
            _ =>
                "The repair stopped before it changed anything.",
        };
    }

    // The second half of every sentence, and the half the user acts on: it
    // says whether LowerFilters on this PC is now something other than what it
    // was when the repair started. Internal because the outcomes that get a
    // help dialog instead of a toast - FilterBlocked above all, which is
    // reported AFTER the write pass - compose their own body and still have to
    // carry this sentence.
    internal static string RegistryDetail(RegistryState registry) => registry switch
    {
        RegistryState.Modified =>
            "LowerFilters has already been changed in the registry on this PC.",
        RegistryState.Unknown =>
            "Whether LowerFilters was changed is not known - check it before running the "
            + "repair again.",
        _ => "Nothing in the registry was changed.",
    };

    // What a support log shows, so one line carries BOTH dimensions: an
    // outcome of "failed" alone never says whether HKLM was left edited.
    internal static string LogToken(RepairResult result) =>
        OutcomeToken(result.Outcome) + "/" + RegistryToken(result.Registry);

    static string OutcomeToken(RepairOutcome outcome) => outcome switch
    {
        RepairOutcome.Ok => "ok",
        RepairOutcome.NoInstances => "no-instances",
        RepairOutcome.FilterBlocked => "filter-blocked",
        RepairOutcome.StoppedAfterWrite => "stopped-after-write",
        RepairOutcome.TimedOut => "timed-out",
        _ => "failed",
    };

    static string RegistryToken(RegistryState registry) => registry switch
    {
        RegistryState.Modified => "modified",
        RegistryState.Unknown => "unknown",
        _ => "untouched",
    };

    // The attempt never reached ShellExecute - an invalid request or a temp
    // script that could not be written - so no elevated code ran.
    internal static RepairResult NotLaunched() =>
        new(RepairOutcome.Failed,
            RegistryState.Untouched,
            "The repair could not be started on this PC. " + RegistryDetail(RegistryState.Untouched));

    // --- the previous values, kept past the console window ------------------

    const string BackupKeyPrefix = "key=";
    const string BackupValuePrefix = "prev=";

    // Next to debug.log, never as debug.log: that file rotates at 1 MB and is
    // redacted for sharing, and this is the only copy of what LowerFilters
    // held. Named per attempt - pid, this process, this nonce - so two
    // overlapping repairs for one PID cannot overwrite each other's record.
    internal static string FilterBackupPath(string pid, long nonce) =>
        Path.Combine(
            Logger.LogDir,
            $"mm-filters-backup-{ValidatePid(pid)}-{ProcIdSegment()}-{ValidateNonce(nonce)}.txt");

    // The recorded form: one key= line per key, then one prev= line per value
    // it held, in order. Both line kinds carry the record index first, so the
    // rest of the line is the raw path or value - including any '|' or '=' the
    // registry happened to hold.
    internal static IReadOnlyList<string> FormatFilterBackup(
        string pid, long nonce, IReadOnlyList<FilterBackupEntry> entries)
    {
        var recorded = entries.Where(e => e.PreviousValues.Count > 0).ToList();
        if (recorded.Count == 0)
            return [];
        var lines = new List<string>((recorded.Count * 2) + 2)
        {
            $"mm-filters-backup pid={ValidatePid(pid)} nonce={ValidateNonce(nonce)} "
                + $"utc={DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}",
            "to undo by hand: for each block below set LowerFilters (REG_MULTI_SZ) on that "
                + "path back to the listed values, in the listed order.",
        };
        for (var i = 0; i < recorded.Count; i++)
        {
            var index = i.ToString(CultureInfo.InvariantCulture);
            lines.Add(BackupKeyPrefix + index + "|" + recorded[i].KeyPath);
            foreach (var value in recorded[i].PreviousValues)
                lines.Add(BackupValuePrefix + index + "|" + value);
        }
        return lines;
    }

    // Reads the same form back, out of the backup file or straight out of the
    // script's status sidecar. A key with no recorded value and a value with no
    // key are both dropped: a half record is not something a hand restore may
    // act on.
    internal static IReadOnlyList<FilterBackupEntry> ParseFilterBackup(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var order = new List<(string KeyPath, List<string> Values)>();
        var byIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            // Only the line separator is stripped: a recorded value keeps its
            // own leading and trailing spaces.
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(BackupKeyPrefix, StringComparison.Ordinal))
            {
                if (!SplitRecord(line[BackupKeyPrefix.Length..], out var index, out var keyPath))
                    continue;
                if (keyPath.Length == 0 || byIndex.ContainsKey(index))
                    continue;
                byIndex[index] = order.Count;
                order.Add((keyPath, []));
            }
            else if (line.StartsWith(BackupValuePrefix, StringComparison.Ordinal))
            {
                if (!SplitRecord(line[BackupValuePrefix.Length..], out var index, out var value))
                    continue;
                if (byIndex.TryGetValue(index, out var at))
                    order[at].Values.Add(value);
            }
        }
        return order
            .Where(e => e.Values.Count > 0)
            .Select(e => new FilterBackupEntry(e.KeyPath, e.Values.ToArray()))
            .ToList();
    }

    // Index up to the FIRST '|', remainder verbatim - a LowerFilters value or
    // a key path is free to contain the separator.
    static bool SplitRecord(string rest, out string index, out string value)
    {
        index = "";
        value = "";
        var bar = rest.IndexOf('|');
        if (bar <= 0)
            return false;
        index = rest[..bar];
        value = rest[(bar + 1)..];
        return true;
    }

    // An undo record is only an undo record when there is something to undo.
    // The script reports key=/prev= for every scanned target BEFORE it knows
    // whether it will change any of them, so a nothing-to-do run - or one that
    // refused before the write pass - carries the same lines as a run that
    // rewrote the stack. Keyed off the registry dimension, which is exactly
    // "a write may have landed": Untouched is the one answer that means the
    // recorded values are still what the keys hold.
    internal static bool ShouldRecordFilterBackup(RepairResult result) =>
        result.Registry != RegistryState.Untouched;

    // The production copy. The elevated console is gone the moment the script
    // ends and nothing can be redirected out of it under UseShellExecute, so
    // the previous values are kept here, from the sidecar lines the script
    // wrote before its first write. Returns the file it wrote, or null when
    // there was nothing to record.
    static string? WriteFilterBackup(string pid, long nonce, IReadOnlyList<FilterBackupEntry> entries)
    {
        var lines = FormatFilterBackup(pid, nonce, entries);
        if (lines.Count == 0)
            return null;
        var path = FilterBackupPath(pid, nonce);
        try
        {
            Directory.CreateDirectory(Logger.LogDir);
            File.WriteAllLines(path, lines);
            PruneFilterBackups();
            return path;
        }
        catch (Exception ex)
        {
            Logger.Log($"DEVICE_REPAIR_FILTERS backup_failed pid={pid} nonce={nonce} err={ex.Message}");
            return null;
        }
    }

    // How many attempts' records are kept. These files sit in the folder "Open
    // logs" opens, hold unredacted BTHENUM key paths, and nothing else ever
    // deletes them - debug.log rotates and ModeFlip clears its sentinel, so an
    // unbounded pile here is the odd one out. The newest few are the ones that
    // can still describe a stack the user has not fixed yet.
    const int KeptFilterBackups = 5;

    // Best effort: a record that cannot be deleted is left alone, because
    // failing to tidy is never a reason to fail the repair that just ran.
    static void PruneFilterBackups()
    {
        try
        {
            var stale = new DirectoryInfo(Logger.LogDir)
                .GetFiles("mm-filters-backup-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeptFilterBackups);
            foreach (var file in stale)
            {
                try { file.Delete(); } catch { /* in use or gone */ }
            }
        }
        catch { /* the folder is unreadable; the record itself is already written */ }
    }

    // Shared elevation shape for both repairs: write the script, ShellExecute
    // it with Verb=runas, and poll the status sidecar because ExitCode is
    // unavailable once UseShellExecute is true. Started=false means
    // ShellExecute handed back no process at all - UAC refused outright.
    // TimedOut=true means the poll loop reached ElevatedCeiling and killed the
    // tree: the script was cut off wherever it had got to, which is reported
    // as its own outcome and never as a script that failed on its own.
    static (bool Started, string Sidecar, int? Exit, bool TimedOut) LaunchElevated(
        string scriptPath,
        string script,
        string statusPath)
    {
        try { File.Delete(statusPath); } catch { /* ignore */ }
        File.WriteAllText(scriptPath, script);
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Path.GetTempPath(),
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return (false, "", null, false); }
        if (p is null)
            return (false, "", null, false);
        string sidecar = "";
        int? exit = null;
        var settled = false;
        using (p)
        {
            var started = DateTime.UtcNow;
            var until = started + ElevatedCeiling;
            while (DateTime.UtcNow < until)
            {
                if (File.Exists(statusPath))
                {
                    // A read that loses to the script's own append keeps the
                    // last good text: it carries the recorded previous values.
                    try { sidecar = File.ReadAllText(statusPath); }
                    catch { /* locked mid-append */ }
                    if (IsTerminalResult(ParseProgress(sidecar)?.Result))
                    {
                        settled = true;
                        break;
                    }
                }
                try
                {
                    // Cancelled UAC prompt: the launcher is gone and nothing was written.
                    if (p.HasExited
                        && sidecar.Length == 0
                        && DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
                    {
                        settled = true;
                        break;
                    }
                }
                catch { /* UseShellExecute */ }
                Thread.Sleep(150);
            }
            if (!p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            try { if (p.HasExited) exit = p.ExitCode; } catch { /* UseShellExecute */ }
        }
        // Only a loop that ran out of time killed a script that was still
        // working - the kill after a final report is just cleanup. The elevated
        // tree can be stopped anywhere, including between the device-key write
        // and the instance-key writes, so the last report it managed to write
        // is what the registry dimension is read from.
        return (true, sidecar, exit, !settled);
    }

    // The five results either script writes as its last word. "running" is not
    // one of them: it means the pass was still in flight when this was read.
    static bool IsTerminalResult(string? result) =>
        result is "ok" or "failed" or "no-instances" or "filter-blocked" or "nothing-to-do";

    // Never throws for NoInstances / FilterBlocked - those are returned so the
    // caller can explain them. Throws only when the request itself is invalid
    // (bad PID, unknown service) or the temp script cannot be written.
    internal static RepairResult Apply(string pid, string filterServiceName)
    {
        pid = ValidatePid(pid);
        var svc = ValidateFilterServiceName(filterServiceName);
        // One nonce per attempt, minted here because Apply IS the attempt: it
        // names this attempt's script and its status sidecar on both sides of
        // the elevation boundary - see StatusSidecarPath for what a PID-only
        // name lets the poller believe. AttemptNonce.Next() and not the
        // millisecond clock, because two attempts can start inside one
        // millisecond and would then mint the same value. Logged so two
        // overlapping attempts can be told apart in a support log.
        var nonce = AttemptNonce.Next();
        var script = BuildScript(pid, nonce, svc);
        var temp = Path.Combine(
            Path.GetTempPath(),
            $"mm-repair-{pid}-{ProcIdSegment()}-{ValidateNonce(nonce)}.ps1");
        Logger.Log($"DEVICE_REPAIR pid={pid} nonce={nonce} svc={svc}");
        var run = LaunchElevated(temp, script, StatusSidecarPath(pid, nonce));
        var progress = ParseProgress(run.Sidecar);
        // HasWritePass: false - BuildScript restarts devices and reads
        // SystemStartOptions, and contains no registry write at all, so this
        // attempt cannot have changed LowerFilters even when its report never
        // reaches the tray (an over-the-shoulder elevation writes the sidecar
        // under the admin's profile, a ceiling kill can land before the first
        // status line). Telling the user to go check LowerFilters would be a
        // warning about a change this script has no code to make.
        var result = Decide(
            new RepairEvidence(run.Started, progress, run.Exit, run.TimedOut, HasWritePass: false));
        Logger.Log($"DEVICE_REPAIR pid={pid} svc={svc} nonce={nonce} "
            + $"exit={run.Exit?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} "
            + $"stage={progress?.Stage.ToString() ?? "none"} timed_out={run.TimedOut} "
            + $"outcome={LogToken(result)}");
        return result;
    }

    // Same never-throws-for-NoInstances/FilterBlocked contract as Apply.
    // Throws only when the request itself is invalid (bad PID, unknown or
    // contradictory service names) or the temp script cannot be written.
    internal static RepairResult ApplyRemoveStaleFilters(string pid, string keepService, string[] removeServices)
    {
        pid = ValidatePid(pid);
        var keep = ValidateFilterServiceName(keepService);
        var remove = ValidateRemoveList(keep, removeServices);
        // Its own attempt, so its own nonce - this repair can be running while
        // an Apply for the same PID is, and neither may poll the other's file.
        var nonce = AttemptNonce.Next();
        var script = BuildRemoveStaleFiltersScript(pid, nonce, keep, remove);
        var temp = Path.Combine(
            Path.GetTempPath(),
            $"mm-repair-filters-{pid}-{ProcIdSegment()}-{ValidateNonce(nonce)}.ps1");
        var removed = string.Join(",", remove);
        Logger.Log($"DEVICE_REPAIR_FILTERS pid={pid} nonce={nonce} keep={keep} remove={removed}");
        var run = LaunchElevated(temp, script, FiltersStatusSidecarPath(pid, nonce));
        var progress = ParseProgress(run.Sidecar);
        var result = Decide(
            new RepairEvidence(run.Started, progress, run.Exit, run.TimedOut, HasWritePass: true));
        // After the outcome, because the outcome is what says whether there is
        // anything to undo, but from the same sidecar text: this is the only
        // copy of what the keys held - the sidecar lives in %TEMP% and the
        // elevated console that printed them is already gone. Written whenever
        // a write may have landed, including after a ceiling kill.
        var backup = ShouldRecordFilterBackup(result)
            ? WriteFilterBackup(pid, nonce, ParseFilterBackup(run.Sidecar))
            : null;
        Logger.Log($"DEVICE_REPAIR_FILTERS pid={pid} keep={keep} remove={removed} nonce={nonce} "
            + $"exit={run.Exit?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} "
            + $"stage={progress?.Stage.ToString() ?? "none"} "
            + $"written={progress?.KeysWritten.ToString(CultureInfo.InvariantCulture) ?? "n/a"} "
            + $"timed_out={run.TimedOut} backup={(backup is null ? "none" : Path.GetFileName(backup))} "
            + $"outcome={LogToken(result)}");
        return result;
    }
}
