// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// The decision logic of the Mode A / Mode B flip, not PowerShell. Nothing here
// touches the registry, a HID handle or a process: the script is asserted as
// generated text, and every outcome is driven from the evidence record the
// live run fills in.
public class ModeFlipTests
{
    const string DeviceKey =
        @"HKLM:\SYSTEM\CurrentControlSet\Enum\BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0323";

    const string InstanceKey = DeviceKey + @"\9&73b8b28&0&D0C050CC8C4D_C00000000";

    static ModeFlipTarget Carrier(string key = DeviceKey) =>
        new(key, true, ["applewirelessmouse", "mouhid"]);

    static ModeFlipTarget NoValue(string key = InstanceKey) =>
        new(key, false, []);

    // One cycle, one nonce. It names the handshake files on both sides of the
    // elevation boundary.
    const long Nonce = 1_757_900_000_000;

    // The filter name the flip removed. Together with the PID it is the ONLY
    // thing that crosses out of the sentinel into generated script text.
    const string FilterName = "applewirelessmouse";

    static ModeFlipSentinel Sentinel(params ModeFlipTarget[] targets) =>
        new(ModeFlip.SentinelVersion, Nonce, ModeFlip.V3Pid, targets);

    static string FlipScript() => ModeFlip.BuildFlipScript(ModeFlip.V3Pid, Nonce);

    static string RestoreScript(string filterName = FilterName) =>
        ModeFlip.BuildRestoreScript(ModeFlip.V3Pid, Nonce, filterName);

    static ModeFlip.FlipEvidence Evidence(
        bool started = true,
        bool ready = true,
        string terminal = ModeFlip.TokenRestored,
        int percent = 57,
        bool? verified = true) => new(started, ready, terminal, percent, verified);

    // --- generated flip script -------------------------------------------

    [Fact]
    public void FlipScript_RemovesTheAppleFilterAndNothingElse()
    {
        var script = FlipScript();

        // The one value the flip is allowed to drop.
        Assert.Contains("$filter = 'applewirelessmouse'", script, StringComparison.Ordinal);
        // Every other name on LowerFilters is copied through, so the removal is
        // a filtered rebuild of the value and never a blind delete.
        Assert.Contains(
            "if ($n -and $n.ToLowerInvariant().StartsWith($filter.ToLowerInvariant())) { continue }",
            script, StringComparison.Ordinal);
        Assert.Contains("Show-Names $kept", script, StringComparison.Ordinal);
        // Previous value recorded verbatim before any write.
        Assert.Contains("Write-Phase ('prev key=' + $t.Path + ' LowerFilters=' + (Show-Names $t.Previous))",
            script, StringComparison.Ordinal);
    }

    [Fact]
    public void FlipScript_TargetsLiveBthenumInstancesOnly()
    {
        var script = FlipScript();

        Assert.Contains(@"$enumPath = 'SYSTEM\CurrentControlSet\Enum\BTHENUM'", script, StringComparison.Ordinal);
        Assert.Contains("& pnputil.exe /restart-device \"$id\"", script, StringComparison.Ordinal);
        // USB and HID\VID_ nodes are charge-cable phantoms, never the live
        // stack, and no action may be aimed at one.
        Assert.Contains(@"if ($low.StartsWith('usb\')) { return $true }", script, StringComparison.Ordinal);
        Assert.Contains(@"if ($low.StartsWith('hid\vid_')) { return $true }", script, StringComparison.Ordinal);
        Assert.Contains("if (Test-SkipPath $full) { continue }", script, StringComparison.Ordinal);
        // No other Enum hive is walked for targets.
        Assert.DoesNotContain(@"Enum\USB", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"Enum\HID", script, StringComparison.OrdinalIgnoreCase);
        // Refuses when there is no live instance rather than guessing one.
        Assert.Contains("Write-Phase 'no-instances'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void FlipScript_RestoresRecordedPreviousValueInsideFinally()
    {
        var script = FlipScript();

        var tryAt = script.IndexOf("\ntry {", StringComparison.Ordinal);
        var finallyAt = script.IndexOf("\nfinally {", StringComparison.Ordinal);
        Assert.True(tryAt > 0, "flip script has no try block");
        Assert.True(finallyAt > tryAt, "flip script has no finally block after its try");

        var body = script[tryAt..finallyAt];
        var restore = script[finallyAt..];

        // The removal is in the try; the restore of the recorded previous
        // value and the second device restart are in the finally, so a failed
        // flip, a tray that never answers, or a terminating error still ends
        // in Mode B.
        Assert.Contains("flip write key=", body, StringComparison.Ordinal);
        Assert.Contains("Restart-Stack $ids 'flip'", body, StringComparison.Ordinal);
        // The try never writes the previous value back - that is the finally's
        // only job, so there is exactly one restore and it cannot be skipped.
        Assert.DoesNotContain("Write-LowerFilters $t.Path $t.Previous", body, StringComparison.Ordinal);

        Assert.Contains("[void](Write-LowerFilters $t.Path $t.Previous)", restore, StringComparison.Ordinal);
        Assert.Contains("Restart-Stack $ids 'restore'", restore, StringComparison.Ordinal);
        // And the end state is re-read, never assumed from an exit code.
        Assert.Contains("Read-LowerFiltersAt $t.Path", restore, StringComparison.Ordinal);
        Assert.Contains("Wait-Mode { Test-ModeB }", restore, StringComparison.Ordinal);
        Assert.Contains("Write-Phase 'restored'", restore, StringComparison.Ordinal);
        Assert.Contains("Write-Phase 'restore-failed'", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void FlipScript_HoldsTheModeAWindowOpenForTheTray()
    {
        var script = FlipScript();

        // One elevation for the whole cycle: the script signals Mode A and
        // then waits for the tray's done file instead of exiting and being
        // relaunched (which would be a second UAC prompt).
        Assert.Contains("Write-Phase 'ready'", script, StringComparison.Ordinal);
        Assert.Contains("Wait-Mode { Test-Path -LiteralPath $doneFile }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedScripts_CarryNothingFromTheDeadDevQueueProtocol()
    {
        var flip = FlipScript();
        var restore = RestoreScript();

        foreach (var script in new[] { flip, restore })
        {
            Assert.DoesNotContain("mm-dev-queue", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("schtasks", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MM-Dev-Cycle", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("request.txt", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("result.txt", script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RestoreScript_DerivesItsOwnTargetsInsideTheElevatedContext()
    {
        var script = RestoreScript();

        // The walk, its gates and the classification all happen in the
        // elevated context. Nothing outside decides what gets written.
        Assert.Contains("$stack = Get-BthenumNode", script, StringComparison.Ordinal);
        Assert.Contains("function Get-BthenumNode {", script, StringComparison.Ordinal);
        Assert.Contains(@"$enumPath = 'SYSTEM\CurrentControlSet\Enum\BTHENUM'", script, StringComparison.Ordinal);
        // The same PID + VID + phantom gate the flip script uses...
        Assert.Contains("if (-not ($low.Contains($needleA) -or $low.Contains($needleB))) { continue }",
            script, StringComparison.Ordinal);
        Assert.Contains("if (-not (Test-Vid $sub)) { continue }", script, StringComparison.Ordinal);
        Assert.Contains("if (Test-SkipPath $full) { continue }", script, StringComparison.Ordinal);
        // ...plus the two gates that keep an elevated write off a key with no
        // stack: the {00001200-...} SDP sibling carries this PID and VID but
        // has no Service and no LowerFilters.
        Assert.Contains("if (-not $low.StartsWith($hidUuidPrefix.ToLowerInvariant())) { continue }",
            script, StringComparison.Ordinal);
        Assert.Contains("if (-not [string]::IsNullOrEmpty($svc))", script, StringComparison.Ordinal);
        // And there is no recorded-target table left to aim it with.
        Assert.DoesNotContain("$recorded", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreScript_TakesNoRegistryKeyFromATamperedSentinel()
    {
        // The sentinel is a file in %APPDATA%, so any unprivileged process on
        // this desktop can rewrite it. This one names a key on the far side of
        // the registry and keeps a real-looking one for company.
        const string foreign = @"HKLM:\SYSTEM\CurrentControlSet\Services\WinDefend";
        var tampered = Sentinel(
            new ModeFlipTarget(foreign, true, [FilterName]),
            Carrier());

        // Exactly one scalar crosses the boundary...
        var filterName = ModeFlip.RestoreFilterName(tampered);
        Assert.Equal(FilterName, filterName);
        var script = ModeFlip.BuildRestoreScript(tampered.Pid, Nonce, filterName!);

        // ...and no key path does - not the forged one, and not even the real
        // one, because the script has no business being told either.
        Assert.DoesNotContain(foreign, script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WinDefend", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DeviceKey, script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(InstanceKey, script, StringComparison.OrdinalIgnoreCase);
        // Which is exactly the build-time gate: both scripts assemble every key
        // path at runtime from their own walk, so a literal enum key path in
        // generated text could only have come from outside.
        Assert.DoesNotContain(@"Enum\BTHENUM\", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$stack = Get-BthenumNode", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreScript_RefusesAFilterNameItCannotSafelyReElevate()
    {
        // Not a plain service name: quoting it and hoping is how an elevated
        // shell ends up running somebody else's text.
        Assert.Throws<InvalidOperationException>(() => RestoreScript("apple'; rm -rf"));
        Assert.Throws<InvalidOperationException>(() => RestoreScript(""));
        // Plain, but not the Apple filter family. The restore has exactly one
        // job and putting an unrelated service on LowerFilters is not it.
        Assert.Throws<InvalidOperationException>(() => RestoreScript("mouhid"));
        Assert.Throws<InvalidOperationException>(() => RestoreScript("MagicMouseDriver204Scroll"));
        // A family variant is carried through VERBATIM, because that is the
        // name that was actually removed.
        Assert.Contains("$restoreName = 'AppleWirelessMouse204'",
            RestoreScript("AppleWirelessMouse204"), StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreFilterName_RejectsASentinelThatDescribesNoScrollMode()
    {
        // Nothing Apple-family was ever recorded, so there is no Mode B to go
        // back to - and inventing a name to write would be inventing a driver
        // binding the tray never measured.
        Assert.Null(ModeFlip.RestoreFilterName(Sentinel(
            new ModeFlipTarget(DeviceKey, true, ["mouhid", "HidBth"]))));
        Assert.Null(ModeFlip.RestoreFilterName(Sentinel(NoValue())));
        // The recorded spelling wins over the catalog's, so the name that goes
        // back is the name that came off.
        Assert.Equal("AppleWirelessMouse204", ModeFlip.RestoreFilterName(Sentinel(
            new ModeFlipTarget(DeviceKey, true, ["mouhid", "AppleWirelessMouse204"]))));
    }

    [Fact]
    public void RestoreScript_RebuildsTheValueFromTheLiveKeyAndKeepsNonFamilyNames()
    {
        var script = RestoreScript();

        // The family name goes back at the head and every other name on the
        // LIVE value is copied through in place, so mouhid and HidBth are
        // never dropped and never reordered.
        Assert.Contains("[void]$want.Add($restoreName)", script, StringComparison.Ordinal);
        Assert.Contains("foreach ($m in @($n.Names)) {", script, StringComparison.Ordinal);
        Assert.Contains("[void]$want.Add($s)", script, StringComparison.Ordinal);
        // ABSENT is the flip's Clear-LowerFilters signature and gets a write;
        // PRESENT-but-empty is a key the flip never touched and is left alone.
        // Collapsing those two is how a stack gains a filter it never had.
        Assert.Contains("if (($null -ne $n.Names) -and (@($n.Names).Count -eq 0)) {",
            script, StringComparison.Ordinal);
        Assert.Contains("reason=present-empty", script, StringComparison.Ordinal);
        // A key already carrying the family name is not written again - but it
        // is still verified at the end.
        Assert.Contains("reason=already-scroll-mode", script, StringComparison.Ordinal);
        // The end state is re-read per key before any verdict, never inferred
        // from an exit code.
        Assert.Contains("$now = Read-LowerFiltersAt $n.Path", script, StringComparison.Ordinal);
        Assert.Contains("if (-not (Test-FamilyName $now)) { $allMatch = $false }",
            script, StringComparison.Ordinal);
        Assert.Contains("Wait-Mode { Test-ModeB }", script, StringComparison.Ordinal);
        Assert.Contains("Write-Phase 'restored'", script, StringComparison.Ordinal);
        Assert.Contains("Write-Phase 'restore-failed'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreScript_WritesBeforeItEverLooksAtLiveInstances()
    {
        var script = RestoreScript();

        var writeAt = script.IndexOf("[void](Write-LowerFilters $p.Path $p.Want)", StringComparison.Ordinal);
        var restartGateAt = script.IndexOf("if ($ids.Count -eq 0) {", StringComparison.Ordinal);
        Assert.True(writeAt > 0, "restore script never writes LowerFilters");
        Assert.True(restartGateAt > writeAt,
            "the restore must write the recorded value before it decides whether a restart is possible");
        // Losing the live instances skips the RESTART, never the write. This is
        // the only one-click way out of a dead scroll wheel, so a devnode that
        // went missing must not be able to turn it into a no-op.
        Assert.Contains("Write-Phase 'restart-skipped instances=0'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Phase 'no-instances'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreScript_ReportsNoTargetsRatherThanWritingBlind()
    {
        var script = RestoreScript();

        var guardAt = script.IndexOf("Write-Phase 'no-targets'", StringComparison.Ordinal);
        var writeAt = script.IndexOf("[void](Write-LowerFilters $p.Path $p.Want)", StringComparison.Ordinal);
        Assert.True(guardAt > 0, "restore script has no empty-derivation guard");
        Assert.True(guardAt < writeAt, "the empty-derivation guard must sit before any write");
        Assert.Contains("if ($expect.Count -eq 0) {", script, StringComparison.Ordinal);
        // And the tray has to be able to act on it.
        Assert.Equal(ModeFlip.TokenNoTargets,
            ModeFlip.TerminalToken("restore-only pid=0323\nderived nodes=0 instances=0\nno-targets\n"));
    }

    [Fact]
    public void NoDerivedTarget_IsAFailureThatNamesWhatToPutBackByHand()
    {
        var detail = ModeFlip.HandRecoveryDetail(Sentinel(NoValue(), Carrier()));

        // Never a silent success.
        Assert.Contains("NOT be restored", detail, StringComparison.Ordinal);
        // The forensic half of the sentinel is what makes the message
        // actionable: the key, and the verbatim value to put back on it.
        Assert.Contains(DeviceKey, detail, StringComparison.Ordinal);
        Assert.Contains("applewirelessmouse mouhid", detail, StringComparison.Ordinal);
        // A record naming no family filter says so instead of naming a key.
        Assert.Contains("no scroll-mode filter",
            ModeFlip.HandRecoveryDetail(Sentinel(NoValue())), StringComparison.Ordinal);
    }

    [Fact]
    public void HandshakeFiles_AreNamedAfterTheCycleNotJustTheDevice()
    {
        var mine = ModeFlip.StatusSidecarPath(ModeFlip.V3Pid, Nonce);

        // A transcript left behind by a previous or overlapping cycle is not
        // the file this cycle polls, so it can never supply this cycle's
        // "ready" - which would read a battery in Mode B - or its "restored",
        // which would clear the sentinel while a live script holds Mode A.
        Assert.NotEqual(mine, ModeFlip.StatusSidecarPath(ModeFlip.V3Pid, Nonce + 1));
        Assert.Contains($"{Nonce}", mine, StringComparison.Ordinal);
        Assert.NotEqual(
            ModeFlip.DoneFilePath(ModeFlip.V3Pid, Nonce),
            ModeFlip.DoneFilePath(ModeFlip.V3Pid, Nonce + 1));
        Assert.NotEqual(
            ModeFlip.ScriptPath(ModeFlip.V3Pid, Nonce, restore: false),
            ModeFlip.ScriptPath(ModeFlip.V3Pid, Nonce, restore: true));

        // Both sides of the elevation boundary derive the name the same way, so
        // the script writes the file the tray is watching.
        Assert.Contains($"$nonce = '{Nonce}'", FlipScript(), StringComparison.Ordinal);
        Assert.Contains(
            "$statusFile = Join-Path $env:TEMP ('mm-modeflip-' + $targetPid + '-' + $nonce + '.status')",
            FlipScript(), StringComparison.Ordinal);
        Assert.Contains(
            "$statusFile = Join-Path $env:TEMP ('mm-modeflip-restore-' + $targetPid + '-' + $nonce + '.status')",
            RestoreScript(), StringComparison.Ordinal);
    }

    // --- outcome mapping --------------------------------------------------

    [Fact]
    public void Outcome_Ok_OnlyWhenReadingAndRestoreBothProven()
    {
        Assert.Equal(ModeFlipOutcome.Ok, ModeFlip.MapOutcome(Evidence()));
    }

    [Fact]
    public void Outcome_Cancelled_WhenNoElevatedProcessEverStarted()
    {
        // UAC refused: nothing ran, so nothing can be wrong with the device.
        Assert.Equal(ModeFlipOutcome.Cancelled,
            ModeFlip.MapOutcome(Evidence(started: false, ready: false, terminal: "", percent: -1, verified: null)));
    }

    [Fact]
    public void Outcome_RefusalsComeFromTheScriptsOwnTokens()
    {
        Assert.Equal(ModeFlipOutcome.NoInstances,
            ModeFlip.MapOutcome(Evidence(ready: false, terminal: ModeFlip.TokenNoInstances, percent: -1, verified: null)));
        // The Apple filter was not registered, so the script never flipped and
        // a Mode B it never left cannot be "restored".
        Assert.Equal(ModeFlipOutcome.NotPathA,
            ModeFlip.MapOutcome(Evidence(ready: false, terminal: ModeFlip.TokenNoFilter, percent: -1, verified: null)));
    }

    [Fact]
    public void Outcome_BatteryUnreadable_IsNotARestoreFailure()
    {
        // Mode A reached, the mouse said nothing, Mode B confirmed: the user
        // has no percent and a working scroll wheel.
        Assert.Equal(ModeFlipOutcome.BatteryUnreadable,
            ModeFlip.MapOutcome(Evidence(percent: -1)));
    }

    [Fact]
    public void Outcome_FlipFailed_WhenModeANeverArrived()
    {
        Assert.Equal(ModeFlipOutcome.FlipFailed,
            ModeFlip.MapOutcome(Evidence(ready: false, percent: -1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Outcome_UnverifiablePostStateNeverReportsSuccess(bool? verified)
    {
        // A perfect reading does not buy a pass: an unproven Mode B outranks
        // every other result because the cost is a dead scroll wheel.
        Assert.Equal(ModeFlipOutcome.RestoreFailed,
            ModeFlip.MapOutcome(Evidence(verified: verified)));
        Assert.Equal(ModeFlipOutcome.RestoreFailed,
            ModeFlip.MapOutcome(Evidence(ready: false, percent: -1, verified: verified)));
        Assert.Equal(ModeFlipOutcome.RestoreFailed,
            ModeFlip.MapOutcome(Evidence(terminal: ModeFlip.TokenRestoreFailed, verified: verified)));
    }

    [Fact]
    public void Outcome_ScriptSayingRestoreFailedLosesToALaterVerifiedReRead()
    {
        // The script's Mode B budget can expire while the stack is still
        // settling; the tray re-reads afterwards and that read is later.
        Assert.Equal(ModeFlipOutcome.Ok,
            ModeFlip.MapOutcome(Evidence(terminal: ModeFlip.TokenRestoreFailed, verified: true)));
    }

    // --- preflight --------------------------------------------------------

    [Theory]
    [InlineData(DriverStatus.PatchedKmdf)]
    [InlineData(DriverStatus.StockKmdf)]
    [InlineData(DriverStatus.NotBound)]
    [InlineData(null)]
    public void Preflight_RefusesEveryDriverButThePatchedApple(DriverStatus? status)
    {
        Assert.Equal(ModeFlipOutcome.NotPathA,
            ModeFlip.PreflightOutcome(status, instanceCount: 2, appleFilterRegistered: true));
    }

    [Fact]
    public void Preflight_RefusesWhenNoLiveInstanceResolves()
    {
        Assert.Equal(ModeFlipOutcome.NoInstances,
            ModeFlip.PreflightOutcome(DriverStatus.PathAPatched, 0, appleFilterRegistered: true));
    }

    [Fact]
    public void Preflight_RefusesWhenTheFilterIsAlreadyGone()
    {
        // Patched Apple with no Apple filter on LowerFilters means the stack is
        // in Mode A right now. Flipping would have no Mode B to return to.
        Assert.Equal(ModeFlipOutcome.NotPathA,
            ModeFlip.PreflightOutcome(DriverStatus.PathAPatched, 2, appleFilterRegistered: false));
    }

    [Fact]
    public void Preflight_ProceedsOnlyOnPatchedAppleWithALiveInstanceAndTheFilter()
    {
        Assert.Null(ModeFlip.PreflightOutcome(DriverStatus.PathAPatched, 2, appleFilterRegistered: true));
    }

    [Fact]
    public void AppleFilterRegistered_IsFamilyMembershipNotAnExactName()
    {
        Assert.True(ModeFlip.AppleFilterRegistered([Carrier()]));
        Assert.True(ModeFlip.AppleFilterRegistered(
            [new ModeFlipTarget(DeviceKey, true, ["AppleWirelessMouse204"])]));
        Assert.False(ModeFlip.AppleFilterRegistered(
            [new ModeFlipTarget(DeviceKey, true, ["MagicMouseDriver204Scroll"])]));
        Assert.False(ModeFlip.AppleFilterRegistered([NoValue()]));
    }

    // --- verification -----------------------------------------------------

    [Fact]
    public void VerifyRestored_UnreadableFiltersAreNoEvidence()
    {
        Assert.Null(ModeFlip.VerifyRestored(null, modeBObserved: true));
        Assert.Null(ModeFlip.VerifyRestored(null, modeBObserved: false));
    }

    [Fact]
    public void VerifyRestored_NeedsBothTheValueAndTheHidShape()
    {
        Assert.True(ModeFlip.VerifyRestored(true, modeBObserved: true));
        // Registry right but the unified HID path never came back: the wheel
        // is what Mode B is for, so this is not a restore.
        Assert.False(ModeFlip.VerifyRestored(true, modeBObserved: false));
        Assert.False(ModeFlip.VerifyRestored(false, modeBObserved: true));
    }

    [Fact]
    public void CompareTargets_MatchesVerbatimValueAndPresence()
    {
        var recorded = new[] { Carrier(), NoValue() };

        Assert.True(ModeFlip.CompareTargets(recorded, [Carrier(), NoValue()]));
        // Case is a spelling difference in a service name, not a change.
        Assert.True(ModeFlip.CompareTargets(recorded,
            [new ModeFlipTarget(DeviceKey, true, ["AppleWirelessMouse", "MOUHID"]), NoValue()]));
        // Still in Mode A: the filter did not come back.
        Assert.False(ModeFlip.CompareTargets(recorded,
            [new ModeFlipTarget(DeviceKey, true, ["mouhid"]), NoValue()]));
        // Order is part of the value - Windows loads LowerFilters in order.
        Assert.False(ModeFlip.CompareTargets(recorded,
            [new ModeFlipTarget(DeviceKey, true, ["mouhid", "applewirelessmouse"]), NoValue()]));
        // A key that carried nothing must not come back carrying something.
        Assert.False(ModeFlip.CompareTargets(recorded,
            [Carrier(), new ModeFlipTarget(InstanceKey, true, ["applewirelessmouse"])]));
    }

    [Fact]
    public void CompareTargets_MissingKeyOrUnreadableHiveIsNoEvidence()
    {
        var recorded = new[] { Carrier(), NoValue() };

        Assert.Null(ModeFlip.CompareTargets(recorded, null));
        Assert.Null(ModeFlip.CompareTargets(recorded, [Carrier()]));
        Assert.Null(ModeFlip.CompareTargets([], [Carrier()]));
    }

    [Fact]
    public void PhantomNodesAreNeverTargets()
    {
        Assert.True(ModeFlip.IsPhantomNode(@"USB\VID_05AC&PID_0323\5&2bd2cd23&0&4"));
        Assert.True(ModeFlip.IsPhantomNode(@"HID\VID_05AC&PID_0323&MI_01&Col01\a&16288706&0&0000"));
        Assert.False(ModeFlip.IsPhantomNode(
            @"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0323\9&73b8b28&0&D0C050CC8C4D_C00000000"));
    }

    // --- sentinel ---------------------------------------------------------

    [Fact]
    public void Sentinel_RoundTripsEveryRecordedKey()
    {
        var written = new ModeFlipSentinel(
            ModeFlip.SentinelVersion, 1_757_900_000_000, ModeFlip.V3Pid, [Carrier(), NoValue()]);

        var text = ModeFlip.FormatSentinel(written);
        // The terminator is what makes a torn write detectable at all.
        Assert.Contains("end=2\n", text, StringComparison.Ordinal);
        Assert.True(ModeFlip.TryParseSentinel(text, out var read));
        Assert.NotNull(read);
        Assert.Equal(written.StartedUnixMs, read!.StartedUnixMs);
        Assert.Equal(ModeFlip.V3Pid, read.Pid);
        Assert.Equal(2, read.Targets.Count);
        Assert.Equal(DeviceKey, read.Targets[0].KeyPath);
        Assert.True(read.Targets[0].PreviousPresent);
        Assert.Equal(new[] { "applewirelessmouse", "mouhid" }, read.Targets[0].Previous);
        // The key with no value survives as "had no value", not as empty.
        Assert.Equal(InstanceKey, read.Targets[1].KeyPath);
        Assert.False(read.Targets[1].PreviousPresent);
        Assert.Empty(read.Targets[1].Previous);
    }

    [Fact]
    public void Sentinel_KeepsPresentButEmptyApartFromAbsent()
    {
        var absent = new ModeFlipTarget(InstanceKey, false, []);
        var empty = new ModeFlipTarget(DeviceKey, true, []);

        Assert.True(ModeFlip.TryParseSentinel(
            ModeFlip.FormatSentinel(Sentinel(absent, empty)), out var read));
        Assert.NotNull(read);
        Assert.False(read!.Targets[0].PreviousPresent);
        Assert.True(read.Targets[1].PreviousPresent);
        Assert.Empty(read.Targets[0].Previous);
        Assert.Empty(read.Targets[1].Previous);
        // The two are not interchangeable to the verifier either: a key that
        // carried nothing must not come back carrying an empty value.
        Assert.False(ModeFlip.CompareTargets(
            [absent], [new ModeFlipTarget(InstanceKey, true, [])]));
    }

    [Fact]
    public void Sentinel_SurvivingMeansAPreviousCycleDied()
    {
        var live = ModeFlip.FormatSentinel(new ModeFlipSentinel(
            ModeFlip.SentinelVersion, 1_757_900_000_000, ModeFlip.V3Pid, [Carrier()]));

        Assert.True(ModeFlip.IsStaleSentinel(live));
        // Windows line endings from a hand-edited file are still readable.
        Assert.True(ModeFlip.IsStaleSentinel(live.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a sentinel at all")]
    // No key line: nothing to put back, so this cannot drive a restore.
    [InlineData("v=1\nstarted=1757900000000\npid=0323\n")]
    // A version this build does not understand must not be acted on.
    [InlineData("v=2\nstarted=1757900000000\npid=0323\nkey=HKLM:\\x\npresent=true\nfilter=applewirelessmouse\n")]
    // A filter name that could not be re-elevated safely is refused here too.
    [InlineData("v=1\nstarted=1757900000000\npid=0323\nkey=HKLM:\\x\npresent=true\nfilter=a'; del\n")]
    // A key with no present= line at all. Defaulting it would turn a write
    // torn between the two lines into "this key carried no LowerFilters", and
    // recovery would then take that as licence to leave the filter off.
    [InlineData("v=1\nstarted=1757900000000\npid=0323\nkey=HKLM:\\x\nfilter=applewirelessmouse\nend=1\n")]
    // No terminator: the file was truncated mid-write.
    [InlineData("v=1\nstarted=1757900000000\npid=0323\nkey=HKLM:\\x\npresent=true\nfilter=applewirelessmouse\n")]
    // A terminator that disagrees with what was actually read.
    [InlineData("v=1\nstarted=1757900000000\npid=0323\nkey=HKLM:\\x\npresent=true\nfilter=applewirelessmouse\nend=2\n")]
    // Lines before any key= belong to nothing, and must not be adopted by the
    // first key that turns up.
    [InlineData("v=1\nstarted=1757900000000\npid=0323\npresent=true\nfilter=applewirelessmouse\nkey=HKLM:\\x\npresent=false\nend=1\n")]
    public void Sentinel_UnusableContentIsNotAStaleCycle(string? raw)
    {
        Assert.False(ModeFlip.IsStaleSentinel(raw));
        Assert.False(ModeFlip.TryParseSentinel(raw, out var parsed));
        Assert.Null(parsed);
    }

    // --- transcript parsing ----------------------------------------------

    // Captured verbatim from a real run of the generated script (rehearsal
    // against a scratch key, 2026-09-15).
    const string HappyTranscript = """
        started pid=0323
        instances=1
        prev key=HKLM:\x LowerFilters=[applewirelessmouse|mouhid]
        flip write key=HKLM:\x LowerFilters=[mouhid]
        flip restart BTHENUM\FAKE
        mode_a=True
        ready
        done_signal=True
        restore key=HKLM:\x LowerFilters=[applewirelessmouse|mouhid]
        restore restart BTHENUM\FAKE
        post key=HKLM:\x prev=[applewirelessmouse|mouhid] now=[applewirelessmouse|mouhid]
        post mode_b=True filters_match=True
        restored
        """;

    [Fact]
    public void Transcript_ReadyAndTerminalTokensAreReadFromWholeLines()
    {
        Assert.True(ModeFlip.HasToken(HappyTranscript, ModeFlip.TokenReady));
        Assert.Equal(ModeFlip.TokenRestored, ModeFlip.TerminalToken(HappyTranscript));
        // "done_signal=True" and "mode_a=True" must not be mistaken for the
        // bare tokens the tray acts on.
        Assert.False(ModeFlip.HasToken("done_signal=True\nmode_a=True\n", ModeFlip.TokenReady));
        Assert.Equal("", ModeFlip.TerminalToken("started pid=0323\ninstances=2\n"));
    }

    [Fact]
    public void Transcript_TheLastTerminalTokenWins()
    {
        // The finally block always appends its verdict after any earlier
        // refusal or failure line.
        Assert.Equal(ModeFlip.TokenRestored,
            ModeFlip.TerminalToken("flip-failed\nrestore-failed\nrestored\n"));
        Assert.Equal(ModeFlip.TokenRestoreFailed,
            ModeFlip.TerminalToken("ready\nrestored\nrestore-failed\n"));
    }

    // --- user-facing wording ---------------------------------------------

    [Fact]
    public void Detail_RestoreFailureWarnsAboutScrollAndNamesTheOneClickFix()
    {
        var detail = ModeFlip.DetailFor(ModeFlipOutcome.RestoreFailed, -1, restoredToModeB: false);

        Assert.Contains("scroll", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restore scroll mode", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_ReportsThePercentItActuallyRead()
    {
        Assert.Contains("57%", ModeFlip.DetailFor(ModeFlipOutcome.Ok, 57, restoredToModeB: true), StringComparison.Ordinal);
        // A standalone restore has no reading to report and must not invent one.
        Assert.DoesNotContain("%", ModeFlip.DetailFor(ModeFlipOutcome.Ok, -1, restoredToModeB: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_RefusalsSayNothingWasChanged()
    {
        foreach (var outcome in new[] { ModeFlipOutcome.NotPathA, ModeFlipOutcome.NoInstances, ModeFlipOutcome.Cancelled })
        {
            Assert.Contains("Nothing was changed",
                ModeFlip.DetailFor(outcome, -1, restoredToModeB: true), StringComparison.Ordinal);
        }
    }
}
