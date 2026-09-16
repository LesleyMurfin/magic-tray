// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace MagicMouseTray;

// The Mode A / Mode B flip, performed BY THE TRAY.
//
// On the patched Apple driver the 2024 Magic Mouse (PID 0323) can only be in
// one of two shapes at a time, and the shape is decided by one registry value:
//
//   Mode B  LowerFilters carries applewirelessmouse -> one unified v3 HID path
//           (no &col0x suffix). Scroll works. Battery is unreadable.
//   Mode A  applewirelessmouse removed from LowerFilters -> the v3 HID path
//           splits and a &col02 collection appears. Battery is HID input
//           report 0x90 on that COL02 collection. Scroll is DEAD.
//
// Getting a battery reading therefore means: flip to Mode A, read, flip back.
// The privileged half of that recipe is exactly two steps, measured on the
// reference PC (2026-09-15): set or remove the LowerFilters REG_MULTI_SZ value
// on the device's Enum key, then pnputil /restart-device every live BTHENUM
// instance. There is no separate installer, no service start, no unpair, no
// radio work, and nothing is ever aimed at a USB\ or HID\VID_ node - those are
// charge-cable phantoms (CM_PROB_PHANTOM), the same rule DeviceRepair follows.
//
// This used to be done by writing C:\mm-dev-queue\request.txt and running a
// developer scheduled task (MM-Dev-Cycle) that shipped with nobody. On a user
// machine both halves failed silently. That protocol is gone; the work is done
// here, in one elevated PowerShell script generated into %TEMP% and launched
// with Verb=runas, the same elevation shape as DeviceRepair / DeviceEnable.
//
// ONE UAC prompt for the whole cycle. The generated script owns the cycle and
// the tray talks to it through two files in %TEMP%. Both names carry the
// cycle's own nonce - the sentinel's StartedUnixMs, which is already unique
// and already recorded - so a transcript left behind by an earlier or
// overlapping cycle can never be read as this one's:
//
//   mm-modeflip-0323-<nonce>.status  append-only phase transcript written by
//                             the script; the tray polls it for "ready" and
//                             for a terminal token. Append-only so no phase
//                             can be missed between two polls.
//   mm-modeflip-0323-<nonce>.done    written by the TRAY once it has taken
//                             its battery reading. The script blocks on it.
//
// A leftover file under this cycle's own name is cleared first, and a clear
// that FAILS refuses the cycle: polling a transcript somebody else owns is how
// the tray would come to claim a restore it never observed.
//
// So the sequence is: script records the previous LowerFilters verbatim ->
// removes the Apple filter -> restarts the BTHENUM instances -> polls for
// COL02 -> appends "ready" -> WAITS -> tray reads the battery unelevated
// through the readPercent delegate -> tray writes done -> script restores.
//
// The restore is the safety-critical half: leaving a user in Mode A leaves
// their scroll wheel dead. Three independent belts:
//
//   1. The restore lives in a PowerShell finally block, so a failed flip, a
//      missing done file, an error, or Ctrl-C still restores.
//   2. The script re-reads LowerFilters and the present HID path shape AFTER
//      the restore and reports the verified end state - never "exit 0 means
//      done", which is what made the old recycle lie.
//   3. Before anything is elevated the tray writes a sentinel file next to its
//      log (%APPDATA%\MagicMouseTray\mode-flip.sentinel) holding every key it
//      is about to touch and that key's verbatim previous value. It is deleted
//      only after a restore the tray itself re-read and confirmed. If the tray
//      is killed mid-cycle the sentinel survives, StaleModeAOnStartup() sees
//      it, and RestoreModeB() puts the filter back in one click. A sentinel
//      that could not be written REFUSES the cycle: Mode A with no recovery
//      record is the one shape with no way back.
//
// The sentinel lives in %APPDATA%, which every unprivileged process on this
// desktop can rewrite, so it does NOT get to aim an elevated script. Two
// things cross that boundary, and both are re-checked here as if the file
// were hostile: the 4-hex PID (ValidatePid) and the recorded LowerFilters
// value itself - every name in it through IsPlainServiceName, and at least
// one of them through RepairPlanner.IsAppleFamily, before any of them is
// rendered as a quoted literal.
//
// The VALUE has to cross because Windows loads LowerFilters IN ORDER. A
// recorded ["mouhid", "applewirelessmouse"] put back as
// ["applewirelessmouse", "mouhid"] is a different value than the one that
// came off, and the tray's own CompareTargets - deliberately order-sensitive
// - would refuse it. The live key cannot supply that order: in Mode A the
// family name is not on it at all, so nothing there records where it sat.
//
// The elevated restore script still RE-DISCOVERS the keys it writes by
// walking SYSTEM\CurrentControlSet\Enum\BTHENUM itself under the same PID +
// VID + not-a-phantom gate the flip script uses - no registry path out of the
// sentinel ever reaches script text. The recorded key paths stay in the file
// and in the log as FORENSIC data: they are what CompareTargets re-reads the
// end state from and what the tray quotes back to the user when a restore has
// to be done by hand.
internal enum ModeFlipOutcome
{
    Ok,
    NotPathA,
    NoInstances,
    FlipFailed,
    BatteryUnreadable,
    RestoreFailed,
    Cancelled,
}

internal sealed record ModeFlipResult(ModeFlipOutcome Outcome, int Percent, bool RestoredToModeB, string Detail);

// One registry key the cycle may write, with the value it held BEFORE the
// cycle. PreviousPresent is the tri-state that matters: a key with no
// LowerFilters value at all is not the same as a key with an empty one, and
// restoring the wrong one of those two is how a stack loses its filter.
//
// Once it has been through the sentinel file - which lives in a user-writable
// directory - each field has exactly one consumer, and only ONE of them can
// reach generated script text:
//
//   KeyPath          FORENSIC ONLY: CompareTargets (which live key to
//                    re-read), the log, and HandRecoveryDetail's
//                    hand-recovery message. Never script text - the elevated
//                    script derives the keys it writes itself.
//   Previous         the same three, plus RestoreSequence - the recorded
//                    LowerFilters value the standalone restore has to write
//                    back, in its recorded order. This is the one field that
//                    crosses into script text, and every name in it is
//                    re-validated on the way (see RestoreSequence and
//                    BuildRestoreScript).
//   PreviousPresent  CompareTargets only. It used to drive the restore
//                    script's "Present = $true/$false" rows; the elevated
//                    script now decides absent-versus-empty from the LIVE key
//                    instead, so this field no longer feeds script text at
//                    all. It is kept because it is the one fact a human
//                    recovering by hand cannot re-derive after the fact.
//
// See the trust-boundary note in the file header.
internal sealed record ModeFlipTarget(string KeyPath, bool PreviousPresent, string[] Previous);

// StartedUnixMs doubles as the cycle nonce that tags the %TEMP% handshake
// files, so it must stay unique per cycle.
internal sealed record ModeFlipSentinel(
    int Version, long StartedUnixMs, string Pid, IReadOnlyList<ModeFlipTarget> Targets);

internal static class ModeFlip
{
    internal const string LogPrefix = "MODE_FLIP";
    internal const string V3Pid = "0323";
    internal const int DefaultTimeoutMs = 45_000;
    internal const int SentinelVersion = 1;

    // Historical latency constants from the working implementation, kept
    // verbatim: Mode B confirm poll budget 8000 ms, observed P95 flip latency
    // ~563 ms, HID path settling 1-4 s.
    internal const int ModeAVerifyMs = 8_000;
    internal const int ModeBVerifyMs = 8_000;

    // How long the elevated script will sit in Mode A waiting for the tray's
    // done file. The tray always writes it (with a failed reading if need be),
    // so this is only reached when the tray itself died.
    internal const int DoneWaitMs = 60_000;

    // Headroom the tray allows the script AFTER its own ready budget expires,
    // so a restore in progress is never interrupted by the tray giving up.
    const int RestoreGraceMs = 90_000;

    const int PollMs = 150;
    const int BatteryReadTries = 5;
    const int BatteryReadGapMs = 500;

    // Get-PnpDevice is a CIM call, so the mode probe costs ~1 s and is polled
    // at a rate that suits it rather than the 100 ms a registry read allows.
    internal const int ModePollMs = 500;

    const string LowerFiltersValueName = "LowerFilters";

    // Phase tokens the script appends to its status sidecar.
    internal const string TokenReady = "ready";
    internal const string TokenRestored = "restored";
    internal const string TokenRestoreFailed = "restore-failed";
    internal const string TokenNoInstances = "no-instances";
    internal const string TokenNoFilter = "no-filter";

    // Raised by the standalone restore when its OWN in-context BTHENUM walk
    // finds no devnode to write. Nothing was written, so the tray turns this
    // into a RestoreFailed that quotes the recorded key and value for hand
    // recovery - never into a silent success.
    internal const string TokenNoTargets = "no-targets";

    internal const string RestoreWarning =
        "Scroll mode could NOT be verified - your scroll wheel may be dead. "
        + "Use Driver > Restore scroll mode to put it back.";

    // Nothing the old dev-queue protocol used may ever reappear in a generated
    // script, and neither may the installer names DeviceEnable already bans.
    //
    // "Enum\BTHENUM\" is the MF-SENTINEL-TARGET-CONTROL guard: both scripts
    // build every key path at RUNTIME out of $enumPath and the subkey names
    // their own elevated walk returned ('HKLM:\' + $enumPath + '\' + $sub), so
    // a literal enum key path in generated text can only have come from
    // outside - i.e. from the user-writable sentinel. There is no legitimate
    // way to produce one, which is what makes banning it a real gate.
    internal static readonly string[] BannedScriptTokens =
    [
        "mm-dev-queue",
        "schtasks",
        "MM-Dev-Cycle",
        "request.txt",
        "result.txt",
        "/enable-device",
        "/disable-device",
        "/remove-device",
        "/delete-driver",
        "unpair",
        @"Enum\BTHENUM\",
    ];

    internal static string SentinelPath =>
        Path.Combine(Logger.LogDir, "mode-flip.sentinel");

    // The handshake files are named after the CYCLE, not just the device. With
    // a fixed name a leftover or concurrently written transcript is
    // indistinguishable from this cycle's, and the tray would act on somebody
    // else's "ready" (reading the battery while the device is still in Mode B)
    // and somebody else's "restored" (clearing the sentinel while a live
    // elevated script is about to enter Mode A).
    internal static string StatusSidecarPath(string pid, long nonce) =>
        Path.Combine(Path.GetTempPath(), $"mm-modeflip-{pid.ToLowerInvariant()}-{ValidateNonce(nonce)}.status");

    internal static string DoneFilePath(string pid, long nonce) =>
        Path.Combine(Path.GetTempPath(), $"mm-modeflip-{pid.ToLowerInvariant()}-{ValidateNonce(nonce)}.done");

    internal static string RestoreStatusSidecarPath(string pid, long nonce) =>
        Path.Combine(Path.GetTempPath(), $"mm-modeflip-restore-{pid.ToLowerInvariant()}-{ValidateNonce(nonce)}.status");

    internal static string ScriptPath(string pid, long nonce, bool restore) =>
        Path.Combine(
            Path.GetTempPath(),
            $"mm-modeflip{(restore ? "-restore" : "")}-{pid.ToLowerInvariant()}-{ValidateNonce(nonce)}.ps1");

    // --- public API -------------------------------------------------------

    // True when a previous cycle left a sentinel behind, i.e. it died before
    // its restore could be re-read and confirmed. The device may well be fine
    // (the script's finally usually wins), which is why this only reports that
    // the cycle did not finish cleanly - the caller offers RestoreModeB.
    internal static bool StaleModeAOnStartup()
    {
        var stale = IsStaleSentinel(ReadAllTextOrNull(SentinelPath));
        if (stale)
            Logger.Log($"{LogPrefix} phase=startup stale_sentinel=true path={SentinelPath}");
        return stale;
    }

    // One cycle at a time. Every piece of cycle state is a process-wide path -
    // one sentinel, one script file, one handshake pair - and two cycles
    // racing means two trays each computing "verified" from the other's
    // transcript, with one of them free to delete the sentinel while the other
    // still holds the device in Mode A.
    static int _cycleInFlight;

    static ModeFlipResult CycleInFlightResult()
    {
        Logger.Log($"{LogPrefix} phase=preflight outcome=Cancelled reason=cycle_in_flight");
        return new ModeFlipResult(ModeFlipOutcome.Cancelled, -1, ObserveModeB(),
            "A battery-mode cycle is already running. Nothing was changed.");
    }

    // One elevated restore, verified.
    //
    // The elevated script derives the keys it writes ITSELF. Out of the
    // sentinel this path trusts the PID and the recorded LowerFilters value,
    // both re-validated here, and hands the script nothing else. The recorded
    // key paths stay forensic: CompareTargets re-reads the live end state from
    // them and HandRecoveryDetail quotes them to the user, but neither becomes
    // script text. See the trust-boundary note in the file header.
    internal static ModeFlipResult RestoreModeB()
    {
        if (Interlocked.CompareExchange(ref _cycleInFlight, 1, 0) != 0)
            return CycleInFlightResult();
        try
        {
            return RestoreModeBCore();
        }
        finally
        {
            Interlocked.Exchange(ref _cycleInFlight, 0);
        }
    }

    static ModeFlipResult RestoreModeBCore()
    {
        if (!TryParseSentinel(ReadAllTextOrNull(SentinelPath), out var sentinel) || sentinel is null)
        {
            // Nothing recorded means nothing to put back. Say what is true now
            // rather than writing a value we never measured.
            bool modeB = ObserveModeB();
            Logger.Log($"{LogPrefix} phase=restore sentinel=none mode_b={modeB}");
            return modeB
                ? new ModeFlipResult(ModeFlipOutcome.Ok, -1, true,
                    "Scroll mode is already active - there was nothing to restore.")
                : new ModeFlipResult(ModeFlipOutcome.RestoreFailed, -1, false, RestoreWarning);
        }

        var pid = sentinel.Pid;
        foreach (var t in sentinel.Targets)
            Logger.Log($"{LogPrefix} phase=restore_start key={t.KeyPath} present={t.PreviousPresent} prev={Render(t.Previous)}");

        // The exact value the restore has to put back, in its recorded order.
        // A sentinel recording no Apple-family filter never described a Mode B
        // at all, so there is nothing to restore and picking a value anyway
        // would mean writing a driver binding the tray never measured.
        var recorded = RestoreSequence(sentinel);
        if (recorded is null)
        {
            Logger.Log($"{LogPrefix} phase=restore outcome=RestoreFailed reason=no_family_filter");
            return new ModeFlipResult(ModeFlipOutcome.RestoreFailed, -1, false,
                HandRecoveryDetail(sentinel));
        }
        Logger.Log($"{LogPrefix} phase=restore recorded={Render(recorded)}");

        // A restore is its own cycle and gets its own nonce: two restores from
        // the same sentinel must not share a transcript either. Minted by
        // AttemptNonce so two cycles that start inside one millisecond cannot
        // land on the same value - the file names are all this nonce carries.
        var nonce = AttemptNonce.Next();

        string script;
        try
        {
            script = BuildRestoreScript(pid, nonce, recorded);
        }
        catch (Exception ex)
        {
            Logger.Log($"{LogPrefix} phase=restore outcome=RestoreFailed reason=script err={ex.Message}");
            return new ModeFlipResult(ModeFlipOutcome.RestoreFailed, -1, false, RestoreWarning);
        }

        var statusPath = RestoreStatusSidecarPath(pid, nonce);
        if (!ClearHandshake(statusPath, null))
        {
            Logger.Log($"{LogPrefix} phase=restore outcome=RestoreFailed reason=stale_status path={statusPath}");
            return new ModeFlipResult(ModeFlipOutcome.RestoreFailed, -1, false, RestoreWarning);
        }
        var scriptPath = ScriptPath(pid, nonce, restore: true);

        Process? proc;
        try
        {
            proc = StartElevated(scriptPath, script);
        }
        catch (Exception ex)
        {
            Logger.Log($"{LogPrefix} phase=restore outcome=Cancelled reason=start err={ex.Message}");
            return new ModeFlipResult(ModeFlipOutcome.Cancelled, -1, ObserveModeB(),
                "Cancelled at the Windows permission prompt. Nothing was changed.");
        }

        if (proc is null)
        {
            Logger.Log($"{LogPrefix} phase=restore outcome=Cancelled reason=no-process");
            return new ModeFlipResult(ModeFlipOutcome.Cancelled, -1, ObserveModeB(),
                "Cancelled at the Windows permission prompt. Nothing was changed.");
        }

        using (proc)
        {
            var transcript = AwaitTerminal(proc, statusPath, RestoreGraceMs, null, out _);
            var terminal = TerminalToken(transcript);
            // The verdict, whole: the recorded value back, verbatim and in
            // order. Mode B is measured and logged as evidence beside it,
            // never as a condition - see FlipEvidence.
            var verified = CompareTargets(sentinel.Targets, ReadStack(pid)?.Targets);
            bool modeB = WaitForModeB(ModeBVerifyMs);
            Logger.Log($"{LogPrefix} phase=restore script_terminal={(terminal.Length == 0 ? "none" : terminal)} filters_match={Render(verified)} mode_b={modeB}");

            if (verified == true)
            {
                DeleteSentinel();
                return new ModeFlipResult(ModeFlipOutcome.Ok, -1, true,
                    "Scroll mode restored and verified.");
            }
            // The script's own walk found no devnode to aim at, so it wrote
            // nothing whatsoever. That is the one failure a user can finish by
            // hand, so the forensic key and value go into the message.
            if (string.Equals(terminal, TokenNoTargets, StringComparison.OrdinalIgnoreCase))
                return new ModeFlipResult(ModeFlipOutcome.RestoreFailed, -1, false,
                    HandRecoveryDetail(sentinel));
            return new ModeFlipResult(ModeFlipOutcome.RestoreFailed, -1, false, RestoreWarning);
        }
    }

    // Flip to Mode A, read the battery through readPercent (unelevated, in the
    // window the elevated script holds open), flip back, and report the
    // VERIFIED end state. timeoutMs is the budget for reaching Mode A; the
    // restore is always waited for on top of it.
    internal static ModeFlipResult ReadBatteryViaFlip(Func<int> readPercent, int timeoutMs = DefaultTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(readPercent);
        if (Interlocked.CompareExchange(ref _cycleInFlight, 1, 0) != 0)
            return CycleInFlightResult();
        try
        {
            return ReadBatteryViaFlipCore(readPercent, timeoutMs);
        }
        finally
        {
            Interlocked.Exchange(ref _cycleInFlight, 0);
        }
    }

    static ModeFlipResult ReadBatteryViaFlipCore(Func<int> readPercent, int timeoutMs)
    {
        const string pid = V3Pid;
        var status = CurrentV3Status();
        var stack = ReadStack(pid);
        IReadOnlyList<ModeFlipTarget> targets = stack?.Targets ?? [];
        IReadOnlyList<string> ids = stack?.InstanceIds ?? [];
        bool appleRegistered = AppleFilterRegistered(targets);

        Logger.Log($"{LogPrefix} phase=preflight pid={pid} status={Render(status)} instances={ids.Count} apple_registered={appleRegistered}");
        foreach (var t in targets)
            Logger.Log($"{LogPrefix} phase=preflight key={t.KeyPath} present={t.PreviousPresent} prev={Render(t.Previous)}");

        if (PreflightOutcome(status, ids.Count, appleRegistered) is ModeFlipOutcome refused)
        {
            Logger.Log($"{LogPrefix} phase=preflight outcome={refused}");
            bool intact = ObserveModeB();
            return new ModeFlipResult(refused, -1, intact, DetailFor(refused, -1, intact));
        }

        // This whole value is what the restore path will re-elevate
        // (RestoreSequence hands the carrier's recorded names to the script,
        // in order), and all of it goes into the forensic record, so a name
        // that is not a plain service name is refused now rather than quoted
        // and hoped for.
        foreach (var t in targets)
        {
            foreach (var name in t.Previous)
            {
                if (IsPlainServiceName(name))
                    continue;
                Logger.Log($"{LogPrefix} phase=preflight outcome=FlipFailed reason=unexpected_filter_name key={t.KeyPath} prev={Render(t.Previous)}");
                bool intact = ObserveModeB();
                return new ModeFlipResult(ModeFlipOutcome.FlipFailed, -1, intact,
                    DetailFor(ModeFlipOutcome.FlipFailed, -1, intact));
            }
        }

        // StartedUnixMs is this cycle's nonce: it names the handshake files and
        // the generated script, so nothing an earlier or concurrent cycle left
        // behind can answer for this one. AttemptNonce keeps it a Unix
        // millisecond reading while making it unique in this process, which the
        // bare clock was not.
        var nonce = AttemptNonce.Next();
        var sentinel = new ModeFlipSentinel(SentinelVersion, nonce, pid, targets);

        var statusPath = StatusSidecarPath(pid, nonce);
        var donePath = DoneFilePath(pid, nonce);
        // A handshake file under this cycle's own name that will not clear is a
        // file this cycle does not own. Polling somebody else's transcript is
        // how the tray comes to act on a "ready" it never saw and to report a
        // "restored" that has not happened yet, so refuse before elevating.
        if (!ClearHandshake(statusPath, donePath))
        {
            Logger.Log($"{LogPrefix} phase=preflight outcome=FlipFailed reason=stale_handshake path={statusPath}");
            bool intact = ObserveModeB();
            return new ModeFlipResult(ModeFlipOutcome.FlipFailed, -1, intact,
                DetailFor(ModeFlipOutcome.FlipFailed, -1, intact));
        }

        // Belt 3 of three, and the only belt that outlives the process: no
        // recovery record, no flip. Mode A with no sentinel is the one shape
        // the tray can never offer a way out of.
        if (!WriteSentinel(sentinel))
        {
            Logger.Log($"{LogPrefix} phase=preflight outcome=FlipFailed reason=no_sentinel");
            bool intact = ObserveModeB();
            return new ModeFlipResult(ModeFlipOutcome.FlipFailed, -1, intact,
                DetailFor(ModeFlipOutcome.FlipFailed, -1, intact));
        }

        string script;
        try
        {
            script = BuildFlipScript(pid, nonce);
        }
        catch (Exception ex)
        {
            DeleteSentinel();
            Logger.Log($"{LogPrefix} phase=script outcome=FlipFailed err={ex.Message}");
            bool intact = ObserveModeB();
            return new ModeFlipResult(ModeFlipOutcome.FlipFailed, -1, intact,
                DetailFor(ModeFlipOutcome.FlipFailed, -1, intact));
        }

        var scriptPath = ScriptPath(pid, nonce, restore: false);

        Process? proc = null;
        bool started;
        try
        {
            proc = StartElevated(scriptPath, script);
            started = proc is not null;
        }
        catch (Exception ex)
        {
            Logger.Log($"{LogPrefix} phase=elevate err={ex.Message}");
            started = false;
        }

        int percent = -1;
        bool ready = false;
        string terminal = "";

        if (started && proc is not null)
        {
            using (proc)
            {
                var transcript = AwaitTerminal(
                    proc,
                    statusPath,
                    timeoutMs + RestoreGraceMs,
                    onReady: () =>
                    {
                        percent = ReadWithRetries(readPercent);
                        WriteDone(donePath, percent);
                    },
                    readyBudgetMs: timeoutMs,
                    donePath: donePath,
                    readyObserved: out ready);
                terminal = TerminalToken(transcript);
            }
        }
        else
        {
            // ShellExecute handed back no process: the UAC prompt was refused,
            // so nothing was written and nothing has to be restored.
            DeleteSentinel();
        }

        var live = ReadStack(pid)?.Targets;
        // The verdict, whole: the restore is verified when, and only when, the
        // recorded LowerFilters value came back verbatim and in order. Mode B
        // is measured for the log, never as a condition - see FlipEvidence.
        var verified = CompareTargets(targets, live);
        bool modeB = WaitForModeB(ModeBVerifyMs);

        var evidence = new FlipEvidence(started, ready, terminal, percent, verified);
        var outcome = MapOutcome(evidence);

        Logger.Log($"{LogPrefix} phase=verify filters_match={Render(verified)} mode_b={modeB}");
        foreach (var t in targets)
            Logger.Log($"{LogPrefix} phase=verify key={t.KeyPath} prev={Render(t.Previous)} post={Render(LiveFor(live, t.KeyPath))}");
        Logger.Log($"{LogPrefix} phase=done started={started} ready={ready} script_terminal={(terminal.Length == 0 ? "none" : terminal)} pct={percent} outcome={outcome}");

        if (verified == true)
            DeleteSentinel();
        else if (started)
            Logger.Log($"{LogPrefix} phase=done sentinel=kept path={SentinelPath} reason=restore-unverified");

        return new ModeFlipResult(outcome, percent, verified == true,
            DetailFor(outcome, percent, verified == true));
    }

    // --- decision logic (pure) -------------------------------------------

    // What the elevated script and the tray's own re-read jointly prove.
    //
    // FiltersRestored is the recorded-value comparison (CompareTargets) and
    // nothing else. It is tri-state on purpose: null means the end state could
    // not be read at all, and that must never be reported as success.
    //
    // Observing Mode B is deliberately NOT part of it. The BTHENUM devnode
    // re-enumerates on the Bluetooth stack's own schedule - measured on the
    // reference PC (2026-09-15) as arriving AFTER the registry value is
    // already correct, and sometimes past any budget worth blocking a user on
    // - so requiring it would report a restore that did land as failed and
    // send the user to click Restore again. The registry value is what binds
    // the filter and is the only half of the end state the tray controls; Mode
    // B is still measured and logged beside every verdict as evidence.
    internal readonly record struct FlipEvidence(
        bool Started, bool ReadyObserved, string Terminal, int Percent, bool? FiltersRestored);

    internal static ModeFlipOutcome MapOutcome(FlipEvidence e)
    {
        // No elevated process at all: nothing was touched.
        if (!e.Started)
            return ModeFlipOutcome.Cancelled;
        // The script refused before writing anything.
        if (string.Equals(e.Terminal, TokenNoInstances, StringComparison.OrdinalIgnoreCase))
            return ModeFlipOutcome.NoInstances;
        if (string.Equals(e.Terminal, TokenNoFilter, StringComparison.OrdinalIgnoreCase))
            return ModeFlipOutcome.NotPathA;
        // Anything less than the recorded value back outranks every other
        // result: an unread battery is an inconvenience, a dead scroll wheel
        // is not.
        if (e.FiltersRestored != true)
            return ModeFlipOutcome.RestoreFailed;
        if (!e.ReadyObserved)
            return ModeFlipOutcome.FlipFailed;
        if (e.Percent < 0)
            return ModeFlipOutcome.BatteryUnreadable;
        return ModeFlipOutcome.Ok;
    }

    // Refusals that are decided before anything is elevated. null = proceed.
    internal static ModeFlipOutcome? PreflightOutcome(
        DriverStatus? status, int instanceCount, bool appleFilterRegistered)
    {
        if (status != DriverStatus.PathAPatched)
            return ModeFlipOutcome.NotPathA;
        if (instanceCount == 0)
            return ModeFlipOutcome.NoInstances;
        // Patched-Apple with the filter already gone from LowerFilters means
        // the stack is in Mode A right now. Flipping cannot restore a Mode B
        // that was never recorded, so refuse instead of stranding the wheel.
        if (!appleFilterRegistered)
            return ModeFlipOutcome.NotPathA;
        return null;
    }

    // Order-sensitive, case-insensitive comparison of every recorded key
    // against the live registry. A recorded key that is no longer present at
    // all is no evidence (null), not proof of failure.
    internal static bool? CompareTargets(
        IReadOnlyList<ModeFlipTarget> recorded, IReadOnlyList<ModeFlipTarget>? live)
    {
        if (live is null)
            return null;
        if (recorded.Count == 0)
            return null;
        foreach (var want in recorded)
        {
            var got = live.FirstOrDefault(t =>
                string.Equals(t.KeyPath, want.KeyPath, StringComparison.OrdinalIgnoreCase));
            if (got is null)
                return null;
            if (got.PreviousPresent != want.PreviousPresent)
                return false;
            if (got.Previous.Length != want.Previous.Length)
                return false;
            for (int i = 0; i < want.Previous.Length; i++)
            {
                if (!string.Equals(got.Previous[i], want.Previous[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        return true;
    }

    internal static string DetailFor(ModeFlipOutcome outcome, int percent, bool restoredToModeB) => outcome switch
    {
        ModeFlipOutcome.Ok when percent >= 0 =>
            $"Battery {percent}% read in battery mode. Scroll mode restored and verified.",
        ModeFlipOutcome.Ok => "Scroll mode restored and verified.",
        ModeFlipOutcome.NotPathA =>
            "This only works while the patched Apple driver is bound. Nothing was changed.",
        ModeFlipOutcome.NoInstances =>
            "No live Bluetooth connection to this mouse was found. Nothing was changed.",
        ModeFlipOutcome.FlipFailed => restoredToModeB
            ? "Could not reach battery mode, so no reading was taken. Scroll mode is intact and verified."
            : RestoreWarning,
        ModeFlipOutcome.BatteryUnreadable =>
            "Battery mode was reached but the mouse returned no reading. Scroll mode restored and verified.",
        ModeFlipOutcome.RestoreFailed => RestoreWarning,
        ModeFlipOutcome.Cancelled =>
            "Cancelled at the Windows permission prompt. Nothing was changed.",
        _ => "Unknown result.",
    };

    // Phase transcript parsing. The sidecar is append-only so a phase cannot
    // be missed between two polls.
    internal static bool HasToken(string transcript, string token)
    {
        foreach (var line in SplitLines(transcript))
        {
            if (string.Equals(line, token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // The LAST terminal token wins: the script appends "no-instances" /
    // "no-filter" / "no-targets" before it can reach any write, and
    // "restored" / "restore-failed" from its finally block.
    internal static string TerminalToken(string transcript)
    {
        var found = "";
        foreach (var line in SplitLines(transcript))
        {
            if (string.Equals(line, TokenRestored, StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, TokenRestoreFailed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, TokenNoInstances, StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, TokenNoTargets, StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, TokenNoFilter, StringComparison.OrdinalIgnoreCase))
                found = line;
        }
        return found;
    }

    internal static bool IsPhantomNode(string instanceId) =>
        instanceId.StartsWith(@"usb\", StringComparison.OrdinalIgnoreCase)
        || instanceId.StartsWith(@"hid\vid_", StringComparison.OrdinalIgnoreCase);

    internal static bool AppleFilterRegistered(IReadOnlyList<ModeFlipTarget> targets)
    {
        foreach (var t in targets)
        {
            foreach (var name in t.Previous)
            {
                if (RepairPlanner.IsAppleFamily(name))
                    return true;
            }
        }
        return false;
    }

    // The recorded LowerFilters value the standalone restore has to put back,
    // and the only thing out of the sentinel besides the PID that an elevated
    // script ever sees. It is re-validated here as if the file were hostile,
    // because it lives in a directory every unprivileged process on this
    // desktop can rewrite.
    //
    // The carrier is the recorded key whose value names an Apple-family
    // filter: that is the value Mode B had, so that is the value - whole and
    // in its recorded ORDER, because Windows loads LowerFilters in order -
    // that Mode B needs back. Every name in it must be a plain service name;
    // a single unusable name disqualifies the whole sequence, since a restore
    // that silently dropped one element would write a value the tray never
    // measured. null means this sentinel describes no Mode B to go back to,
    // which is a refusal, not a guess.
    internal static string[]? RestoreSequence(ModeFlipSentinel sentinel)
    {
        foreach (var t in sentinel.Targets)
        {
            bool family = false;
            bool usable = true;
            foreach (var name in t.Previous)
            {
                if (!IsPlainServiceName(name))
                {
                    usable = false;
                    break;
                }
                if (RepairPlanner.IsAppleFamily(name))
                    family = true;
            }
            if (usable && family)
                return [.. t.Previous];
        }
        return null;
    }

    // What to put back by hand, quoted straight out of the forensic record.
    // This is the honest tail for the two cases where the elevated script wrote
    // nothing at all: its own in-context walk derived no devnode (no-targets),
    // or the sentinel named no Apple-family filter in the first place. The key
    // path is shown to a HUMAN here; it never reaches script text.
    internal static string HandRecoveryDetail(ModeFlipSentinel sentinel)
    {
        const string lead =
            "Scroll mode could NOT be restored - your scroll wheel may be dead. "
            + "Nothing was changed on this attempt. ";
        foreach (var t in sentinel.Targets)
        {
            foreach (var name in t.Previous)
            {
                if (!RepairPlanner.IsAppleFamily(name))
                    continue;
                return lead + "To put it back by hand, set LowerFilters = "
                    + string.Join(" ", t.Previous) + " on " + t.KeyPath + ".";
            }
        }
        return lead + "The recovery record names no scroll-mode filter to put back.";
    }

    // Same charset a service key can legally hold, as DeviceRepair enforces.
    internal static bool IsPlainServiceName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (var c in name)
        {
            var ok = c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or '-' or '.';
            if (!ok)
                return false;
        }
        return true;
    }

    // --- sentinel ---------------------------------------------------------

    // The sentinel is a flat line file so a human can read it in Notepad in
    // the middle of a recovery. "end=<count>" is a TERMINATOR: WriteAllText is
    // not atomic against power loss, and a file torn mid-value - a half
    // written filter= line still parses as a plain service name - must be
    // refused rather than acted on.
    //
    // The filter= lines under a key are the recorded REG_MULTI_SZ, one element
    // per line, in the order the registry held them. That order is load
    // bearing, not incidental: Windows loads LowerFilters in order, and it is
    // the order RestoreSequence hands to the elevated restore and the order
    // CompareTargets verifies the end state against. Nothing here may sort,
    // deduplicate or otherwise tidy them. Version 1 has always recorded the
    // whole sequence this way, which is why restoring the order needed no
    // format change and old sentinels still parse.
    internal static string FormatSentinel(ModeFlipSentinel s)
    {
        var sb = new StringBuilder();
        sb.Append("v=").Append(s.Version).Append('\n');
        sb.Append("started=").Append(s.StartedUnixMs).Append('\n');
        sb.Append("pid=").Append(s.Pid).Append('\n');
        foreach (var t in s.Targets)
        {
            // key= opens a target; the present= and filter= lines that follow
            // belong to it. Key paths hold '\' and '&', so no single-character
            // separator is safe - one value per line is.
            sb.Append("key=").Append(t.KeyPath).Append('\n');
            sb.Append("present=").Append(t.PreviousPresent ? "true" : "false").Append('\n');
            foreach (var name in t.Previous)
                sb.Append("filter=").Append(name).Append('\n');
        }
        sb.Append("end=").Append(s.Targets.Count).Append('\n');
        return sb.ToString();
    }

    internal static bool TryParseSentinel(string? raw, out ModeFlipSentinel? sentinel)
    {
        sentinel = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        int version = 0;
        long started = 0;
        var pid = "";
        int declared = -1;
        var targets = new List<ModeFlipTarget>();
        string? key = null;
        bool present = false;
        bool presentSeen = false;
        var filters = new List<string>();

        // A target is only closed once its tri-state was stated outright.
        // Defaulting present to false would turn a file torn between key= and
        // present= into "this key carried no LowerFilters", and the restore
        // would then take that as licence to leave the filter off - the exact
        // Mode A dead-scroll state recovery exists to undo.
        bool Flush()
        {
            if (key is null)
                return true;
            if (!presentSeen)
                return false;
            targets.Add(new ModeFlipTarget(key, present, [.. filters]));
            key = null;
            present = false;
            presentSeen = false;
            filters.Clear();
            return true;
        }

        foreach (var line in SplitLines(raw))
        {
            var split = line.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0)
                continue;
            var name = line[..split];
            var value = line[(split + 1)..];
            switch (name)
            {
                case "v":
                    if (!int.TryParse(value, out version)) return false;
                    break;
                case "started":
                    if (!long.TryParse(value, out started)) return false;
                    break;
                case "pid":
                    pid = value;
                    break;
                case "key":
                    if (!Flush()) return false;
                    if (value.Length == 0) return false;
                    key = value;
                    break;
                case "present":
                    // With no key open the file is scrambled, and silently
                    // attributing the line to the NEXT key would move a
                    // tri-state onto a target it never described.
                    if (key is null) return false;
                    present = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                    presentSeen = true;
                    break;
                case "filter":
                    if (key is null) return false;
                    if (!IsPlainServiceName(value)) return false;
                    filters.Add(value);
                    break;
                case "end":
                    if (!int.TryParse(value, out declared)) return false;
                    break;
                default:
                    break;
            }
        }
        if (!Flush())
            return false;

        if (version != SentinelVersion)
            return false;
        if (pid.Length != 4)
            return false;
        // A sentinel with nothing to put back cannot drive a restore, so it is
        // not a stale cycle - it is a corrupt file.
        if (targets.Count == 0)
            return false;
        // Truncated, or a terminator that disagrees with what was read.
        if (declared != targets.Count)
            return false;

        sentinel = new ModeFlipSentinel(version, started, pid, targets);
        return true;
    }

    internal static bool IsStaleSentinel(string? raw) => TryParseSentinel(raw, out _);

    // Written to a sibling name and MOVED into place, so no reader can catch a
    // half-written record. False means the record did not land, and the caller
    // must then refuse to flip rather than enter Mode A with no way back.
    static bool WriteSentinel(ModeFlipSentinel s)
    {
        var tmp = SentinelPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Logger.LogDir);
            File.WriteAllText(tmp, FormatSentinel(s), Encoding.ASCII);
            File.Move(tmp, SentinelPath, overwrite: true);
            Logger.Log($"{LogPrefix} phase=sentinel_write path={SentinelPath} targets={s.Targets.Count}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"{LogPrefix} phase=sentinel_write err={ex.Message}");
            Delete(tmp);
            return false;
        }
    }

    static void DeleteSentinel()
    {
        if (Delete(SentinelPath))
            Logger.Log($"{LogPrefix} phase=sentinel_clear path={SentinelPath}");
    }

    // Every handshake file must be gone before the script is launched. Delete
    // returns false on a sharing violation, which is exactly what a still
    // running cycle holding its transcript open in Add-Content produces, so a
    // file that will not go away means this cycle does not own its own names.
    static bool ClearHandshake(string statusPath, string? donePath)
    {
        if (!ClearOne(statusPath))
            return false;
        return donePath is null || ClearOne(donePath);
    }

    static bool ClearOne(string path)
    {
        Delete(path);
        try { return !File.Exists(path); }
        catch { return false; }
    }

    // --- generated scripts ------------------------------------------------

    // Shared half of both scripts: the BTHENUM walker's PID/VID gate, the
    // phantom-node gate, and the present-device measurement that decides
    // Mode A vs Mode B.
    //
    // Presence comes from Get-PnpDevice -PresentOnly, which is the same
    // DIGCF_PRESENT question HidNative.EnumerateHidPaths asks the tray side.
    // It is asked rather than read out of the registry on purpose: measured on
    // the reference PC (2026-09-15), Control\DeviceClasses still lists the
    // unified BT interface AND both collection interfaces for this PID at the
    // same time, and its per-interface presence flag sits under a
    // #\Properties subkey an unelevated read cannot even open. A registry
    // walk there would have reported Mode A and Mode B simultaneously.
    //
    // WHAT COUNTS AS A TARGET, and why the derivation gate is not just a PID
    // substring. Measured on the reference PC (2026-09-15), ONE paired mouse
    // puts four keys in the BTHENUM hive whose names carry the PID and pass
    // the VID needle:
    //
    //   {00001124-...}_VID&0001004c_PID&0323   LowerFilters absent
    //     \9&73b8b28&0&D0C0...C4D_C00000000    LowerFilters set, Service=HidBth
    //   {00001200-...}_VID&0001004c_PID&0323   LowerFilters absent
    //     \9&73b8b28&0&D0C0...C4D_C00000000    LowerFilters absent, NO Service
    //
    // LowerFilters lives ONLY on the instance key under the {00001124-...} HID
    // service-class UUID - the one with a Service bound. The {00001200-...}
    // sibling is the PnP-information SDP record: same PID, same VID, no driver
    // and nothing to filter. So Get-BthenumNode, the in-context derivation the
    // restore aims its writes with, gates on that UUID prefix AND a non-empty
    // Service on top of the PID/VID/phantom gate. Do NOT simplify that back to
    // a PID substring: it would point an elevated write at a node with no
    // stack, and the UUID half is also what makes the script's idea of a
    // target agree with the tray's own
    // DeviceSnapshotReader.BthenumKeyMatchesPid instead of being strictly
    // broader than it.
    const string ScriptPreamble = """
$ErrorActionPreference = 'Continue'
$targetPid = '__PID__'
$filter = '__FILTER__'
$vidNeedles = @(__VIDS__)
$enumPath = 'SYSTEM\CurrentControlSet\Enum\BTHENUM'
$modePollMs = __MODEPOLL_MS__
$modeABudgetMs = __MODEA_MS__
$modeBBudgetMs = __MODEB_MS__
$doneWaitMs = __DONEWAIT_MS__
$nonce = '__NONCE__'
$hidUuidPrefix = '{00001124-0000-1000-8000-00805f9b34fb}'

function Write-Phase([string]$line) {
    Write-Host ('MODE_FLIP ' + $line)
    try { Add-Content -LiteralPath $statusFile -Value $line -Encoding ASCII } catch { }
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

# Presence and contents are separate facts. PowerShell enumerates collections
# on function output, so an empty REG_MULTI_SZ would come back as $null and be
# indistinguishable from "no LowerFilters value at all" - the comma operator
# keeps an empty array an empty array. The restore leans on exactly that
# distinction: ABSENT is the flip's Clear-LowerFilters signature, while
# PRESENT-but-empty is a key the flip never touched.
function Read-Filters($key) {
    $v = $key.GetValue('LowerFilters')
    if ($null -eq $v) { return $null }
    return ,@(@($v) | ForEach-Object { [string]$_ })
}

function Show-Names($names) {
    if ($null -eq $names) { return '<absent>' }
    return '[' + ((@($names) | ForEach-Object { [string]$_ }) -join '|') + ']'
}

# The Apple filter family, the same prefix rule as RepairPlanner.IsAppleFamily.
# The flip body keeps its own inline copy of this test: that script is the
# reviewed model for in-context target discovery and is left byte for byte
# alone.
#
# [string]$n is load-bearing. PowerShell member enumeration turns
# $array.ToLowerInvariant().StartsWith($x) into an ARRAY of booleans, and a
# non-empty array is truthy - so a nested or non-string element would answer
# "yes, the family filter is here" when it plainly is not. Casting first makes
# every element exactly one string comparison.
function Test-FamilyName($names) {
    if ($null -eq $names) { return $false }
    $low = $filter.ToLowerInvariant()
    foreach ($n in @($names)) {
        $s = [string]$n
        if ($s.Length -eq 0) { continue }
        if ($s.ToLowerInvariant().StartsWith($low)) { return $true }
    }
    return $false
}

# Every present HID child devnode of this PID. The HID children are what
# split: Mode A grows a COL02 collection, Mode B has exactly one unified
# node. BTHENUM parents and the USB charge-cable phantoms are excluded - the
# phantoms are not present anyway, which is the whole point of -PresentOnly.
function Get-PresentHidInstance {
    $found = New-Object System.Collections.Generic.List[string]
    $devices = $null
    try {
        $devices = @(Get-PnpDevice -PresentOnly -Class HIDClass -ErrorAction Stop)
    } catch {
        Write-Phase ('hid-probe-unavailable err=' + $_.Exception.Message)
        return $found
    }
    $needleA = 'pid_' + $targetPid.ToLowerInvariant()
    $needleB = 'pid&' + $targetPid.ToLowerInvariant()
    foreach ($d in $devices) {
        $id = [string]$d.InstanceId
        if ([string]::IsNullOrEmpty($id)) { continue }
        $low = $id.ToLowerInvariant()
        if (-not $low.StartsWith('hid\')) { continue }
        if ($low.StartsWith('hid\vid_')) { continue }
        if (-not ($low.Contains($needleA) -or $low.Contains($needleB))) { continue }
        [void]$found.Add($id)
    }
    return $found
}

# Mode A: the COL02 vendor collection is present. That collection carries HID
# input report 0x90, which is the only place a battery percent can be read.
function Test-ModeA {
    foreach ($p in @(Get-PresentHidInstance)) {
        if ($p.ToLowerInvariant().Contains('col02')) { return $true }
    }
    return $false
}

# Mode B: at least one present v3 HID node and NONE of them split into a
# collection. A split node means mouhid sits on col01 and the wheel is dead.
function Test-ModeB {
    $paths = @(Get-PresentHidInstance)
    if ($paths.Count -eq 0) { return $false }
    foreach ($p in $paths) {
        if ($p.ToLowerInvariant().Contains('&col0')) { return $false }
    }
    return $true
}

function Wait-Mode([scriptblock]$probe, [int]$budgetMs, [int]$pollMs) {
    $deadline = (Get-Date).AddMilliseconds($budgetMs)
    while ($true) {
        if (& $probe) { return $true }
        if ((Get-Date) -ge $deadline) { return $false }
        Start-Sleep -Milliseconds $pollMs
    }
}

function Get-BthenumTarget {
    $targets = New-Object System.Collections.Generic.List[object]
    $ids = New-Object System.Collections.Generic.List[string]
    $needleA = 'pid_' + $targetPid.ToLowerInvariant()
    $needleB = 'pid&' + $targetPid.ToLowerInvariant()
    $root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($enumPath)
    if ($root) {
        foreach ($sub in $root.GetSubKeyNames()) {
            $low = $sub.ToLowerInvariant()
            if (-not ($low.Contains($needleA) -or $low.Contains($needleB))) { continue }
            if (-not (Test-Vid $sub)) { continue }
            if (Test-SkipPath ('BTHENUM\' + $sub)) { continue }
            $dev = $root.OpenSubKey($sub)
            if (-not $dev) { continue }
            $devVal = Read-Filters $dev
            if ($null -ne $devVal) {
                [void]$targets.Add([pscustomobject]@{
                    Path = 'HKLM:\' + $enumPath + '\' + $sub
                    Previous = $devVal
                })
            }
            foreach ($inst in $dev.GetSubKeyNames()) {
                $full = 'BTHENUM\' + $sub + '\' + $inst
                if (Test-SkipPath $full) { continue }
                [void]$ids.Add($full)
                $instKey = $dev.OpenSubKey($inst)
                if ($instKey) {
                    $instVal = Read-Filters $instKey
                    if ($null -ne $instVal) {
                        [void]$targets.Add([pscustomobject]@{
                            Path = 'HKLM:\' + $enumPath + '\' + $sub + '\' + $inst
                            Previous = $instVal
                        })
                    }
                    $instKey.Dispose()
                }
            }
            $dev.Dispose()
        }
        $root.Dispose()
    }
    return [pscustomobject]@{ Targets = $targets; Ids = $ids }
}

# The restore's own in-context derivation, and the ONLY thing deciding which
# keys the elevated restore writes - nothing out of the sentinel aims it.
#
# Same PID needle, same VID needle and the same phantom gate as
# Get-BthenumTarget, plus the two gates the measured hive demands (see the C#
# comment above ScriptPreamble): the HID service-class UUID prefix, and a bound
# Service. A key with no Service is not a devnode, so its LowerFilters cannot
# affect any stack - and the {00001200-...} SDP sibling carries this PID and
# VID while being exactly that.
#
# Unlike Get-BthenumTarget this KEEPS a key whose LowerFilters is absent: a
# cleared value is precisely what the flip leaves behind on a key where the
# Apple filter was the only name.
function Get-BthenumNode {
    $nodes = New-Object System.Collections.Generic.List[object]
    $ids = New-Object System.Collections.Generic.List[string]
    $needleA = 'pid_' + $targetPid.ToLowerInvariant()
    $needleB = 'pid&' + $targetPid.ToLowerInvariant()
    $root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($enumPath)
    if ($root) {
        foreach ($sub in $root.GetSubKeyNames()) {
            $low = $sub.ToLowerInvariant()
            if (-not ($low.Contains($needleA) -or $low.Contains($needleB))) { continue }
            if (-not (Test-Vid $sub)) { continue }
            if (Test-SkipPath ('BTHENUM\' + $sub)) { continue }
            if (-not $low.StartsWith($hidUuidPrefix.ToLowerInvariant())) { continue }
            $dev = $root.OpenSubKey($sub)
            if (-not $dev) { continue }
            foreach ($inst in $dev.GetSubKeyNames()) {
                $full = 'BTHENUM\' + $sub + '\' + $inst
                if (Test-SkipPath $full) { continue }
                [void]$ids.Add($full)
                $instKey = $dev.OpenSubKey($inst)
                if ($instKey) {
                    $svc = [string]$instKey.GetValue('Service')
                    $val = Read-Filters $instKey
                    if (-not [string]::IsNullOrEmpty($svc)) {
                        [void]$nodes.Add([pscustomobject]@{
                            Path = 'HKLM:\' + $enumPath + '\' + $sub + '\' + $inst
                            Service = $svc
                            Names = $val
                        })
                    }
                    $instKey.Dispose()
                }
            }
            $dev.Dispose()
        }
        $root.Dispose()
    }
    return [pscustomobject]@{ Nodes = $nodes; Ids = $ids }
}

# "$id" is required: BTHENUM instance IDs contain '&'. Unquoted, PowerShell
# treats '&' as the call operator and pnputil never runs.
function Restart-Stack($ids, [string]$tag) {
    $failed = 0
    foreach ($id in $ids) {
        Write-Phase ($tag + ' restart ' + $id)
        & pnputil.exe /restart-device "$id"
        if ($LASTEXITCODE -ne 0) {
            $failed++
            Write-Phase ($tag + ' restart-failed rc=' + $LASTEXITCODE + ' ' + $id)
        }
    }
    return $failed
}

function Write-LowerFilters([string]$path, $names) {
    try {
        Set-ItemProperty -LiteralPath $path -Name 'LowerFilters' -Value ([string[]]@($names)) -Type MultiString -ErrorAction Stop
        return $true
    } catch {
        Write-Phase ('write-error key=' + $path + ' err=' + $_.Exception.Message)
        return $false
    }
}

function Clear-LowerFilters([string]$path) {
    try {
        Remove-ItemProperty -LiteralPath $path -Name 'LowerFilters' -Force -ErrorAction Stop
        return $true
    } catch {
        Write-Phase ('clear-error key=' + $path + ' err=' + $_.Exception.Message)
        return $false
    }
}

function Read-LowerFiltersAt([string]$hklmPath) {
    try {
        $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($hklmPath.Substring(6))
        if (-not $k) { return $null }
        $v = Read-Filters $k
        $k.Dispose()
        return $v
    } catch {
        return $null
    }
}
""";

    // The whole cycle, one elevation. The restore is in a finally block, so a
    // failed flip, a tray that never answers, an error, or Ctrl-C all still
    // put the recorded previous value back and still re-read the end state.
    const string FlipBody = """

$statusFile = Join-Path $env:TEMP ('mm-modeflip-' + $targetPid + '-' + $nonce + '.status')
$doneFile = Join-Path $env:TEMP ('mm-modeflip-' + $targetPid + '-' + $nonce + '.done')
Write-Phase ('started pid=' + $targetPid)

$stack = Get-BthenumTarget
$targets = $stack.Targets
$ids = $stack.Ids
Write-Phase ('instances=' + $ids.Count)
if ($ids.Count -eq 0) {
    Write-Phase 'no-instances'
    exit 2
}

# Every previous value is recorded VERBATIM before any write, so this cycle is
# reversible by hand from the transcript alone.
$carriers = New-Object System.Collections.Generic.List[object]
foreach ($t in $targets) {
    Write-Phase ('prev key=' + $t.Path + ' LowerFilters=' + (Show-Names $t.Previous))
    foreach ($n in @($t.Previous)) {
        if ($n -and $n.ToLowerInvariant().StartsWith($filter.ToLowerInvariant())) {
            [void]$carriers.Add($t)
            break
        }
    }
}
if ($carriers.Count -eq 0) {
    Write-Phase ('no-filter expected=' + $filter)
    Write-Phase 'no-filter'
    exit 3
}

$restored = $false
$ready = $false
try {
    foreach ($t in $carriers) {
        $kept = New-Object System.Collections.Generic.List[string]
        foreach ($n in @($t.Previous)) {
            if ($n -and $n.ToLowerInvariant().StartsWith($filter.ToLowerInvariant())) { continue }
            [void]$kept.Add([string]$n)
        }
        if ($kept.Count -eq 0) {
            Write-Phase ('flip clear key=' + $t.Path)
            [void](Clear-LowerFilters $t.Path)
        } else {
            Write-Phase ('flip write key=' + $t.Path + ' LowerFilters=' + (Show-Names $kept))
            [void](Write-LowerFilters $t.Path $kept)
        }
    }

    [void](Restart-Stack $ids 'flip')
    $ready = Wait-Mode { Test-ModeA } $modeABudgetMs $modePollMs
    Write-Phase ('mode_a=' + $ready)

    if ($ready) {
        # The tray now reads the battery unelevated and writes the done file.
        Write-Phase 'ready'
        $seen = Wait-Mode { Test-Path -LiteralPath $doneFile } $doneWaitMs 100
        Write-Phase ('done_signal=' + $seen)
    } else {
        Write-Phase 'flip-failed'
    }
}
finally {
    # ALWAYS restore, whatever happened above: a dead scroll wheel is worse
    # than a missing battery percent.
    foreach ($t in $carriers) {
        Write-Phase ('restore key=' + $t.Path + ' LowerFilters=' + (Show-Names $t.Previous))
        [void](Write-LowerFilters $t.Path $t.Previous)
    }
    [void](Restart-Stack $ids 'restore')

    $modeB = Wait-Mode { Test-ModeB } $modeBBudgetMs $modePollMs
    $allMatch = $true
    foreach ($t in $carriers) {
        $now = Read-LowerFiltersAt $t.Path
        Write-Phase ('post key=' + $t.Path + ' prev=' + (Show-Names $t.Previous) + ' now=' + (Show-Names $now))
        if ($null -eq $now) {
            $allMatch = $false
        } elseif (((@($now) | ForEach-Object { [string]$_ }) -join '|') -ne ((@($t.Previous) | ForEach-Object { [string]$_ }) -join '|')) {
            $allMatch = $false
        }
    }
    Write-Phase ('post mode_b=' + $modeB + ' filters_match=' + $allMatch)
    if ($modeB -and $allMatch) {
        $restored = $true
        Write-Phase 'restored'
    } else {
        Write-Phase 'restore-failed'
    }
}

if ($restored) { exit 0 }
exit 1
""";

    // Standalone restore, aimed entirely from inside the elevated context.
    //
    // MF-SENTINEL-TARGET-CONTROL: the sentinel sits where every unprivileged
    // process can rewrite it, so it does NOT get to name the keys this script
    // writes. What comes in is $targetPid and $restoreNames - the recorded
    // LowerFilters value, every name in it re-validated in C# and rendered as
    // a quoted literal - and the script derives its own targets with
    // Get-BthenumNode, the same walk and gates the flip uses. No recorded KEY
    // PATH appears anywhere in here, and the Enum\BTHENUM\ entry in
    // BannedScriptTokens is what stops one coming back.
    //
    // What "the same value back" means once the targets are re-derived. The
    // recorded value IS the answer, in its recorded ORDER: the flip took
    // family names off a live LowerFilters and copied every other name
    // through untouched, so the value recorded before the flip is exactly the
    // value Mode B had. Windows loads LowerFilters in order, and that
    // recorded order is also what the tray's order-sensitive CompareTargets
    // re-reads for - so a restore that writes the same names in a different
    // order writes a value that was never measured and fails verification.
    //
    // The live key cannot supply that order: in Mode A the family name is off
    // it entirely, so nothing there says whether it sat first, last or in the
    // middle. The live value is still read, for one thing - a name on it that
    // the record does not know about was added after the record was taken,
    // and dropping it would be a loss, so it goes on the end.
    //
    //   absent            the flip's Clear-LowerFilters signature - the family
    //                     name was the only entry -> write the recorded value
    //   non-family names  the flip's filtered rebuild -> write the recorded
    //                     value, then anything live it does not name
    //   present but empty the flip CLEARS rather than empties, so this key was
    //                     never a carrier -> leave it completely alone
    //   already family    this key is in Mode B already -> nothing to write,
    //                     but still verified at the end
    const string RestoreBody = """

$statusFile = Join-Path $env:TEMP ('mm-modeflip-restore-' + $targetPid + '-' + $nonce + '.status')
$restoreNames = @(__RESTORE_NAMES__)
Write-Phase ('restore-only pid=' + $targetPid + ' recorded=' + (Show-Names $restoreNames))

$stack = Get-BthenumNode
$nodes = $stack.Nodes
$ids = $stack.Ids
Write-Phase ('derived nodes=' + $nodes.Count + ' instances=' + $ids.Count)

$expect = New-Object System.Collections.Generic.List[object]
$pending = New-Object System.Collections.Generic.List[object]
foreach ($n in $nodes) {
    Write-Phase ('node key=' + $n.Path + ' svc=' + $n.Service + ' LowerFilters=' + (Show-Names $n.Names))
    if (Test-FamilyName $n.Names) {
        Write-Phase ('skip key=' + $n.Path + ' reason=already-scroll-mode')
        [void]$expect.Add($n)
        continue
    }
    if (($null -ne $n.Names) -and (@($n.Names).Count -eq 0)) {
        Write-Phase ('skip key=' + $n.Path + ' reason=present-empty')
        continue
    }
    # The recorded value first, in the order it was recorded in. Nothing here
    # sorts, filters or reorders it - that order is the whole point.
    $want = New-Object System.Collections.Generic.List[string]
    foreach ($m in @($restoreNames)) {
        $s = [string]$m
        if ($s.Length -eq 0) { continue }
        [void]$want.Add($s)
    }
    # Then whatever the LIVE key carries that the record does not name: it was
    # added after the record was taken, and a restore may not lose it.
    #
    # @($null) is a one-element array holding $null, so an ABSENT value would
    # otherwise contribute an empty string and the key would come back as
    # REG_MULTI_SZ { applewirelessmouse, "" } - a value the flip never wrote.
    # -contains compares strings case-insensitively, which is the same
    # spelling rule CompareTargets applies to the end state.
    foreach ($m in @($n.Names)) {
        $s = [string]$m
        if ($s.Length -eq 0) { continue }
        if ($want -contains $s) { continue }
        [void]$want.Add($s)
    }
    [void]$expect.Add($n)
    [void]$pending.Add([pscustomobject]@{ Path = $n.Path; Want = $want })
}

# Nothing derivable to aim at, so write NOTHING and say so. The tray turns
# this token into a failure that quotes the recorded key and value for hand
# recovery - the honest answer when the elevated context cannot find the
# device the recovery record describes.
if ($expect.Count -eq 0) {
    Write-Phase 'no-targets'
    exit 2
}

# The write comes FIRST and is gated on nothing. Instance re-discovery only
# decides whether a restart is worth issuing; the registry value is what
# actually revives the wheel, and this is the single one-click way out of a
# dead one, so it must never be skipped because a devnode went missing or
# because two walkers disagreed about a subkey name.
foreach ($p in $pending) {
    Write-Phase ('restore key=' + $p.Path + ' LowerFilters=' + (Show-Names $p.Want))
    [void](Write-LowerFilters $p.Path $p.Want)
}

if ($ids.Count -eq 0) {
    Write-Phase 'restart-skipped instances=0'
} else {
    [void](Restart-Stack $ids 'restore')
}

$modeB = Wait-Mode { Test-ModeB } $modeBBudgetMs $modePollMs
$allMatch = $true
foreach ($n in $expect) {
    $now = Read-LowerFiltersAt $n.Path
    Write-Phase ('post key=' + $n.Path + ' before=' + (Show-Names $n.Names) + ' now=' + (Show-Names $now))
    if (-not (Test-FamilyName $now)) { $allMatch = $false }
}
Write-Phase ('post mode_b=' + $modeB + ' filters_match=' + $allMatch)
if ($modeB -and $allMatch) {
    Write-Phase 'restored'
    exit 0
}
Write-Phase 'restore-failed'
exit 1
""";

    internal static string BuildFlipScript(string pid, long nonce) =>
        Finish(Preamble(pid, nonce) + FlipBody);

    // recorded is the ONE thing out of the sentinel besides the PID that
    // reaches script text: the LowerFilters value to put back, in the order it
    // was recorded in. It is re-validated here as if the file were hostile,
    // and ALL of it is - a sequence is only as safe as its worst element, so
    // every name must be a plain service name and at least one must belong to
    // the Apple filter family per the catalog. A refusal throws, which the
    // caller turns into the ordinary restore-failed path that quotes the
    // recorded value for hand recovery. No key path is accepted at any price -
    // the script derives its own.
    internal static string BuildRestoreScript(string pid, long nonce, IReadOnlyList<string> recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        if (recorded.Count == 0)
            throw new InvalidOperationException("ModeFlip needs a recorded LowerFilters value to restore.");
        foreach (var name in recorded)
        {
            if (!IsPlainServiceName(name))
                throw new InvalidOperationException($"ModeFlip refuses filter name '{name}'.");
        }
        if (!recorded.Any(RepairPlanner.IsAppleFamily))
            throw new InvalidOperationException(
                $"ModeFlip refuses a recorded value with no family filter: {string.Join("|", recorded)}.");
        var body = RestoreBody.Replace(
            "__RESTORE_NAMES__",
            string.Join(", ", recorded.Select(Literal)),
            StringComparison.Ordinal);
        return Finish(Preamble(pid, nonce) + body);
    }

    static string Preamble(string pid, long nonce)
    {
        pid = ValidatePid(pid);
        var needles = DeviceEnable.VidNeedlesForPid(pid);
        if (needles.Length == 0)
            throw new InvalidOperationException($"No catalog VID for pid={pid}.");
        return ScriptPreamble
            .Replace("__PID__", pid, StringComparison.Ordinal)
            .Replace("__NONCE__", ValidateNonce(nonce), StringComparison.Ordinal)
            .Replace("__FILTER__", DriverPackageCatalog.AppleFilterServiceName, StringComparison.Ordinal)
            .Replace("__VIDS__", string.Join(", ", needles.Select(Literal)), StringComparison.Ordinal)
            .Replace("__MODEPOLL_MS__", ModePollMs.ToString(), StringComparison.Ordinal)
            .Replace("__MODEA_MS__", ModeAVerifyMs.ToString(), StringComparison.Ordinal)
            .Replace("__MODEB_MS__", ModeBVerifyMs.ToString(), StringComparison.Ordinal)
            .Replace("__DONEWAIT_MS__", DoneWaitMs.ToString(), StringComparison.Ordinal);
    }

    // Last gate before the script can reach an elevated shell: the dev-queue
    // protocol, the installer names DeviceEnable bans, the destructive pnputil
    // verbs and any literal Enum key path must not be in it. That last one is
    // the build-time half of MF-SENTINEL-TARGET-CONTROL - see
    // BannedScriptTokens for why a literal can only have come from outside.
    static string Finish(string script)
    {
        foreach (var banned in BannedScriptTokens)
        {
            if (script.Contains(banned, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ModeFlip refuses {banned}.");
        }
        foreach (var banned in DeviceEnable.ForbiddenNames)
        {
            if (script.Contains(banned, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ModeFlip refuses {banned}.");
        }
        return script;
    }

    static string Literal(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    static string ValidatePid(string pid)
    {
        if (string.IsNullOrEmpty(pid) || pid.Length != 4)
            throw new InvalidOperationException("ModeFlip needs a 4-hex PID.");
        foreach (var c in pid)
        {
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
                throw new InvalidOperationException($"ModeFlip needs a 4-hex PID, got '{pid}'.");
        }
        return pid.ToLowerInvariant();
    }

    // The nonce crosses the elevation boundary as part of a file name on both
    // sides, so it is rendered as plain digits and nothing else.
    static string ValidateNonce(long nonce)
    {
        if (nonce <= 0)
            throw new InvalidOperationException($"ModeFlip needs a positive cycle nonce, got {nonce}.");
        return nonce.ToString();
    }

    // --- live reads -------------------------------------------------------

    internal sealed record ModeFlipStack(
        IReadOnlyList<ModeFlipTarget> Targets, IReadOnlyList<string> InstanceIds);

    static DriverStatus? CurrentV3Status()
    {
        try
        {
            // The lasting radio choice matters: Mode A itself clears the Apple
            // LowerFilters, so a sticky pathA choice is what keeps a mouse
            // found in Mode A classified as PathAPatched instead of Stock.
            var lasting = Config.Load().Driver0323;
            foreach (var d in DriverHealthChecker.GetPerDeviceStatus(lasting))
            {
                if (string.Equals(d.Pid, V3Pid, StringComparison.OrdinalIgnoreCase))
                    return d.Status;
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"{LogPrefix} phase=status err={ex.Message}");
            return null;
        }
    }

    // Read-only, unelevated: the Enum hive is readable without elevation,
    // which is what lets the tray record the previous value itself and verify
    // the end state independently of the script's own verdict.
    static ModeFlipStack? ReadStack(string pid)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DeviceSnapshotReader.BtEnumBase, writable: false);
            if (root is null)
                return null;
            var targets = new List<ModeFlipTarget>();
            var ids = new List<string>();
            foreach (var sub in root.GetSubKeyNames())
            {
                if (!DeviceSnapshotReader.BthenumKeyMatchesPid(sub, pid))
                    continue;
                if (IsPhantomNode(@"BTHENUM\" + sub))
                    continue;
                using var dev = root.OpenSubKey(sub, writable: false);
                if (dev is null)
                    continue;
                var devPath = $@"HKLM:\{DeviceSnapshotReader.BtEnumBase}\{sub}";
                var devValue = ReadLowerFilters(dev);
                targets.Add(new ModeFlipTarget(devPath, devValue.Present, devValue.Value));
                foreach (var inst in dev.GetSubKeyNames())
                {
                    var full = $@"BTHENUM\{sub}\{inst}";
                    if (IsPhantomNode(full))
                        continue;
                    ids.Add(full);
                    using var instKey = dev.OpenSubKey(inst, writable: false);
                    if (instKey is null)
                        continue;
                    var instValue = ReadLowerFilters(instKey);
                    targets.Add(new ModeFlipTarget(
                        $@"HKLM:\{DeviceSnapshotReader.BtEnumBase}\{sub}\{inst}",
                        instValue.Present, instValue.Value));
                }
            }
            return new ModeFlipStack(targets, ids);
        }
        catch (Exception ex)
        {
            Logger.Log($"{LogPrefix} phase=read_stack err={ex.Message}");
            return null;
        }
    }

    // LowerFilters is REG_MULTI_SZ or REG_SZ depending on who wrote it.
    static (bool Present, string[] Value) ReadLowerFilters(RegistryKey key)
    {
        var value = key.GetValue(LowerFiltersValueName);
        if (value is string[] arr)
            return (true, arr);
        if (value is string s)
        {
            string[] single = s.Length == 0 ? [] : [s];
            return (true, single);
        }
        return (false, []);
    }

    static bool ObserveModeB()
    {
        try { return V3RecycleManager.IsV3InModeB(); }
        catch (Exception ex)
        {
            Logger.Log($"{LogPrefix} phase=observe err={ex.Message}");
            return false;
        }
    }

    static bool WaitForModeB(int budgetMs)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        do
        {
            if (ObserveModeB())
                return true;
            Thread.Sleep(100);
        } while (Environment.TickCount64 < deadline);
        return false;
    }

    // A ready-made readPercent delegate for ReadBatteryViaFlip.
    //
    // In Mode A BOTH collections are present: col01 is the standard HID mouse
    // page and answers nothing useful, col02 is the vendor collection that
    // carries input report 0x90. DeviceRegistry.Discover returns whichever the
    // SetupDi enumeration yielded first, so it silently read -1 from col01 -
    // col02 has to be targeted by name.
    internal static int ReadCol02BatteryPercent()
    {
        var path = HidNative.EnumerateHidPaths().FirstOrDefault(p =>
            V3RecycleManager.IsV3Path(p)
            && p.Contains("col02", StringComparison.OrdinalIgnoreCase));
        if (path is null)
        {
            Logger.Log($"{LogPrefix} phase=battery_read col02=none");
            return -1;
        }
        Logger.Log($"{LogPrefix} phase=battery_read col02={path}");
        return new MouseBatteryDevice(path, V3DisplayName(), DeviceKind.MagicMouseV3)
            .GetBatteryPercent();
    }

    static string V3DisplayName()
    {
        foreach (var entry in MouseBatteryDevice.KnownMice)
        {
            if (entry.PidPattern.EndsWith(V3Pid, StringComparison.OrdinalIgnoreCase))
                return entry.DisplayName;
        }
        return "Magic Mouse";
    }

    static int ReadWithRetries(Func<int> readPercent)
    {
        int pct = -1;
        // The HID report pipeline is not ready the instant COL02 appears:
        // measured GLE=121 then GLE=21 then success at ~1000 ms.
        for (int attempt = 1; attempt <= BatteryReadTries && pct < 0; attempt++)
        {
            if (attempt > 1)
                Thread.Sleep(BatteryReadGapMs);
            try
            {
                pct = readPercent();
            }
            catch (Exception ex)
            {
                pct = -1;
                Logger.Log($"{LogPrefix} phase=battery_read try={attempt} err={ex.Message}");
            }
            Logger.Log($"{LogPrefix} phase=battery_read try={attempt} pct={pct}");
        }
        return pct;
    }

    // --- elevation --------------------------------------------------------

    static Process? StartElevated(string scriptPath, string script)
    {
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
        return Process.Start(psi);
    }

    static string AwaitTerminal(
        Process proc, string statusPath, int ceilingMs, Action? onReady, out bool readyObserved) =>
        AwaitTerminal(proc, statusPath, ceilingMs, onReady, ceilingMs, null, out readyObserved);

    // Polls the append-only status sidecar until the script reports a terminal
    // phase. ExitCode is unavailable once UseShellExecute is true, so the
    // sidecar is the channel - same reason DeviceRepair uses one.
    //
    // The elevated process is never killed here: it may be in the middle of
    // its restore, and interrupting that is the one failure with real
    // consequences for the user.
    static string AwaitTerminal(
        Process proc,
        string statusPath,
        int ceilingMs,
        Action? onReady,
        int readyBudgetMs,
        string? donePath,
        out bool readyObserved)
    {
        readyObserved = false;
        var started = Environment.TickCount64;
        var transcript = "";
        bool readyGaveUp = false;
        while (true)
        {
            transcript = ReadAllTextOrNull(statusPath) ?? "";
            if (!readyObserved && HasToken(transcript, TokenReady))
            {
                readyObserved = true;
                Logger.Log($"{LogPrefix} phase=ready elapsed_ms={Environment.TickCount64 - started}");
                onReady?.Invoke();
            }
            if (TerminalToken(transcript).Length > 0)
                break;
            var elapsed = Environment.TickCount64 - started;
            if (!readyObserved && !readyGaveUp && elapsed > readyBudgetMs)
            {
                // Mode A never arrived. Release the script from its wait at
                // once instead of letting it sit there for its full window.
                readyGaveUp = true;
                Logger.Log($"{LogPrefix} phase=ready_timeout budget_ms={readyBudgetMs}");
                if (donePath is not null)
                    WriteDone(donePath, -1);
            }
            if (elapsed > ceilingMs)
            {
                Logger.Log($"{LogPrefix} phase=ceiling elapsed_ms={elapsed} exited={HasExited(proc)}");
                break;
            }
            if (HasExited(proc) && elapsed > 5_000)
                break;
            Thread.Sleep(PollMs);
        }
        return transcript;
    }

    static bool HasExited(Process proc)
    {
        try { return proc.HasExited; }
        catch { return false; }
    }

    static void WriteDone(string donePath, int percent)
    {
        try
        {
            File.WriteAllText(donePath, $"pct={percent}\n", Encoding.ASCII);
            Logger.Log($"{LogPrefix} phase=done_signal pct={percent}");
        }
        catch (Exception ex)
        {
            Logger.Log($"{LogPrefix} phase=done_signal err={ex.Message}");
        }
    }

    // --- small helpers ----------------------------------------------------

    static IEnumerable<string> SplitLines(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length > 0)
                yield return line;
        }
    }

    static string? ReadAllTextOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    static bool Delete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    static string[]? LiveFor(IReadOnlyList<ModeFlipTarget>? live, string keyPath) =>
        live?.FirstOrDefault(t =>
            string.Equals(t.KeyPath, keyPath, StringComparison.OrdinalIgnoreCase))?.Previous;

    static string Render(string[]? names) =>
        names is null ? "unreadable" : "[" + string.Join("|", names) + "]";

    static string Render(bool? value) =>
        value is null ? "unknown" : value == true ? "true" : "false";

    static string Render(DriverStatus? status) =>
        status is null ? "unknown" : status.Value.ToString();
}
