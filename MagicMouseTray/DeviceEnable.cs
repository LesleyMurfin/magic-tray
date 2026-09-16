// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace MagicMouseTray;

// Start/Stop this device in Windows: stop/start the Windows device for this
// catalog PID so a Mac can take the Bluetooth link. No unpair. No driver
// installers.
// VID needles come from KnownMice / KnownKeyboards — adding a device to
// those tables is what makes enable/disable work for it.
//
// Two different things are called "enabled" in this app. This file is only
// about the FIRST one:
//   Windows device state - the devnode is enabled or disabled in PnP.
//                          Changing it needs elevation, and it is what
//                          pnputil /enable-device and /disable-device move.
//   tray config state    - enabled_<pid> in config.ini, i.e. whether the tray
//                          polls the device at all. Pure user-space config;
//                          nothing here touches it.
//
// The verdict is the END STATE, never an exit code: pnputil legitimately
// exits 1 for a device that is already in the wanted state, which is why the
// elevated script reads the state FIRST, skips pnputil entirely when there is
// nothing to do, and RE-READS the state after any pnputil call. Same rule
// ModeFlip follows.
//
// What an attempt did. Changed and AlreadyInState are the two successes, and
// both mean a read of the live devnodes says the device IS in the wanted
// state. The three failures are kept apart on purpose, because the tray used
// to blame UAC for all of them:
//   UacDeclined  elevation never started, so nothing ran at all.
//   Failed       elevation SUCCEEDED, the script ran, and the re-read does not
//                show the wanted state. Never a UAC message.
//   NoInstances  Windows has no live instance for this PID to aim at.
internal enum DeviceEnableOutcome
{
    Changed,
    AlreadyInState,
    NoInstances,
    UacDeclined,
    Failed,
}

// Detail is a finished sentence for the user, for every member.
internal readonly record struct DeviceEnableResult(DeviceEnableOutcome Outcome, string Detail)
{
    internal bool Succeeded =>
        Outcome is DeviceEnableOutcome.Changed or DeviceEnableOutcome.AlreadyInState;
}

// The device state the elevated script read, as its status sidecar reports it.
// Total is the live instances it found; InWantedBefore / InWantedAfter are how
// many of them read as the wanted state before and after the pnputil pass. An
// instance whose state could not be read is never counted as wanted, so
// "unreadable" can never pass for success. PnputilRan / PnputilExit are
// transcript only - they never decide the outcome.
internal readonly record struct DeviceEnableReading(
    string Result,
    int Total,
    int InWantedBefore,
    int InWantedAfter,
    bool PnputilRan,
    int? PnputilExit);

// Everything one attempt produced. Started is false only when ShellExecute
// handed back no elevated process - that, and only that, is UAC declined.
// Reading is null when the elevated step never wrote a complete report.
internal readonly record struct DeviceEnableEvidence(
    bool Started,
    DeviceEnableReading? Reading,
    int? Exit);

internal static class DeviceEnable
{
    internal static readonly string[] ForbiddenNames =
    [
        "Install-KMDF",
        "Uninstall-KMDF",
        "Install-MagicMousePatch",
        "Uninstall-MagicMousePatch",
        "FLIP:NoFilter",
    ];

    internal static string[] VidNeedlesForPid(string pid)
    {
        if (string.IsNullOrEmpty(pid) || pid.Length != 4)
            return [];
        pid = pid.ToLowerInvariant();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in MouseBatteryDevice.KnownMice)
            if (m.PidPattern.EndsWith(pid, StringComparison.OrdinalIgnoreCase))
                set.Add(m.VidPattern);
        foreach (var k in KeyboardBatteryDevice.KnownKeyboards)
            if (k.PidPattern.EndsWith(pid, StringComparison.OrdinalIgnoreCase))
                set.Add(k.VidPattern);
        return [.. set];
    }

    internal static bool MatchesInstance(string instanceId, string pid)
    {
        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(pid))
            return false;
        pid = pid.ToLowerInvariant();
        if (pid.Length != 4)
            return false;
        var needles = VidNeedlesForPid(pid);
        if (needles.Length == 0)
            return false;
        var low = instanceId.ToLowerInvariant();
        if (!low.Contains("pid_" + pid, StringComparison.Ordinal) &&
            !low.Contains("pid&" + pid, StringComparison.Ordinal))
            return false;
        foreach (var vid in needles)
        {
            if (!string.IsNullOrEmpty(vid) &&
                low.Contains(vid.ToLowerInvariant(), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal const int NoInstancesExitCode = 2;
    internal const int ScriptErrorExitCode = 3;

    internal const string NoInstancesDetail =
        "Windows has no live Bluetooth instance for this device right now, so "
        + "there was nothing to change. Pair the device and try again.";

    internal const string StartedButSilentDetail =
        "The elevated step started but never reported a device state, so "
        + "nothing was verified and nothing was remembered.";

    internal const string UnreadableDeviceListDetail =
        "The elevated step could not read the Windows device list, so nothing "
        + "was changed.";

    // Named after the ATTEMPT and the PROCESS, not just the device. Two
    // elevated attempts can be in flight for the same PID at once: TrayApp's
    // StartRepairApply and StartStaleFilterRemoval each launch straight into
    // Task.Run, and RunDriverActionAsync does not serialize driver actions.
    // Under a PID-only name the poller in LaunchElevated cannot tell whose
    // report it parsed - it accepts the first COMPLETE report at that path -
    // so it would hand back an end state some other elevated process
    // measured, and the File.Delete in Apply cannot close that hole because
    // the other attempt is free to write the path after the delete succeeds.
    // Same collision and the same fix as ModeFlip's cycle nonce
    // (ModeFlip.cs:225-237).
    //
    // Two segments, two independent guarantees. AttemptNonce.Next() is
    // monotonic within one process, so it separates attempts inside this tray;
    // it cannot separate two trays, and two trays do run at once
    // (ModeFlip.cs:253-257 records exactly that race). Environment.ProcessId
    // separates the processes.
    internal static string StatusSidecarPath(string pid, long nonce) =>
        Path.Combine(
            Path.GetTempPath(),
            $"mm-enable-{pid.ToLowerInvariant()}-{ProcIdSegment()}-{ValidateNonce(nonce)}.status");

    // The generated .ps1 was PID-derived too, so two overlapping attempts also
    // took turns overwriting the file the other one was about to run.
    internal static string ScriptPath(string pid, long nonce) =>
        Path.Combine(
            Path.GetTempPath(),
            $"mm-enable-{pid.ToLowerInvariant()}-{ProcIdSegment()}-{ValidateNonce(nonce)}.ps1");

    // The nonce crosses the elevation boundary as part of a file name on both
    // sides, so it is rendered as plain digits and nothing else.
    static string ValidateNonce(long nonce)
    {
        if (nonce <= 0)
            throw new InvalidOperationException($"DeviceEnable needs a positive attempt nonce, got {nonce}.");
        return nonce.ToString();
    }

    // Same rule for the process id: it is the other half of the same file
    // name, so it is validated and rendered the same way.
    static string ProcIdSegment()
    {
        var procId = Environment.ProcessId;
        if (procId <= 0)
            throw new InvalidOperationException($"DeviceEnable needs a positive process id, got {procId}.");
        return procId.ToString();
    }

    internal static string BuildScript(string pid, long nonce, bool enable)
    {
        pid = pid.ToLowerInvariant();
        var needles = VidNeedlesForPid(pid);
        if (needles.Length == 0)
            throw new InvalidOperationException($"No catalog VID for pid={pid}.");
        var verb = enable ? "enable-device" : "disable-device";
        var vidLiteral = string.Join(", ", needles.Select(v =>
            "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'"));
        // "$id" is required: HID/BTHENUM instance IDs contain '&'. Unquoted,
        // PowerShell treats '&' as the call operator and pnputil never runs.
        // USB and HID\VID_ nodes are charge-cable leftovers (CM_PROB_PHANTOM).
        // Enable/disable is the Bluetooth device on this PC, not USB.
        var template = """
$ErrorActionPreference = 'Continue'
$targetPid = '__PID__'
$procId = '__PROCID__'
$nonce = '__NONCE__'
$verb = '__VERB__'
$wantEnabled = ($verb -eq 'enable-device')
$pidA = 'PID_' + $targetPid
$pidB = 'PID&' + $targetPid
$vidNeedles = @(__VIDS__)
$ids = New-Object System.Collections.Generic.List[string]
$containers = @{}
$enumBase = 'SYSTEM\CurrentControlSet\Enum'
$statusFile = Join-Path $env:TEMP ('mm-enable-' + $targetPid + '-' + $procId + '-' + $nonce + '.status')
'running' | Set-Content -LiteralPath $statusFile -Encoding ASCII

# The tray decides success from these numbers, so they are the device state as
# read, not a verdict. 'pre' and 'post' are counted from the same registry the
# walk below matched on.
function Write-Report([string]$result, [int]$total, [int]$pre, [int]$post, [bool]$ran, $code) {
    $pnputil = 'not-run'
    if ($ran) { $pnputil = 'exit:' + [string]$code }
    Set-Content -LiteralPath $statusFile -Encoding ASCII -Value @(
        "result=$result",
        "total=$total",
        "pre=$pre",
        "post=$post",
        "pnputil=$pnputil")
}

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

# CONFIGFLAG_DISABLED (0x20) is the bit the PnP disable verb sets and the
# enable verb clears, so it is the end state worth reading back. No value at
# all means the devnode was never disabled. $null means the key could not be
# read - unknown, and never counted as the wanted state.
function Read-State([string]$full) {
    $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($enumBase + '\' + $full)
    if (-not $k) { return $null }
    try {
        $flags = $k.GetValue('ConfigFlags')
        if ($null -eq $flags) { return $true }
        return (-not ([int]$flags -band 0x20))
    } catch {
        return $null
    } finally {
        $k.Dispose()
    }
}

function Get-WantedCount([bool]$want) {
    $n = 0
    foreach ($one in $ids) {
        $state = Read-State $one
        if ($null -ne $state -and $state -eq $want) { $n++ }
    }
    return $n
}

function Add-Matching([string]$enumerator) {
    if ($enumerator -eq 'USB') { return }
    $root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($enumBase + '\' + $enumerator)
    if (-not $root) { return }
    foreach ($sub in $root.GetSubKeyNames()) {
        $low = $sub.ToLowerInvariant()
        if (-not ($low.Contains($pidA.ToLowerInvariant()) -or $low.Contains($pidB.ToLowerInvariant()))) { continue }
        if (-not (Test-Vid $sub)) { continue }
        $dev = $root.OpenSubKey($sub)
        if (-not $dev) { continue }
        foreach ($inst in $dev.GetSubKeyNames()) {
            $full = $enumerator + '\' + $sub + '\' + $inst
            if (Test-SkipPath $full) { continue }
            $container = ''
            $instKey = $dev.OpenSubKey($inst)
            if ($instKey) {
                $container = [string]$instKey.GetValue('ContainerID')
                $instKey.Dispose()
            }
            if ([string]::IsNullOrEmpty($container)) { $container = 'unknown' }
            $containers[$full] = $container
            [void]$ids.Add($full)
        }
        $dev.Dispose()
    }
    $root.Dispose()
}

$enumRoot = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($enumBase)
if (-not $enumRoot) {
    Write-Host 'DEVICE_ENABLE no Enum'
    Write-Report 'script-error' 0 0 0 $false $null
    exit 3
}
foreach ($enumerator in $enumRoot.GetSubKeyNames()) {
    Add-Matching $enumerator
}
$enumRoot.Dispose()

$rank = { if ($_.StartsWith('BTHENUM\', [StringComparison]::OrdinalIgnoreCase)) { 1 } else { 0 } }
if ($verb -eq 'disable-device') {
    $ids = [System.Collections.Generic.List[string]]($ids | Sort-Object $rank)
} else {
    $ids = [System.Collections.Generic.List[string]]($ids | Sort-Object $rank -Descending)
}

if ($ids.Count -eq 0) {
    Write-Host "DEVICE_ENABLE no instances pid=$targetPid"
    Write-Report 'no-instances' 0 0 0 $false $null
    exit 2
}

# One ContainerID per physical device. Rows are per-PID, so two of the same
# model on one PC appear here together and are toggled together.
$distinct = @($containers.Values | Sort-Object -Unique)
Write-Host "DEVICE_ENABLE containers=$($distinct -join ',')"

$total = $ids.Count
$pre = Get-WantedCount $wantEnabled
Write-Host "DEVICE_ENABLE pre=$pre/$total verb=$verb"

# Idempotent pre-check: every live instance is already in the wanted state, so
# the PnP verb has nothing to do. Calling it anyway is what returned 1 for a
# device that was already right. Report the state that was read and run nothing.
if ($pre -eq $total) {
    Write-Host "DEVICE_ENABLE already pid=$targetPid verb=$verb"
    Write-Report 'already' $total $pre $pre $false $null
    exit 0
}

$failed = 0
$lastCode = 0
foreach ($id in $ids) {
    Write-Host "DEVICE_ENABLE $verb $id container=$($containers[$id])"
    & pnputil.exe "/$verb" "$id"
    if ($LASTEXITCODE -ne 0) {
        $failed++
        $lastCode = $LASTEXITCODE
    }
}

# Post-read decides. A non-zero pnputil exit on a device that NOW reads as the
# wanted state is a success, and a zero exit that did not move the state is not.
$post = Get-WantedCount $wantEnabled
Write-Host "DEVICE_ENABLE post=$post/$total pnputil_failed=$failed"
if ($post -eq $total) {
    Write-Report 'ok' $total $pre $post $true $lastCode
    exit 0
}
Write-Report 'not-verified' $total $pre $post $true $lastCode
exit 1
""";
        var script = template
            .Replace("__PID__", pid, StringComparison.Ordinal)
            .Replace("__PROCID__", ProcIdSegment(), StringComparison.Ordinal)
            .Replace("__NONCE__", ValidateNonce(nonce), StringComparison.Ordinal)
            .Replace("__VERB__", verb, StringComparison.Ordinal)
            .Replace("__VIDS__", vidLiteral, StringComparison.Ordinal);
        foreach (var name in ForbiddenNames)
        {
            if (script.Contains(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"DeviceEnable refuses {name}.");
        }
        return script;
    }

    // --- decision logic (pure) -------------------------------------------

    // A report is complete or it is nothing: the script writes all five lines
    // in one Set-Content, so a partial parse means the step died mid-way and
    // there is no state to trust.
    internal static DeviceEnableReading? ParseSidecar(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var result = "";
        int total = -1, pre = -1, post = -1;
        var ran = false;
        int? code = null;
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
                case "result":
                    result = val;
                    break;
                case "total":
                    total = ParseCount(val);
                    break;
                case "pre":
                    pre = ParseCount(val);
                    break;
                case "post":
                    post = ParseCount(val);
                    break;
                case "pnputil":
                    ran = !val.Equals("not-run", StringComparison.Ordinal);
                    if (val.StartsWith("exit:", StringComparison.Ordinal)
                        && int.TryParse(val[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                        code = c;
                    break;
                default:
                    break;
            }
        }
        if (result.Length == 0 || total < 0 || pre < 0 || post < 0)
            return null;
        return new DeviceEnableReading(result, total, pre, post, ran, code);
    }

    static int ParseCount(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0
            ? n
            : -1;

    // Total: every expected end state is a value. The order matters - Started
    // is checked first because UAC is the one thing that can be true before
    // any state was read, and it is the ONLY branch allowed to say so.
    internal static DeviceEnableResult Decide(DeviceEnableEvidence evidence, bool enable)
    {
        var wanted = enable ? "enabled" : "disabled";
        var unwanted = enable ? "disabled" : "enabled";
        if (!evidence.Started)
            return new DeviceEnableResult(
                DeviceEnableOutcome.UacDeclined,
                "Cancelled at the Windows permission prompt (UAC), so nothing "
                + "on this PC was changed.");
        if (evidence.Reading is not { } reading)
            return evidence.Exit == NoInstancesExitCode
                ? new DeviceEnableResult(DeviceEnableOutcome.NoInstances, NoInstancesDetail)
                : new DeviceEnableResult(DeviceEnableOutcome.Failed, StartedButSilentDetail);
        if (reading.Result.Equals("script-error", StringComparison.OrdinalIgnoreCase))
            return new DeviceEnableResult(DeviceEnableOutcome.Failed, UnreadableDeviceListDetail);
        if (reading.Total <= 0)
            return new DeviceEnableResult(DeviceEnableOutcome.NoInstances, NoInstancesDetail);
        if (reading.InWantedAfter >= reading.Total)
            return reading.PnputilRan
                ? new DeviceEnableResult(
                    DeviceEnableOutcome.Changed,
                    $"Windows now reports this device as {wanted}.")
                : new DeviceEnableResult(
                    DeviceEnableOutcome.AlreadyInState,
                    $"Windows already had this device {wanted}, so nothing had to change.");
        var pnputil = reading.PnputilRan
            ? $"pnputil exited {reading.PnputilExit?.ToString(CultureInfo.InvariantCulture) ?? "non-zero"}"
            : "pnputil did not run";
        return new DeviceEnableResult(
            DeviceEnableOutcome.Failed,
            $"The elevated step ran, but Windows still reports this device as {unwanted} "
            + $"({reading.InWantedAfter} of {reading.Total} instances in the wanted state, {pnputil}).");
    }

    // What the log says was verified, so a support log shows the END STATE and
    // not just an exit code: "exit=1 verified=already-enabled" is the case
    // that used to be reported as a UAC failure.
    internal static string VerifiedToken(DeviceEnableOutcome outcome, bool enable)
    {
        var wanted = enable ? "enabled" : "disabled";
        return outcome switch
        {
            DeviceEnableOutcome.AlreadyInState => "already-" + wanted,
            DeviceEnableOutcome.Changed => wanted,
            DeviceEnableOutcome.NoInstances => "no-instances",
            DeviceEnableOutcome.UacDeclined => "not-started",
            _ => "no",
        };
    }

    // --- elevation -------------------------------------------------------

    // Started=false means ShellExecute handed back no process at all: UAC
    // refused or the user declined it. Anything after that point happened with
    // elevation granted and must never be reported as a UAC problem.
    static (bool Started, string Sidecar, int? Exit) LaunchElevated(string scriptPath, string statusPath)
    {
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
        catch (System.ComponentModel.Win32Exception) { return (false, "", null); }
        if (p is null)
            return (false, "", null);
        using (p)
        {
            var started = DateTime.UtcNow;
            var until = started + TimeSpan.FromMinutes(2);
            var sidecar = "";
            while (DateTime.UtcNow < until)
            {
                if (File.Exists(statusPath))
                {
                    try { sidecar = File.ReadAllText(statusPath).Trim(); }
                    catch { sidecar = ""; }
                    if (ParseSidecar(sidecar) is not null)
                        break;
                }
                try
                {
                    if (p.HasExited
                        && ParseSidecar(sidecar) is null
                        && DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
                        break;
                }
                catch { /* UseShellExecute */ }
                Thread.Sleep(150);
            }
            if (!p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            int? exit = null;
            try { if (p.HasExited) exit = p.ExitCode; } catch { /* UseShellExecute */ }
            return (true, sidecar, exit);
        }
    }

    // Throws only for a caller that asked for something impossible - an
    // unusable PID, or a PID no catalog VID matches. Every state the device
    // itself can be in comes back as a DeviceEnableResult.
    internal static DeviceEnableResult Apply(string pid, bool enable)
    {
        if (string.IsNullOrEmpty(pid) || pid.Length != 4)
            throw new InvalidOperationException("DeviceEnable needs a 4-hex PID.");
        pid = pid.ToLowerInvariant();
        if (VidNeedlesForPid(pid).Length == 0)
            throw new InvalidOperationException($"No catalog VID for pid={pid}.");
        // One nonce per attempt, minted here because Apply IS the attempt. It
        // names this attempt's script and its status sidecar on both sides of
        // the elevation boundary - see StatusSidecarPath for what a PID-only
        // name lets the poller believe. AttemptNonce.Next() and not the
        // millisecond clock, because two attempts can start inside one
        // millisecond and a shared name is the same hole again. Logged so two
        // overlapping attempts can be told apart in a support log.
        var nonce = AttemptNonce.Next();
        var temp = ScriptPath(pid, nonce);
        var statusPath = StatusSidecarPath(pid, nonce);
        try { File.Delete(statusPath); } catch { /* ignore */ }
        File.WriteAllText(temp, BuildScript(pid, nonce, enable));
        Logger.Log($"DEVICE_ENABLE pid={pid} nonce={nonce.ToString(CultureInfo.InvariantCulture)} val={enable.ToString().ToLowerInvariant()}");
        var run = LaunchElevated(temp, statusPath);
        var reading = ParseSidecar(run.Sidecar);
        var result = Decide(new DeviceEnableEvidence(run.Started, reading, run.Exit), enable);
        var state = reading is { } r
            ? $"{r.InWantedAfter.ToString(CultureInfo.InvariantCulture)}/{r.Total.ToString(CultureInfo.InvariantCulture)}"
            : "n/a";
        Logger.Log(
            $"DEVICE_ENABLE pid={pid} exit={run.Exit?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} "
            + $"verified={VerifiedToken(result.Outcome, enable)} "
            + $"sidecar={reading?.Result ?? (run.Started ? "none" : "not-started")} "
            + $"state={state} pnputil={(reading?.PnputilRan == true ? "ran" : "not-run")} "
            + $"outcome={result.Outcome}");
        return result;
    }
}
