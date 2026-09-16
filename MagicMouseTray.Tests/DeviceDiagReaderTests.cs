// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// DeviceDiagReader.EvaluateCounterSample is the whole multitouch decision with
// the registry lifted out: the driver rewrites Diag\AclTranslateCount every
// 1000 ms, and an INCREASE since our previous sample is the tray's only
// positive proof that the multitouch stream is flowing.
//
// ClassifyPointerKey / SelectPointerKeys are the same shape for the pointer
// child: the "which Enum\HID node is this mouse's pointer" decision, with the
// registry walk and CM_Locate_DevNodeW left in PointerChildLive. Those two
// untestable halves stay untested - PointerChildLive needs a live
// HKLM\...\Enum\HID plus CM_Locate_DevNodeW, and WatcherState needs
// C:\ProgramData\MagicMouseDriver on disk. Neither can run on a clean CI box,
// and faking them would only test the fake.
//
// Isolation is by unique service name per test rather than a state-reset hook:
// the sampling state is keyed per service, so distinct keys cannot leak into
// each other or into a real run, and it keeps "a real run can never forget a
// proven advance" true by construction. Every time is injected, so no test
// touches the wall clock.
public class DeviceDiagReaderTests
{
    static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N");

    static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    static bool? Sample(string service, long value, DateTime atUtc) =>
        DeviceDiagReader.EvaluateCounterSample(service, value, atUtc, out _, out _);

    [Fact]
    public void EvaluateCounterSample_FirstSample_IsUnknown()
    {
        var svc = Unique("mt-first-");

        // A delta needs history. The first read of a service can only establish
        // the baseline, and the log line for it must say prev=none
        // last_advance=never rather than invent either.
        var advancing = DeviceDiagReader.EvaluateCounterSample(svc, 142271, T0,
            out var previous, out var lastAdvance);

        Assert.Null(advancing);
        Assert.Null(previous);
        Assert.Null(lastAdvance);
    }

    [Fact]
    public void EvaluateCounterSample_CounterIncreasedAcrossInterval_IsAdvancing()
    {
        var svc = Unique("mt-rising-");

        // Reference PC (Lesleys-PC, PID 0323) with scroll working, sampled at
        // 2.5 s: AclTranslateCount 142271 -> 142513.
        Assert.Null(Sample(svc, 142271, T0));

        var advancing = DeviceDiagReader.EvaluateCounterSample(svc, 142513, T0.AddSeconds(2.5),
            out var previous, out _);

        Assert.True(advancing);
        Assert.Equal<long?>(142271, previous);
    }

    [Fact]
    public void EvaluateCounterSample_BurstReadsAfterMovement_StayAdvancing()
    {
        var svc = Unique("mt-burst-");

        // THE REGRESSION. A snapshot sweep reads the same PID several times
        // inside one second: menu Opening, then the poll tick, then
        // AfterFindingHandled. The original code overwrote its own baseline on
        // every read, so the reads that followed the first movement compared a
        // number against itself and the live log showed
        //   DEVICE_DIAG_MT svc=... advancing=unknown counter=AclTranslateCount
        //   value=169098 prev=169098 last_advance=...
        // on a mouse that was actively being scrolled. Sub-second reads must
        // not be allowed to erase evidence the sweep already collected.
        Assert.Null(Sample(svc, 169000, T0));
        Assert.True(Sample(svc, 169098, T0.AddMilliseconds(200)));
        Assert.True(Sample(svc, 169098, T0.AddMilliseconds(400)));
        Assert.True(Sample(svc, 169098, T0.AddMilliseconds(600)));
    }

    [Fact]
    public void EvaluateCounterSample_UnchangedWithNoPriorMovement_IsUnknownNotFalse()
    {
        var svc = Unique("mt-idle-");

        // A hand off the mouse for a few seconds is byte-identical in the
        // registry to a dead multitouch stream, so a still counter over a real
        // interval is "no evidence" - never false, which would fire a finding
        // on every user who simply is not touching their mouse.
        Assert.Null(Sample(svc, 500, T0));
        Assert.Null(Sample(svc, 500, T0.AddSeconds(3)));
        Assert.Null(Sample(svc, 500, T0.AddSeconds(6)));
    }

    [Fact]
    public void EvaluateCounterSample_UnchangedShortlyAfterMovement_StaysAdvancing()
    {
        var svc = Unique("mt-alive-");

        Assert.Null(Sample(svc, 1000, T0));
        Assert.True(Sample(svc, 1001, T0.AddSeconds(3)));

        // The counter only climbs while a hand is on the mouse. Without the
        // remembered advance the same healthy device would alternate between
        // "working" and "not verified" from one menu open to the next.
        Assert.True(Sample(svc, 1001, T0.AddSeconds(3 + 30)));
        Assert.True(Sample(svc, 1001, T0.AddSeconds(3 + 59)));
    }

    [Fact]
    public void EvaluateCounterSample_UnchangedLongAfterMovement_ReturnsToUnknown()
    {
        var svc = Unique("mt-stale-");

        Assert.Null(Sample(svc, 1000, T0));
        Assert.True(Sample(svc, 1001, T0.AddSeconds(3)));

        // A delta can only ever claim something about the recent past, so the
        // proof expires. Just past the window it is back to no evidence - still
        // not false.
        Assert.Null(Sample(svc, 1001, T0.AddSeconds(3 + 61)));
    }

    [Fact]
    public void EvaluateCounterSample_CounterWentBackwards_IsUnknownAndBaselineRecovers()
    {
        var svc = Unique("mt-reset-");

        // The Diag counters restart at zero when the filter reloads (driver
        // update, disable/enable, reboot). A drop proves nothing about the
        // stream, at any interval - not inside a burst, not across one.
        Assert.Null(Sample(svc, 169098, T0));
        Assert.Null(Sample(svc, 12, T0.AddMilliseconds(500)));
        Assert.Null(Sample(svc, 12, T0.AddSeconds(3)));

        // And the reader must not be stuck comparing against a value the
        // driver will never reach again: the post-reload counter becomes the
        // baseline, so the next climb is proof.
        Assert.True(Sample(svc, 40, T0.AddSeconds(6)));
    }

    [Fact]
    public void EvaluateCounterSample_DifferentServices_DoNotShareState()
    {
        var kmdf = Unique("mt-kmdf-");
        var apple = Unique("mt-apple-");

        // A machine can carry two filter services (the KMDF family and an
        // Apple-family leftover). Proof for the bound filter must never stand
        // in as proof for the other one.
        Assert.Null(Sample(kmdf, 100, T0));
        Assert.True(Sample(kmdf, 200, T0.AddSeconds(3)));

        Assert.Null(Sample(apple, 200, T0.AddSeconds(3)));
        Assert.Null(Sample(apple, 200, T0.AddSeconds(6)));
        Assert.True(Sample(kmdf, 200, T0.AddSeconds(6)));
    }

    // ---- pointer child selection -------------------------------------------
    //
    // Key-name fixtures only: ClassifyPointerKey/SelectPointerKeys are the
    // whole "which Enum\HID node is this mouse's pointer" decision with the
    // registry and CM_Locate_DevNodeW left outside, which is what makes it
    // testable on a clean box.

    // Measured live on the reference PC, 2026-09-15, with the user confirming
    // pointer AND scroll both working on a Magic Mouse v1 at that moment:
    //   HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&000205AC_PID&030D\A&137E1BF2&9&0000
    // One collection, so Windows wrote NO &Col suffix. Requiring COL01 made
    // this device unreportable (live=unknown keys=0 present=0 forever).
    const string V1SoleKey = "{00001124-0000-1000-8000-00805F9B34FB}_VID&000205AC_PID&030D";

    // The v3 (0323) SPLITS its collections: Col01 is the pointer/multitouch
    // collection, Col02 is the vendor/battery one.
    const string V3Col01Key = "{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0323&Col01";
    const string V3Col02Key = "{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0323&Col02";

    // Left behind by any USB-C charge on the reference PC, COL01 and all.
    const string UsbPhantomKey = "VID_05AC&PID_0323&MI_01&Col01";

    static DeviceDiagReader.PointerKeyKind Classify(string key, string pid) =>
        DeviceDiagReader.ClassifyPointerKey(key, pid);

    [Fact]
    public void ClassifyPointerKey_V1SingleCollectionBluetoothNode_IsThePointerChild()
    {
        Assert.Equal(DeviceDiagReader.PointerKeyKind.SoleCollection, Classify(V1SoleKey, "030d"));
    }

    [Fact]
    public void ClassifyPointerKey_V3Col01_IsThePointerChild()
    {
        Assert.Equal(DeviceDiagReader.PointerKeyKind.Col01, Classify(V3Col01Key, "0323"));
    }

    [Theory]
    // The vendor/battery collection: a healthy battery channel must never be
    // allowed to vouch for a dead cursor, nor take the elevated restart.
    [InlineData(V3Col02Key, "0323")]
    // Nothing above 01 is the pointer either.
    [InlineData("{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0323&Col03", "0323")]
    // Charge-cable phantom as it appears as an Enum\HID key name...
    [InlineData(UsbPhantomKey, "0323")]
    // ...and in its full instance-path spelling.
    [InlineData(@"HID\VID_05AC&PID_030D&MI_01&Col01", "030d")]
    // Some other vendor's Bluetooth HID mouse on the same hive.
    [InlineData("{00001124-0000-1000-8000-00805f9b34fb}_VID&0000046d_PID&b01d", "b01d")]
    // Right shape, wrong mouse: the v1 key must not answer for the v3.
    [InlineData(V1SoleKey, "0323")]
    // Apple VID and PID, but not the Bluetooth HID-profile transport.
    [InlineData("{00001200-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&030d", "030d")]
    public void ClassifyPointerKey_NonPointerShapes_AreRejected(string key, string pid)
    {
        Assert.Equal(DeviceDiagReader.PointerKeyKind.None, Classify(key, pid));
    }

    [Fact]
    public void SelectPointerKeys_V3SplitCollectionsBesideAPhantom_PicksCol01Only()
    {
        var set = DeviceDiagReader.SelectPointerKeys(
            [V3Col02Key, UsbPhantomKey, V3Col01Key], "0323");

        Assert.Equal(V3Col01Key, Assert.Single(set.Keys));
        Assert.Equal("col01", set.Shape);
        Assert.Equal(1, set.Col01Count);
        Assert.Equal(0, set.SoleCount);
    }

    [Fact]
    public void SelectPointerKeys_V1SoleCollectionBesideAPhantom_PicksTheCollectionlessNode()
    {
        var set = DeviceDiagReader.SelectPointerKeys(
            ["VID_05AC&PID_030D&MI_01&Col01", V1SoleKey, V3Col01Key], "030d");

        Assert.Equal(V1SoleKey, Assert.Single(set.Keys));
        Assert.Equal("sole", set.Shape);
        Assert.Equal(1, set.SoleCount);
    }

    [Fact]
    public void SelectPointerKeys_CollectionlessBesideCol01ForOnePid_PicksCol01()
    {
        // COL01 is the pointer collection by construction. The collection-less
        // sibling of a splitting device is the aggregate parent - the live v3
        // reports Status Unknown on it - so it is no evidence about the cursor
        // and the wrong devnode for the elevated pnputil /restart-device of
        // PointerChildMissing. Calling this pair "ambiguous" is what regressed
        // the v3 from live=true keys=1 present=1 to live=unknown keys=0.
        const string col01 =
            "{00001124-0000-1000-8000-00805F9B34FB}_VID&000205AC_PID&030D&Col01";
        var set = DeviceDiagReader.SelectPointerKeys([V1SoleKey, col01], "030d");

        Assert.Equal(col01, Assert.Single(set.Keys));
        Assert.Equal("col01", set.Shape);
        Assert.Equal(1, set.Col01Count);
        // Seen and deliberately ignored, which is what the log line must show.
        Assert.Equal(1, set.SoleCount);
    }

    // The EXACT key set enumerated live on the reference PC for the v3 (0323)
    // on 2026-09-15 while the user was using the mouse:
    //   HID\{00001124-...FB}_VID&0001004C_PID&0323&COL01\A&31E5D054&2A&0000
    //       Status OK,      class Mouse    - the pointer collection.
    //   HID\{00001124-...FB}_VID&0001004C_PID&0323&COL02\A&31E5D054&2A&0001
    //       Status OK,      class HIDClass - vendor/battery.
    //   HID\{00001124-...FB}_VID&0001004C_PID&0323\A&31E5D054&2A&0000
    //       Status Unknown, class Mouse    - the collection-less aggregate.
    // The absence of this fixture is what let the ambiguity rule ship and take
    // a working v3 pointer back to live=unknown.
    [Fact]
    public void SelectPointerKeys_LiveV3ThreeKeyReality_PicksCol01AndIgnoresTheRest()
    {
        const string liveCol01 =
            "{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323&COL01";
        const string liveCol02 =
            "{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323&COL02";
        const string liveCollectionless =
            "{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323";

        var set = DeviceDiagReader.SelectPointerKeys(
            [liveCol01, liveCol02, liveCollectionless], "0323");

        Assert.Equal(liveCol01, Assert.Single(set.Keys));
        Assert.Equal("col01", set.Shape);
        Assert.Equal(1, set.Col01Count);
        Assert.DoesNotContain(liveCol02, set.Keys);
        Assert.DoesNotContain(liveCollectionless, set.Keys);
    }

    [Fact]
    public void SelectPointerKeys_TwoCollectionlessCandidates_IsAmbiguousSoNoEvidence()
    {
        // The same PID under both Apple VID spellings - a stale pairing record
        // beside the live one. "Sole" means sole; two is a guess.
        var set = DeviceDiagReader.SelectPointerKeys(
            [V1SoleKey, "{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&030d"],
            "030d");

        Assert.Empty(set.Keys);
        Assert.Equal("ambiguous", set.Shape);
        Assert.Equal(2, set.SoleCount);
    }

    [Fact]
    public void SelectPointerKeys_PhantomsOnly_IsNoCandidateAtAll()
    {
        var set = DeviceDiagReader.SelectPointerKeys(
            [UsbPhantomKey, V3Col02Key], "0323");

        Assert.Empty(set.Keys);
        Assert.Equal("none", set.Shape);
    }

    // ---- watcher log grammar ---------------------------------------------
    //
    // WatcherState() needs C:\ProgramData\MagicMouseDriver on disk, but
    // ScanWatcherLog is that decision with the file read lifted out, so the
    // three states the tray must keep apart - running, silent, and dead with a
    // reason the watcher printed itself - are tested on synthetic logs.

    const string HeartbeatLine = "[2026-09-14 08:05:00] heartbeat alive";

    // driver-repo issue #34: the watcher hard-depends on
    // C:\mm-dev-queue\mm-f1-once.ps1 and prints this as it exits.
    const string FatalLine = "[2026-09-14 08:10:00] FATAL missing F1 script";

    [Fact]
    public void ScanWatcherLog_HeartbeatLog_IsRunningAndNotFatal()
    {
        var state = DeviceDiagReader.ScanWatcherLog(true,
            ["[2026-09-14 08:00:00] watcher start", HeartbeatLine,
                "[2026-09-14 08:05:01] SetFeature ok=True"]);

        Assert.NotNull(state.LastHeartbeatUtc);
        Assert.True(state.LastF1Ok);
        Assert.Null(state.FatalReason);
    }

    [Fact]
    public void ScanWatcherLog_LogEndsWithFatal_KeepsTheWatchersOwnWordsVerbatim()
    {
        var state = DeviceDiagReader.ScanWatcherLog(true, [HeartbeatLine, FatalLine + "\r"]);

        // Quoted exactly, CR of the CRLF log stripped: the reason is the
        // watcher's claim about itself, so the tray must not reword it.
        Assert.Equal("FATAL missing F1 script", state.FatalReason);

        // The earlier heartbeat still happened and is still reported; what
        // changed is that the tray now also knows the watcher died after it.
        Assert.NotNull(state.LastHeartbeatUtc);
    }

    [Fact]
    public void ScanWatcherLog_FatalThenALaterHeartbeat_IsRecoveredNotFatal()
    {
        // Something the watcher wrote AFTER the fatal proves the process that
        // printed it was replaced. A restarted watcher must not be called dead.
        var state = DeviceDiagReader.ScanWatcherLog(true,
            [FatalLine, "[2026-09-14 08:15:00] heartbeat alive"]);

        Assert.Null(state.FatalReason);
        Assert.NotNull(state.LastHeartbeatUtc);
    }

    [Fact]
    public void ScanWatcherLog_NothingToRead_IsUnknownAndNeverFatal()
    {
        // An empty log and an absent one arrive here identically - ReadLogTail
        // yields no lines for both - and neither is evidence of anything.
        var installed = DeviceDiagReader.ScanWatcherLog(true, []);
        var probeFailed = DeviceDiagReader.ScanWatcherLog(null, []);

        Assert.True(installed.Installed);
        Assert.Null(installed.LastHeartbeatUtc);
        Assert.Null(installed.FatalReason);
        Assert.Null(probeFailed.Installed);
        Assert.Null(probeFailed.FatalReason);
    }

    [Fact]
    public void ScanWatcherLog_LinesThatMerelyMentionFatal_AreNotADeath()
    {
        // The parse stays narrow on purpose: the word inside a routine line, a
        // longer word that starts with it, and a bare marker with no
        // explanation are none of them lines this watcher writes. Reading a
        // death out of any of them would invent a fault out of nothing.
        var state = DeviceDiagReader.ScanWatcherLog(true,
            ["[2026-09-14 08:00:00] retrying after FATAL from mm-f1-once",
                "[2026-09-14 08:01:00] FATALITY",
                "[2026-09-14 08:02:00] FATAL   "]);

        Assert.Null(state.FatalReason);
    }
}
