// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

/// <summary>
/// The stale-filter repair is the only code in the tray that WRITES HKLM, and
/// it writes LowerFilters on the device key and on every instance key before it
/// rebuilds the stack. Between the first write and a completed restart the
/// machine is in a worse state than the one the repair was invoked to fix: the
/// registry names a different filter set than the running stack. Every test
/// here is about what the tray knows, says and keeps about that window.
///
/// All seams are pure: a parsed status report, a list of (key, previous values)
/// and a mapper. Nothing here touches the registry, starts a process, or needs
/// a particular Windows build.
/// </summary>
public class DeviceRepairTests
{
    // One attempt, one nonce - it names this attempt's script, its status
    // sidecar and its backup record on both sides of the elevation boundary.
    const long Nonce = 1_757_900_000_111;
    const string Keep = "MagicMouseDriver204Scroll";
    const string Stale = "MagicMouseDriver";

    const string DeviceKey =
        @"HKLM:\SYSTEM\CurrentControlSet\Enum\BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&030d";
    const string InstanceKey =
        @"HKLM:\SYSTEM\CurrentControlSet\Enum\BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&030d\8&2bd7b9a1&0&E806884B0741_C00000000";

    static RepairProgress Progress(
        string result,
        RepairStage stage,
        int planned = 0,
        int written = 0,
        int writeFailures = 0,
        int restartFailures = 0) =>
        new(result, stage, planned, written, writeFailures, restartFailures);

    static RepairEvidence Ran(RepairProgress? progress, int? exit = null, bool timedOut = false) =>
        new(Started: true, progress, exit, timedOut);

    // The evidence Apply hands the mapper: the restart-only script, which has
    // no registry write in it at all.
    static RepairEvidence RestartOnly(RepairProgress? progress, int? exit = null, bool timedOut = false) =>
        new(Started: true, progress, exit, timedOut, HasWritePass: false);

    // --- what the outcome says about HKLM --------------------------------

    /// <summary>
    /// ShellExecute handed back no process, so the script never ran: this is
    /// the one and only case in which the tray may promise that nothing on the
    /// machine was touched.
    /// </summary>
    [Fact]
    public void NeverStarted_IsTheOnlyPromiseThatNothingWasWritten()
    {
        var result = DeviceRepair.Decide(
            new RepairEvidence(Started: false, Progress: null, Exit: null, TimedOut: false));

        Assert.Equal(RepairOutcome.Failed, result.Outcome);
        Assert.Equal(RegistryState.Untouched, result.Registry);
        Assert.False(result.Succeeded);
        Assert.Contains("UAC", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("failed/untouched", DeviceRepair.LogToken(result));
    }

    /// <summary>
    /// Elevation was granted and then the script went silent. The tray does not
    /// know how far it got, so it must not claim the machine is clean - that is
    /// a different answer from "nothing was written", and the user needs it to
    /// decide whether to look at LowerFilters before retrying.
    /// </summary>
    [Fact]
    public void StartedThenSilent_IsUnknown_NotUntouched()
    {
        var silent = DeviceRepair.Decide(Ran(null));
        var declined = DeviceRepair.Decide(
            new RepairEvidence(Started: false, Progress: null, Exit: null, TimedOut: false));

        Assert.Equal(RepairOutcome.Failed, silent.Outcome);
        Assert.Equal(RegistryState.Unknown, silent.Registry);
        Assert.NotEqual(declined.Detail, silent.Detail);
        Assert.DoesNotContain("UAC", silent.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("failed/unknown", DeviceRepair.LogToken(silent));
    }

    /// <summary>
    /// The script refused before writing (the keep filter would not have
    /// survived) or failed while still enumerating: a plain failure, and the
    /// stack is exactly as it was.
    /// </summary>
    [Fact]
    public void WroteNothingThenFailed_IsAPlainFailure()
    {
        var result = DeviceRepair.Decide(
            Ran(Progress("failed", RepairStage.Scanned, planned: 2, written: 0)));

        Assert.Equal(RepairOutcome.Failed, result.Outcome);
        Assert.Equal(RegistryState.Untouched, result.Registry);
        Assert.False(result.Succeeded);
        Assert.Equal("failed/untouched", DeviceRepair.LogToken(result));
    }

    /// <summary>
    /// One of two keys was rewritten and the pass then stopped. The registry
    /// now names one filter set and the loaded stack another - the dead-wheel
    /// state the repair exists to clear - so this can never map onto the same
    /// outcome as "failed, nothing was written".
    /// </summary>
    [Fact]
    public void WroteTheDeviceKeyThenStopped_IsNotAPlainFailure()
    {
        var stopped = DeviceRepair.Decide(
            Ran(Progress("failed", RepairStage.Writing, planned: 2, written: 1, writeFailures: 1)));
        var clean = DeviceRepair.Decide(
            Ran(Progress("failed", RepairStage.Scanned, planned: 2, written: 0)));

        Assert.Equal(RepairOutcome.StoppedAfterWrite, stopped.Outcome);
        Assert.Equal(RegistryState.Modified, stopped.Registry);
        Assert.False(stopped.Succeeded);
        Assert.NotEqual(clean.Outcome, stopped.Outcome);
        Assert.NotEqual(clean.Detail, stopped.Detail);
        Assert.Equal("stopped-after-write/modified", DeviceRepair.LogToken(stopped));
    }

    /// <summary>
    /// A write was in flight when the report stopped: no key is known to have
    /// landed, but none is known to be intact either. Unknown is its own answer
    /// and is never rounded down to "untouched".
    /// </summary>
    [Fact]
    public void WriteInFlightWhenItStopped_IsUnknown()
    {
        var result = DeviceRepair.Decide(
            Ran(Progress("failed", RepairStage.Writing, planned: 2, written: 0, writeFailures: 1)));

        Assert.Equal(RepairOutcome.StoppedAfterWrite, result.Outcome);
        Assert.Equal(RegistryState.Unknown, result.Registry);
        Assert.Equal("stopped-after-write/unknown", DeviceRepair.LogToken(result));
    }

    /// <summary>
    /// Every write landed and the restart pass then failed. The filters are
    /// correct on disk and wrong in the loaded stack, which is precisely the
    /// state a user reads as "the repair broke my mouse".
    /// </summary>
    [Fact]
    public void WroteEverythingButTheRestartFailed_LeavesAStaleStack()
    {
        var result = DeviceRepair.Decide(
            Ran(Progress("failed", RepairStage.Written, planned: 2, written: 2, restartFailures: 1)));

        Assert.Equal(RepairOutcome.StoppedAfterWrite, result.Outcome);
        Assert.Equal(RegistryState.Modified, result.Registry);
        Assert.False(result.Succeeded);
        Assert.Contains("restart", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The whole pass completed and the kept filter was observed running.
    /// </summary>
    [Fact]
    public void FullSuccess_IsOkAndStillRecordsThatHklmChanged()
    {
        var result = DeviceRepair.Decide(
            Ran(Progress("ok", RepairStage.Verified, planned: 2, written: 2), exit: 0));

        Assert.Equal(RepairOutcome.Ok, result.Outcome);
        Assert.Equal(RegistryState.Modified, result.Registry);
        Assert.True(result.Succeeded);
        Assert.Equal("ok/modified", DeviceRepair.LogToken(result));
    }

    /// <summary>
    /// The stale names were already gone: the stack is in the state the repair
    /// wanted, and nothing was written to get there.
    /// </summary>
    [Fact]
    public void NothingToDo_SucceedsWithAnUntouchedRegistry()
    {
        var result = DeviceRepair.Decide(
            Ran(Progress("nothing-to-do", RepairStage.Scanned, planned: 0, written: 0), exit: 0));

        Assert.Equal(RepairOutcome.Ok, result.Outcome);
        Assert.Equal(RegistryState.Untouched, result.Registry);
        Assert.True(result.Succeeded);
        Assert.Equal("ok/untouched", DeviceRepair.LogToken(result));
    }

    /// <summary>
    /// No live BTHENUM instance for this PID, reported either way: nothing was
    /// enumerated, so nothing was written.
    /// </summary>
    [Fact]
    public void NoInstances_IsItsOwnOutcome_FromTheReportOrTheExitCode()
    {
        var reported = DeviceRepair.Decide(
            Ran(Progress("no-instances", RepairStage.Scanned), exit: DeviceRepair.NoInstancesExitCode));
        var byExit = DeviceRepair.Decide(Ran(null, exit: DeviceRepair.NoInstancesExitCode));

        foreach (var result in new[] { reported, byExit })
        {
            Assert.Equal(RepairOutcome.NoInstances, result.Outcome);
            Assert.Equal(RegistryState.Untouched, result.Registry);
            Assert.False(result.Succeeded);
        }
    }

    /// <summary>
    /// The edit and the restart both completed, and the filter still would not
    /// load because signing policy blocks it. The user is told the policy
    /// answer AND that the registry now holds the rewritten filter list - the
    /// two facts are independent and both are true.
    /// </summary>
    [Fact]
    public void FilterBlockedAfterTheRewrite_StillReportsTheWrite()
    {
        var afterWrite = DeviceRepair.Decide(
            Ran(Progress("filter-blocked", RepairStage.Restarted, planned: 2, written: 2),
                exit: DeviceRepair.FilterBlockedExitCode));
        var byExit = DeviceRepair.Decide(Ran(null, exit: DeviceRepair.FilterBlockedExitCode));

        Assert.Equal(RepairOutcome.FilterBlocked, afterWrite.Outcome);
        Assert.Equal(RegistryState.Modified, afterWrite.Registry);
        Assert.Equal(RepairOutcome.FilterBlocked, byExit.Outcome);
        Assert.Equal(RegistryState.Unknown, byExit.Registry);
    }

    /// <summary>
    /// The restart-only repair runs a script with no Set-ItemProperty and no
    /// Remove-ItemProperty in it, so "nothing was written" is a fact about that
    /// script and holds however the attempt ended - including when no report is
    /// readable at all, which is reachable whenever the elevated shell lands
    /// under another profile or the ceiling kill beats the first status line.
    /// Warning the user to go and check LowerFilters would be a warning about a
    /// change this script has no code to make.
    /// </summary>
    [Fact]
    public void RestartOnlyRepair_CanNeverReportAWrite()
    {
        var failed = DeviceRepair.Decide(
            RestartOnly(Progress("failed", RepairStage.Restarted, planned: 0, written: 0, restartFailures: 2)));
        var silent = DeviceRepair.Decide(RestartOnly(null));
        var killed = DeviceRepair.Decide(RestartOnly(null, timedOut: true));
        var blocked = DeviceRepair.Decide(
            RestartOnly(null, exit: DeviceRepair.FilterBlockedExitCode));

        Assert.Equal(RepairOutcome.Failed, failed.Outcome);
        Assert.Equal("failed/untouched", DeviceRepair.LogToken(failed));
        Assert.Equal("failed/untouched", DeviceRepair.LogToken(silent));
        Assert.Equal("timed-out/untouched", DeviceRepair.LogToken(killed));
        Assert.Equal("filter-blocked/untouched", DeviceRepair.LogToken(blocked));
        foreach (var result in new[] { failed, silent, killed, blocked })
        {
            Assert.Equal(RegistryState.Untouched, result.Registry);
            Assert.DoesNotContain("LowerFilters", result.Detail, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Five different machine states, five different sentences. A shared
    /// "it failed" for all of them is the defect: the recovery step differs in
    /// every one.
    /// </summary>
    [Fact]
    public void EveryEndState_GetsItsOwnSentence()
    {
        var details = new[]
        {
            DeviceRepair.Decide(new RepairEvidence(false, null, null, false)),
            DeviceRepair.Decide(Ran(Progress("failed", RepairStage.Scanned, planned: 2))),
            DeviceRepair.Decide(Ran(Progress("failed", RepairStage.Writing, planned: 2, written: 1, writeFailures: 1))),
            DeviceRepair.Decide(Ran(Progress("failed", RepairStage.Written, planned: 2, written: 2, restartFailures: 1))),
            DeviceRepair.Decide(Ran(Progress("ok", RepairStage.Verified, planned: 2, written: 2))),
        }.Select(r => r.Detail).ToArray();

        Assert.All(details, d => Assert.False(string.IsNullOrWhiteSpace(d)));
        Assert.Equal(details.Length, details.Distinct(StringComparer.Ordinal).Count());
    }

    // --- the two-minute ceiling ------------------------------------------

    /// <summary>
    /// The poller kills the elevated tree at the ceiling. That is an outcome of
    /// its own: the script was cut off mid-pass, which is not the same event as
    /// a script that ran to the end and reported a failure.
    /// </summary>
    [Fact]
    public void CeilingKill_IsItsOwnOutcome()
    {
        var killed = DeviceRepair.Decide(Ran(Progress("running", RepairStage.Running), timedOut: true));
        var failed = DeviceRepair.Decide(Ran(Progress("failed", RepairStage.Scanned, planned: 2)));

        Assert.Equal(RepairOutcome.TimedOut, killed.Outcome);
        Assert.Equal(RegistryState.Untouched, killed.Registry);
        Assert.False(killed.Succeeded);
        Assert.NotEqual(failed.Outcome, killed.Outcome);
        Assert.NotEqual(failed.Detail, killed.Detail);
        Assert.Equal("timed-out/untouched", DeviceRepair.LogToken(killed));
    }

    /// <summary>
    /// Killed after the first key was rewritten: both facts have to reach the
    /// user, because the tray stopped a process that was halfway through
    /// editing HKLM and never rebuilt the stack.
    /// </summary>
    [Fact]
    public void CeilingKillAfterAWrite_ReportsBothFacts()
    {
        var result = DeviceRepair.Decide(
            Ran(Progress("running", RepairStage.Writing, planned: 2, written: 1), timedOut: true));

        Assert.Equal(RepairOutcome.TimedOut, result.Outcome);
        Assert.Equal(RegistryState.Modified, result.Registry);
        Assert.Equal("timed-out/modified", DeviceRepair.LogToken(result));
        Assert.Contains("LowerFilters", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A completed report wins over the ceiling flag only when the script
    /// reached a terminal state: a kill that races a final 'ok' must not be
    /// reported as a timeout.
    /// </summary>
    [Fact]
    public void CeilingKillAfterTheFinalReport_KeepsTheReportedEndState()
    {
        var result = DeviceRepair.Decide(
            Ran(Progress("ok", RepairStage.Verified, planned: 2, written: 2), timedOut: true));

        Assert.Equal(RepairOutcome.Ok, result.Outcome);
        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// The ceiling is a named value, not a literal buried in the poll loop, so
    /// the outcome above can be tested and explained in the same terms the
    /// poller uses.
    /// </summary>
    [Fact]
    public void ElevatedCeiling_IsTheWindowThePollerKillsAt()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), DeviceRepair.ElevatedCeiling);
    }

    // --- a half-edited stack is still restarted ---------------------------

    /// <summary>
    /// The stack restart is what makes PnP act on the filter list. Once ANY
    /// key has been rewritten the loaded stack no longer matches the registry,
    /// so the restart is required whether or not every write landed. Skipping
    /// it on a write failure is what leaves the machine dead-wheeled. That
    /// condition lives in the generated script and nowhere else, so this reads
    /// the script: a C# copy of the rule would keep passing while the script's
    /// own guard changed underneath it.
    /// </summary>
    [Fact]
    public void FiltersScript_RestartsOnEveryLandedWrite()
    {
        var script = DeviceRepair.BuildRemoveStaleFiltersScript("030d", Nonce, Keep, [Stale]);

        var set = script.IndexOf("Set-ItemProperty", StringComparison.Ordinal);
        var remove = script.IndexOf("Remove-ItemProperty", StringComparison.Ordinal);
        var firstWrite = Math.Min(set, remove);
        var guard = script.IndexOf("if ($written -gt 0) {", StringComparison.Ordinal);
        var restart = script.IndexOf("/restart-device", StringComparison.Ordinal);

        // The only thing standing between the write pass and the restart loop
        // is the count of writes that landed.
        Assert.InRange(guard, firstWrite, restart);
        Assert.DoesNotContain("writeFailures", script[guard..restart], StringComparison.Ordinal);
    }

    /// <summary>
    /// Same rule inside the generated script: the write pass may not exit
    /// between the first LowerFilters write and the restart loop.
    /// </summary>
    [Fact]
    public void FiltersScript_DoesNotExitBetweenTheWriteAndTheRestart()
    {
        var script = DeviceRepair.BuildRemoveStaleFiltersScript("030d", Nonce, Keep, [Stale]);

        var write = script.IndexOf("Set-ItemProperty", StringComparison.Ordinal);
        var restart = script.IndexOf("/restart-device", StringComparison.Ordinal);
        Assert.InRange(write, 0, restart);
        Assert.DoesNotContain("exit 1", script[write..restart], StringComparison.Ordinal);
    }

    // --- previous values outlive the console window -----------------------

    /// <summary>
    /// The previous values are only readable while the elevated console is on
    /// screen unless they cross the elevation boundary, so the script reports
    /// every key and every previous value through the status sidecar - the one
    /// channel the tray can read - and does it BEFORE the first write.
    /// </summary>
    [Fact]
    public void FiltersScript_ReportsPreviousValuesBeforeItWritesAnything()
    {
        var script = DeviceRepair.BuildRemoveStaleFiltersScript("030d", Nonce, Keep, [Stale]);

        var set = script.IndexOf("Set-ItemProperty", StringComparison.Ordinal);
        var remove = script.IndexOf("Remove-ItemProperty", StringComparison.Ordinal);
        var firstWrite = Math.Min(set, remove);
        Assert.InRange(firstWrite, 1, int.MaxValue);

        // Both record line kinds reach the sidecar, not just Write-Host: the
        // console window is gone before anyone can read them back.
        var key = script.IndexOf("key=", StringComparison.Ordinal);
        var prev = script.IndexOf("prev=", StringComparison.Ordinal);
        Assert.InRange(key, 0, firstWrite);
        Assert.InRange(prev, 0, firstWrite);
        Assert.Contains("Add-Content -LiteralPath $statusFile", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stages the mapper reads are written by the script around the write
    /// pass, so a report that stops mid-pass still says which side of the first
    /// write it stopped on.
    /// </summary>
    [Fact]
    public void FiltersScript_MarksTheStageAroundTheWritePass()
    {
        var script = DeviceRepair.BuildRemoveStaleFiltersScript("030d", Nonce, Keep, [Stale]);

        var set = script.IndexOf("Set-ItemProperty", StringComparison.Ordinal);
        var remove = script.IndexOf("Remove-ItemProperty", StringComparison.Ordinal);
        var firstWrite = Math.Min(set, remove);

        Assert.Contains("stage=running", script, StringComparison.Ordinal);
        Assert.Contains("stage=scanned", script, StringComparison.Ordinal);
        Assert.InRange(script.IndexOf("stage=writing", StringComparison.Ordinal), 0, firstWrite);
        Assert.InRange(script.IndexOf("stage=written", StringComparison.Ordinal), firstWrite, int.MaxValue);
        Assert.Contains("written=", script, StringComparison.Ordinal);
        Assert.Contains("write-failures=", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sidecar is read last-line-wins while the script is still appending
    /// to it, and the header seeds written=0. If the stage marker were appended
    /// first, a read landing between the two appends - or one that lost the
    /// count line to a torn read - would pair 'stage=written' with that seeded
    /// zero and the tray would tell the user nothing was changed after the
    /// writes had landed. So each count is appended BEFORE the marker that
    /// makes it the answer.
    /// </summary>
    [Fact]
    public void FiltersScript_AppendsTheCountsBeforeTheStageMarker()
    {
        var script = DeviceRepair.BuildRemoveStaleFiltersScript("030d", Nonce, Keep, [Stale]);

        Assert.InRange(
            script.IndexOf("Write-Status (\"written=\" + $written)", StringComparison.Ordinal),
            0,
            script.IndexOf("Write-Status 'stage=written'", StringComparison.Ordinal));
        Assert.InRange(
            script.IndexOf("Write-Status (\"write-failures=\" + $writeFailures)", StringComparison.Ordinal),
            0,
            script.IndexOf("Write-Status 'stage=written'", StringComparison.Ordinal));
        Assert.InRange(
            script.IndexOf("Write-Status (\"restart-failures=\" + $restartFailures)", StringComparison.Ordinal),
            0,
            script.IndexOf("Write-Status 'stage=restarted'", StringComparison.Ordinal));
    }

    /// <summary>
    /// The restart-only repair reports progress in the same vocabulary, and its
    /// report pins the counts at zero: it has no write pass to describe.
    /// </summary>
    [Fact]
    public void RestartOnlyScript_ReportsZeroPlannedWrites()
    {
        var script = DeviceRepair.BuildScript("030d", Nonce, Keep);

        Assert.Contains("stage=running", script, StringComparison.Ordinal);
        Assert.Contains("planned=0", script, StringComparison.Ordinal);
        Assert.Contains("written=0", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-ItemProperty", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tray reads every touched key and its exact previous value list out
    /// of the sidecar text, in the order the script recorded them. Device key
    /// and instance key are separate entries even though one path is a prefix
    /// of the other.
    /// </summary>
    [Fact]
    public void ParseFilterBackup_ReadsEveryKeyTheScriptTouched()
    {
        var text = string.Join("\n",
        [
            "stage=writing",
            "result=running",
            "planned=2",
            $"key=0|{DeviceKey}",
            $"prev=0|{Stale}",
            $"key=1|{InstanceKey}",
            $"prev=1|{Keep}",
            $"prev=1|{Stale}",
            "prev=1|mouhid",
        ]);

        var entries = DeviceRepair.ParseFilterBackup(text);

        Assert.Equal(2, entries.Count);
        Assert.Equal(DeviceKey, entries[0].KeyPath);
        Assert.Equal([Stale], entries[0].PreviousValues);
        Assert.Equal(InstanceKey, entries[1].KeyPath);
        Assert.Equal([Keep, Stale, "mouhid"], entries[1].PreviousValues);
    }

    /// <summary>
    /// A key recorded with no previous value, a value line with no key, and a
    /// report that never reached the registry all yield nothing to restore -
    /// never a half entry that a hand restore would act on.
    /// </summary>
    [Fact]
    public void ParseFilterBackup_IncompleteRecords_YieldNothingToRestore()
    {
        Assert.Empty(DeviceRepair.ParseFilterBackup("stage=running\nresult=running"));
        Assert.Empty(DeviceRepair.ParseFilterBackup(""));
        Assert.Empty(DeviceRepair.ParseFilterBackup($"prev=0|{Stale}"));
        Assert.Empty(DeviceRepair.ParseFilterBackup($"key=0|{DeviceKey}"));
    }

    /// <summary>
    /// The record the tray keeps is the record the tray can read back: every
    /// key, every previous value, in order, after the elevated process is gone.
    /// </summary>
    [Fact]
    public void FilterBackup_RoundTripsThroughTheRecordedForm()
    {
        FilterBackupEntry[] entries =
        [
            new(DeviceKey, [Stale]),
            new(InstanceKey, [Keep, Stale, "mouhid"]),
        ];

        var lines = DeviceRepair.FormatFilterBackup("030d", Nonce, entries);
        var parsed = DeviceRepair.ParseFilterBackup(string.Join(Environment.NewLine, lines));

        Assert.Equal(entries.Length, parsed.Count);
        for (var i = 0; i < entries.Length; i++)
        {
            Assert.Equal(entries[i].KeyPath, parsed[i].KeyPath);
            Assert.Equal(entries[i].PreviousValues, parsed[i].PreviousValues);
        }

        // The attempt the record belongs to, because two repairs for one PID
        // can be in flight at once.
        Assert.Contains(lines, l => l.Contains("030d", StringComparison.Ordinal)
            && l.Contains(Nonce.ToString(), StringComparison.Ordinal));
        // Nothing to restore, nothing recorded.
        Assert.Empty(DeviceRepair.FormatFilterBackup("030d", Nonce, []));
    }

    /// <summary>
    /// A LowerFilters entry is whatever the registry held, not a name this code
    /// vetted, so a value carrying the record's own separators has to come back
    /// character for character - a restore is pasted back verbatim.
    /// </summary>
    [Fact]
    public void FilterBackup_ValuesCarryingSeparators_ComeBackVerbatim()
    {
        const string awkward = "weird|name=with spaces";
        FilterBackupEntry[] entries = [new(DeviceKey + @"\odd|key", [awkward, Keep])];

        var parsed = DeviceRepair.ParseFilterBackup(
            string.Join("\n", DeviceRepair.FormatFilterBackup("030d", Nonce, entries)));

        Assert.Single(parsed);
        Assert.Equal(DeviceKey + @"\odd|key", parsed[0].KeyPath);
        Assert.Equal([awkward, Keep], parsed[0].PreviousValues);
    }

    /// <summary>
    /// The record lives in the folder the tray already owns and keeps - next to
    /// debug.log, never as debug.log, which rotates at 1 MB and is redacted for
    /// sharing - and it is named per attempt so two overlapping repairs for the
    /// same PID cannot overwrite each other's only copy of the old values.
    /// </summary>
    [Fact]
    public void FilterBackupPath_IsPerAttempt_AndOutlivesTheAttempt()
    {
        var path = DeviceRepair.FilterBackupPath("030d", Nonce);

        Assert.Equal(Logger.LogDir, Path.GetDirectoryName(path));
        Assert.NotEqual(Logger.LogPath, path);
        var name = Path.GetFileName(path);
        Assert.Contains("030d", name, StringComparison.Ordinal);
        Assert.Contains(Nonce.ToString(), name, StringComparison.Ordinal);
        Assert.NotEqual(path, DeviceRepair.FilterBackupPath("030d", Nonce + 1));
        Assert.NotEqual(path, DeviceRepair.FilterBackupPath("0323", Nonce));
    }

    /// <summary>
    /// The script reports key=/prev= for every scanned target before it knows
    /// whether it will change any of them, so those lines are present on runs
    /// that wrote nothing too. A record headed "to undo by hand" for an attempt
    /// with nothing to undo is a false alarm sitting in the folder "Open logs"
    /// opens, so the record is kept only when a write may have landed.
    /// </summary>
    [Fact]
    public void UndoRecord_IsKeptOnlyWhenAWriteMayHaveLanded()
    {
        var wrote = DeviceRepair.Decide(
            Ran(Progress("failed", RepairStage.Writing, planned: 2, written: 1, writeFailures: 1)));
        var maybeWrote = DeviceRepair.Decide(
            Ran(Progress("running", RepairStage.Writing, planned: 2), timedOut: true));
        var silent = DeviceRepair.Decide(Ran(null));
        var nothingToDo = DeviceRepair.Decide(Ran(Progress("nothing-to-do", RepairStage.Scanned)));
        var refused = DeviceRepair.Decide(Ran(Progress("failed", RepairStage.Scanned, planned: 2)));

        Assert.True(DeviceRepair.ShouldRecordFilterBackup(wrote));
        Assert.True(DeviceRepair.ShouldRecordFilterBackup(maybeWrote));
        Assert.True(DeviceRepair.ShouldRecordFilterBackup(silent));
        Assert.False(DeviceRepair.ShouldRecordFilterBackup(nothingToDo));
        Assert.False(DeviceRepair.ShouldRecordFilterBackup(refused));
    }

    // --- the parsed report ------------------------------------------------

    /// <summary>
    /// Every stage token the script writes maps to the stage the mapper reads.
    /// </summary>
    [Fact]
    public void ParseProgress_ReadsEveryStageTheScriptWrites()
    {
        var expected = new (string Token, RepairStage Stage)[]
        {
            ("running", RepairStage.Running),
            ("scanned", RepairStage.Scanned),
            ("writing", RepairStage.Writing),
            ("written", RepairStage.Written),
            ("restarted", RepairStage.Restarted),
            ("verified", RepairStage.Verified),
        };

        foreach (var (token, stage) in expected)
        {
            var progress = DeviceRepair.ParseProgress(
                $"stage={token}\r\nresult=running\r\nplanned=2\r\nwritten=1\r\nwrite-failures=0\r\nrestart-failures=0\r\n");
            Assert.NotNull(progress);
            Assert.Equal(stage, progress!.Value.Stage);
        }
    }

    /// <summary>
    /// The counts the mapper decides on are read off the report exactly.
    /// </summary>
    [Fact]
    public void ParseProgress_ReadsTheCountsTheMapperDecidesOn()
    {
        var progress = DeviceRepair.ParseProgress(
            "stage=written\nresult=failed\nplanned=3\nwritten=2\nwrite-failures=1\nrestart-failures=4\n");

        Assert.NotNull(progress);
        Assert.Equal("failed", progress!.Value.Result);
        Assert.Equal(RepairStage.Written, progress.Value.Stage);
        Assert.Equal(3, progress.Value.KeysPlanned);
        Assert.Equal(2, progress.Value.KeysWritten);
        Assert.Equal(1, progress.Value.WriteFailures);
        Assert.Equal(4, progress.Value.RestartFailures);
    }

    /// <summary>
    /// A report with no stage, an unknown stage, or a negative count is not a
    /// state: guessing one would let the tray claim the registry is clean on
    /// the strength of a truncated file.
    /// </summary>
    [Fact]
    public void ParseProgress_UnreadableReport_IsNoProgress()
    {
        Assert.Null(DeviceRepair.ParseProgress(""));
        Assert.Null(DeviceRepair.ParseProgress("running"));
        Assert.Null(DeviceRepair.ParseProgress("ok"));
        Assert.Null(DeviceRepair.ParseProgress("result=failed\nplanned=2\nwritten=0"));
        Assert.Null(DeviceRepair.ParseProgress("stage=halfway\nresult=failed\nplanned=2\nwritten=0"));
        Assert.Null(DeviceRepair.ParseProgress("stage=writing\nresult=failed\nplanned=2\nwritten=-1"));
    }

    /// <summary>
    /// The sidecar names the attempt on both sides of the elevation boundary,
    /// and the status report the new mapper reads is written to that same file.
    /// </summary>
    [Fact]
    public void FiltersScript_WritesTheProgressReportToTheAttemptSidecar()
    {
        var script = DeviceRepair.BuildRemoveStaleFiltersScript("030d", Nonce, Keep, [Stale]);

        Assert.Contains(
            "$statusFile = Join-Path $env:TEMP ('mm-repair-filters-' + $targetPid + '-' + $procId + '-' + $nonce + '.status')",
            script,
            StringComparison.Ordinal);
        Assert.Equal(
            $"mm-repair-filters-030d-{Environment.ProcessId}-{Nonce}.status",
            Path.GetFileName(DeviceRepair.FiltersStatusSidecarPath("030d", Nonce)));
        Assert.Contains("stage=verified", script, StringComparison.Ordinal);
        Assert.Contains("result=ok", script, StringComparison.Ordinal);
    }
}
