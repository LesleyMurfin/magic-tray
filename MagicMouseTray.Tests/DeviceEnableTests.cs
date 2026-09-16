// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DeviceEnableTests
{
    [Fact]
    public void VidNeedles_030d_FromMouseCatalog()
    {
        var needles = DeviceEnable.VidNeedlesForPid("030d");
        Assert.Contains("000205AC", needles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("VID_05AC", needles, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("0000045e", needles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void VidNeedles_0323_IncludesBleCompanyId()
    {
        var needles = DeviceEnable.VidNeedlesForPid("0323");
        Assert.Contains("0001004C", needles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("VID_05AC", needles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void VidNeedles_0239_FromKeyboardCatalog()
    {
        var needles = DeviceEnable.VidNeedlesForPid("0239");
        Assert.Contains("000205AC", needles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("VID_05AC", needles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void VidNeedles_UnknownPid_Empty()
    {
        Assert.Empty(DeviceEnable.VidNeedlesForPid("abcd"));
        Assert.Empty(DeviceEnable.VidNeedlesForPid("logi"));
        Assert.Empty(DeviceEnable.VidNeedlesForPid(""));
    }

    [Fact]
    public void Matches_Hid030d_Not0323()
    {
        const string hid = @"HID\VID_05AC&PID_030D&COL01\7&abc";
        Assert.True(DeviceEnable.MatchesInstance(hid, "030d"));
        Assert.True(DeviceEnable.MatchesInstance(hid, "030D"));
        Assert.False(DeviceEnable.MatchesInstance(hid, "0323"));
        Assert.False(DeviceEnable.MatchesInstance(hid, "0239"));
    }

    [Fact]
    public void Matches_Bthenum030d_RequiresCatalogVid()
    {
        const string bt =
            @"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&030d\8&def";
        Assert.True(DeviceEnable.MatchesInstance(bt, "030d"));
        Assert.False(DeviceEnable.MatchesInstance(
            @"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0000045e_PID&030d\8&def",
            "030d"));
    }

    [Fact]
    public void Matches_BthledeviceKeyboard_RequiresCatalogVid()
    {
        const string ble =
            @"BTHLEDEVICE\{00001812-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&0239\8&abc";
        Assert.True(DeviceEnable.MatchesInstance(ble, "0239"));
        Assert.False(DeviceEnable.MatchesInstance(ble, "030d"));
        Assert.False(DeviceEnable.MatchesInstance(
            @"BTHLEDEVICE\{00001812-0000-1000-8000-00805f9b34fb}_VID&0000045e_PID&0239\8&abc",
            "0239"));
    }

    // One attempt, one nonce. It names the generated script and the status
    // sidecar on both sides of the elevation boundary.
    const long Nonce = 1_757_900_000_000;

    [Fact]
    public void DisableScript_QuotesInstanceId_WalksEnum_CatalogVids()
    {
        var script = DeviceEnable.BuildScript("030d", Nonce, enable: false);
        Assert.Contains("pnputil.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"/$verb\" \"$id\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/$verb $id", script, StringComparison.Ordinal);
        Assert.Contains("if ($ids.Count -eq 0)", script, StringComparison.Ordinal);
        Assert.Contains("exit 2", script, StringComparison.Ordinal);
        Assert.Contains("GetSubKeyNames()", script, StringComparison.Ordinal);
        Assert.Contains("000205AC", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VID_05AC", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("030d", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/enable-device", script, StringComparison.Ordinal);
        foreach (var name in DeviceEnable.ForbiddenNames)
            Assert.DoesNotContain(name, script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Script_SkipsUsbAndHidVid_NoInstancesExits2()
    {
        foreach (var enable in new[] { false, true })
        {
            var script = DeviceEnable.BuildScript("030d", Nonce, enable);
            Assert.Contains("if ($enumerator -eq 'USB') { return }", script, StringComparison.Ordinal);
            Assert.Contains("StartsWith('usb\\')", script, StringComparison.Ordinal);
            Assert.Contains("StartsWith('hid\\vid_')", script, StringComparison.Ordinal);
            Assert.Contains("'no-instances'", script, StringComparison.Ordinal);
            Assert.Contains("exit 2", script, StringComparison.Ordinal);
            Assert.Equal(2, DeviceEnable.NoInstancesExitCode);
        }
    }

    [Fact]
    public void Script_ReadsAndLogsContainerIdPerInstance()
    {
        foreach (var enable in new[] { false, true })
        {
            var script = DeviceEnable.BuildScript("030d", Nonce, enable);
            // ContainerID is read off each matched instance key...
            Assert.Contains("GetValue('ContainerID')", script, StringComparison.Ordinal);
            // ...recorded per instance id...
            Assert.Contains("$containers[$full] = $container", script, StringComparison.Ordinal);
            // ...and logged with the instance the verb is applied to.
            Assert.Contains(
                "Write-Host \"DEVICE_ENABLE $verb $id container=$($containers[$id])\"",
                script,
                StringComparison.Ordinal);
            // Distinct containers are logged so support sees every physical
            // device a per-PID row touched.
            Assert.Contains(
                "Write-Host \"DEVICE_ENABLE containers=$($distinct -join ',')\"",
                script,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DisableScript_SortsBthenumLast()
    {
        var script = DeviceEnable.BuildScript("030d", Nonce, enable: false);
        Assert.Contains("StartsWith('BTHENUM\\'", script, StringComparison.Ordinal);
        Assert.Contains("Sort-Object $rank)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableScript_UsesEnableDevice_BthenumFirst()
    {
        var script = DeviceEnable.BuildScript("030d", Nonce, enable: true);
        Assert.Contains("$verb = 'enable-device'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$verb = 'disable-device'", script, StringComparison.Ordinal);
        Assert.Contains("Sort-Object $rank -Descending", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScript_UnknownPid_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeviceEnable.BuildScript("abcd", Nonce, enable: false));
        Assert.Contains("No catalog VID", ex.Message, StringComparison.Ordinal);
    }

    // The nonce is used as an id, so the only property that matters is that it
    // is never handed out twice. Nothing serializes elevated attempts -
    // StartRepairApply and StartStaleFilterRemoval go straight to Task.Run, and
    // RunDriverActionAsync does not queue driver actions - so two attempts can
    // mint inside the same millisecond, which the old
    // DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() mint answered with the
    // same number. A repeat means two attempts share a script path and a status
    // sidecar path, and the poller accepts the first COMPLETE report at the
    // path it watches, so the tray would report an end state that a different
    // elevated process measured.
    [Fact]
    public void AttemptNonce_MintedConcurrently_NeverRepeats()
    {
        const int attempts = 8_000;
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var minted = new long[attempts];
        Parallel.For(0, attempts, i => minted[i] = AttemptNonce.Next());
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.Equal(attempts, minted.Distinct().Count());

        // Strictly increasing once sorted: no two attempts can name one file,
        // and a support log sorts by attempt order.
        var sorted = minted.OrderBy(n => n).ToArray();
        var notIncreasing = Enumerable.Range(1, attempts - 1).Count(i => sorted[i] <= sorted[i - 1]);
        Assert.Equal(0, notIncreasing);

        // Still a Unix millisecond reading, not an opaque counter: no value can
        // precede the clock reading taken before the fan-out, and under
        // contention the counter runs ahead of the clock by at most one per
        // attempt.
        Assert.All(minted, n => Assert.InRange(n, before, after + attempts));

        // Both sides of the elevation boundary derive the name the same way, so
        // the script writes the file the tray is polling. The process id is its
        // own segment because AttemptNonce.Next() is unique within THIS process
        // only - two trays can mint in the same millisecond.
        var script = DeviceEnable.BuildScript("030d", Nonce, enable: true);
        Assert.Contains($"$procId = '{Environment.ProcessId}'", script, StringComparison.Ordinal);
        Assert.Contains($"$nonce = '{Nonce}'", script, StringComparison.Ordinal);
        Assert.Contains(
            "$statusFile = Join-Path $env:TEMP ('mm-enable-' + $targetPid + '-' + $procId + '-' + $nonce + '.status')",
            script, StringComparison.Ordinal);
        Assert.Equal(
            $"mm-enable-030d-{Environment.ProcessId}-{Nonce}.status",
            Path.GetFileName(DeviceEnable.StatusSidecarPath("030d", Nonce)));
        Assert.Equal(
            $"mm-enable-030d-{Environment.ProcessId}-{Nonce}.ps1",
            Path.GetFileName(DeviceEnable.ScriptPath("030d", Nonce)));
    }

    // --- idempotency and end-state verification --------------------------

    // Readings are injected: no registry, no elevated process, no pnputil.
    static DeviceEnableEvidence Report(
        string result, int total, int pre, int post, bool ran, int? code, int? exit = null) =>
        new(Started: true, new DeviceEnableReading(result, total, pre, post, ran, code), exit);

    [Fact]
    public void Script_PreChecksState_ThenReReadsAfterPnputil()
    {
        foreach (var enable in new[] { false, true })
        {
            var script = DeviceEnable.BuildScript("030d", Nonce, enable);
            // State is read off CONFIGFLAG_DISABLED, per instance.
            Assert.Contains("$flags = $k.GetValue('ConfigFlags')", script, StringComparison.Ordinal);
            Assert.Contains("-band 0x20", script, StringComparison.Ordinal);
            // Pre-check, and it short-circuits the whole pnputil pass.
            Assert.Contains("$pre = Get-WantedCount $wantEnabled", script, StringComparison.Ordinal);
            Assert.Contains("if ($pre -eq $total) {", script, StringComparison.Ordinal);
            Assert.Contains("Write-Report 'already' $total $pre $pre $false $null", script, StringComparison.Ordinal);
            // Post-read, and the verdict is taken from it.
            Assert.Contains("$post = Get-WantedCount $wantEnabled", script, StringComparison.Ordinal);
            Assert.Contains("if ($post -eq $total) {", script, StringComparison.Ordinal);

            var preCheck = script.IndexOf("if ($pre -eq $total) {", StringComparison.Ordinal);
            var call = script.IndexOf("& pnputil.exe", StringComparison.Ordinal);
            var postRead = script.IndexOf("$post = Get-WantedCount", StringComparison.Ordinal);
            Assert.InRange(preCheck, 0, call);
            Assert.InRange(call, 0, postRead);
        }
    }

    [Fact]
    public void AlreadyInWantedState_SucceedsAndRanNoPnputil()
    {
        var evidence = Report("already", total: 2, pre: 2, post: 2, ran: false, code: null, exit: 0);
        var result = DeviceEnable.Decide(evidence, enable: true);

        Assert.Equal(DeviceEnableOutcome.AlreadyInState, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.False(evidence.Reading!.Value.PnputilRan);
        Assert.Contains("already had this device enabled", result.Detail, StringComparison.Ordinal);
        Assert.Equal("already-enabled", DeviceEnable.VerifiedToken(result.Outcome, enable: true));
    }

    [Fact]
    public void PnputilNonZero_ButEndStateWanted_Succeeds()
    {
        var result = DeviceEnable.Decide(
            Report("ok", total: 2, pre: 0, post: 2, ran: true, code: 1),
            enable: true);

        Assert.Equal(DeviceEnableOutcome.Changed, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal("enabled", DeviceEnable.VerifiedToken(result.Outcome, enable: true));
    }

    [Fact]
    public void PnputilZero_ButEndStateUnchanged_Fails()
    {
        // The mirror of the case above: an exit code of 0 proves nothing.
        var result = DeviceEnable.Decide(
            Report("not-verified", total: 1, pre: 0, post: 0, ran: true, code: 0),
            enable: false);

        Assert.Equal(DeviceEnableOutcome.Failed, result.Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void PnputilNonZero_AndEndStateWrong_FailsWithoutBlamingUac()
    {
        var result = DeviceEnable.Decide(
            Report("not-verified", total: 2, pre: 0, post: 1, ran: true, code: 1),
            enable: true);

        Assert.Equal(DeviceEnableOutcome.Failed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Contains("The elevated step ran", result.Detail, StringComparison.Ordinal);
        Assert.Contains("1 of 2 instances", result.Detail, StringComparison.Ordinal);
        Assert.Contains("pnputil exited 1", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("UAC", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cancel", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ElevationNeverStarted_IsTheOnlyUacMessage()
    {
        var result = DeviceEnable.Decide(
            new DeviceEnableEvidence(Started: false, Reading: null, Exit: null),
            enable: true);

        Assert.Equal(DeviceEnableOutcome.UacDeclined, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Contains("UAC", result.Detail, StringComparison.Ordinal);
        Assert.Equal("not-started", DeviceEnable.VerifiedToken(result.Outcome, enable: true));
    }

    [Fact]
    public void StartedButNeverReported_FailsWithoutBlamingUac()
    {
        var result = DeviceEnable.Decide(
            new DeviceEnableEvidence(Started: true, Reading: null, Exit: null),
            enable: true);

        Assert.Equal(DeviceEnableOutcome.Failed, result.Outcome);
        Assert.Equal(DeviceEnable.StartedButSilentDetail, result.Detail);
        Assert.DoesNotContain("UAC", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoInstances_IsItsOwnOutcome()
    {
        var reported = DeviceEnable.Decide(
            Report("no-instances", total: 0, pre: 0, post: 0, ran: false, code: null, exit: 2),
            enable: true);
        // Same answer when the report never arrived but the exit code did.
        var byExit = DeviceEnable.Decide(
            new DeviceEnableEvidence(Started: true, Reading: null, Exit: DeviceEnable.NoInstancesExitCode),
            enable: true);

        foreach (var result in new[] { reported, byExit })
        {
            Assert.Equal(DeviceEnableOutcome.NoInstances, result.Outcome);
            Assert.False(result.Succeeded);
            Assert.Equal(DeviceEnable.NoInstancesDetail, result.Detail);
            Assert.DoesNotContain("UAC", result.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UnreadableDeviceList_FailsAndIsNotNoInstances()
    {
        var result = DeviceEnable.Decide(
            Report("script-error", total: 0, pre: 0, post: 0, ran: false, code: null, exit: 3),
            enable: true);

        Assert.Equal(DeviceEnableOutcome.Failed, result.Outcome);
        Assert.Equal(DeviceEnable.UnreadableDeviceListDetail, result.Detail);
        Assert.DoesNotContain("UAC", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSidecar_ReadsTheReportTheScriptWrites()
    {
        var reading = DeviceEnable.ParseSidecar(
            "result=ok\r\ntotal=3\r\npre=1\r\npost=3\r\npnputil=exit:1\r\n");

        Assert.NotNull(reading);
        Assert.Equal("ok", reading!.Value.Result);
        Assert.Equal(3, reading.Value.Total);
        Assert.Equal(1, reading.Value.InWantedBefore);
        Assert.Equal(3, reading.Value.InWantedAfter);
        Assert.True(reading.Value.PnputilRan);
        Assert.Equal(1, reading.Value.PnputilExit);

        var skipped = DeviceEnable.ParseSidecar(
            "result=already\ntotal=1\npre=1\npost=1\npnputil=not-run");
        Assert.NotNull(skipped);
        Assert.False(skipped!.Value.PnputilRan);
        Assert.Null(skipped.Value.PnputilExit);
    }

    [Fact]
    public void ParseSidecar_PartialOrLegacyReport_IsNoReading()
    {
        // "running" is what the script writes before it has read anything, and
        // a half-written report must never be read as a state.
        Assert.Null(DeviceEnable.ParseSidecar("running"));
        Assert.Null(DeviceEnable.ParseSidecar(""));
        Assert.Null(DeviceEnable.ParseSidecar("result=ok\ntotal=2\npre=0"));
        Assert.Null(DeviceEnable.ParseSidecar("total=2\npre=0\npost=2\npnputil=not-run"));
    }
}
