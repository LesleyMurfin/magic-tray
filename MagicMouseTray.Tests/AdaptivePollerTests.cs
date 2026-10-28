// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class AdaptivePollerTests : IDisposable
{
    readonly string _dir;
    readonly string _path;
    readonly List<BlockingDevice> _blocked = new();

    public AdaptivePollerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mm-tray-poll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.ini");
    }

    public void Dispose()
    {
        // ReadBatteryGuarded abandons a timed-out read, so every blocked worker these tests
        // parked would stay parked for the rest of the suite - the exact cost the wedged-group
        // tests exist to bound. Release them here instead of leaking them.
        foreach (var device in _blocked)
            device.Release();

        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public void DeviceSetChanged_TrueWhen030DAppears()
    {
        var before = new[] { "Magic Keyboard" };
        var after = new[] { "Magic Keyboard", "Magic Mouse v1" };
        Assert.True(AdaptivePoller.DeviceSetChanged(before, after));
    }

    [Fact]
    public void DeviceSetChanged_FalseWhenSameNames()
    {
        var names = new[] { "Magic Keyboard", "Magic Mouse v1" };
        Assert.False(AdaptivePoller.DeviceSetChanged(names, names));
        Assert.False(AdaptivePoller.DeviceSetChanged(names, ["Magic Mouse v1", "Magic Keyboard"]));
    }

    // Discovery now yields one name per INTERFACE, so WaitForNextCycle compares a list
    // holding a mouse's name three times against the name-keyed _lastSeen. Only a
    // set comparison survives that; a count or sequence comparison would report a
    // device-set change on every 15 s probe and turn a 24 h battery interval into a
    // permanent 15 s HID poll.
    [Fact]
    public void DeviceSetChanged_FalseWhenOneDeviceRepeatsItsName()
    {
        Assert.False(AdaptivePoller.DeviceSetChanged(
            ["Magic Mouse 2024"],
            ["Magic Mouse 2024", "Magic Mouse 2024", "Magic Mouse 2024"]));
    }

    [Fact]
    public void DeviceSetChanged_TrueWhenNameDisappears()
    {
        Assert.True(AdaptivePoller.DeviceSetChanged(
            ["Magic Keyboard", "Magic Mouse v1"],
            ["Magic Keyboard"]));
    }

    [Fact]
    public void DeviceSetProbeInterval_Is15Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), AdaptivePoller.DeviceSetProbeInterval);
    }

    [Fact]
    public void ShouldSkipPid_Disabled030d_NotOthers()
    {
        var cfg = Config.Load(_path);
        Assert.False(AdaptivePoller.ShouldSkipPid(cfg, "030d"));
        Assert.False(AdaptivePoller.ShouldSkipPid(cfg, "0323"));

        cfg.SetDeviceEnabled("030d", false);

        Assert.True(AdaptivePoller.ShouldSkipPid(cfg, "030d"));
        Assert.False(AdaptivePoller.ShouldSkipPid(cfg, "0323"));

        int hidReads = 0;
        foreach (var pid in new[] { "030d", "0323" })
        {
            if (AdaptivePoller.ShouldSkipPid(cfg, pid)) continue;
            hidReads++;
        }
        Assert.Equal(1, hidReads);
    }

    [Fact]
    public void ShouldSkipPid_DiscoverOmit_Disabled030d()
    {
        var cfg = Config.Load(_path);
        cfg.SetDeviceEnabled("030d", false);

        var omitted = BatteryAlertPolicy.NamesOmittedFromDiscover(
            ["Magic Mouse v1", "Magic Keyboard"],
            ["Magic Keyboard"]);
        Assert.Equal(new[] { "Magic Mouse v1" }, omitted);

        bool raiseOmit = false;
        foreach (var name in omitted)
        {
            var pid = name == "Magic Mouse v1" ? "030d" : "0239";
            if (AdaptivePoller.ShouldSkipPid(cfg, pid)) continue;
            raiseOmit = true;
        }
        Assert.False(raiseOmit);
    }

    [Fact]
    public void ShouldSkipPid_Reenable_AllowsRead()
    {
        var cfg = Config.Load(_path);
        cfg.SetDeviceEnabled("030d", false);
        Assert.True(AdaptivePoller.ShouldSkipPid(cfg, "030d"));
        cfg.SetDeviceEnabled("030d", true);
        Assert.False(AdaptivePoller.ShouldSkipPid(cfg, "030d"));
    }

    [Fact]
    public void BestReading_FirstRealPercentWins_LaterInterfacesNotRead()
    {
        var unreadable = new CountingDevice(-2);
        var real = new CountingDevice(42);
        var later = new CountingDevice(77);

        int best = AdaptivePoller.BestReading(
            [unreadable, real, later], TimeSpan.FromSeconds(30));

        Assert.Equal(42, best);
        Assert.Equal(1, unreadable.Reads);
        Assert.Equal(1, real.Reads);
        Assert.Equal(0, later.Reads);
    }

    [Fact]
    public void BestReading_AllInterfacesFail_MinusTwoBeatsMinusOne()
    {
        var notFound = new CountingDevice(-1);
        var present = new CountingDevice(-2);
        var alsoNotFound = new CountingDevice(-1);

        int best = AdaptivePoller.BestReading(
            [notFound, present, alsoNotFound], TimeSpan.FromSeconds(30));

        Assert.Equal(-2, best);
        Assert.Equal(1, notFound.Reads);
        Assert.Equal(1, present.Reads);
        Assert.Equal(1, alsoNotFound.Reads);

        // Nothing present at all stays -1, which is what PollLoop counts three of before
        // it mints -3. Seeding best at -2 would pass every assertion above and silently
        // retire the -3 escalation for every device.
        Assert.Equal(-1, AdaptivePoller.BestReading(
            [new CountingDevice(-1), new CountingDevice(-1)], TimeSpan.FromSeconds(30)));
    }

    // A real 0 is an answer, not a failure, so it ends the group like any other
    // percentage. This is why all three IBatteryDevice implementations floor at
    // MouseBatteryDevice.MinValidPercent: an unfloored zero from a dead interface
    // would end the group here and be reported as the device's level.
    [Fact]
    public void BestReading_RealZeroCountsAsAnAnswer_AndOutranksUnreadable()
    {
        var present = new CountingDevice(-2);
        var zero = new CountingDevice(0);
        var notFound = new CountingDevice(-1);

        int best = AdaptivePoller.BestReading(
            [present, zero, notFound], TimeSpan.FromSeconds(30));

        Assert.Equal(0, best);
        Assert.Equal(1, present.Reads);
        Assert.Equal(1, zero.Reads);
        Assert.Equal(0, notFound.Reads);
    }

    // The failure this branch exists for: an interface that never answers. The scan's other exit
    // (a real percentage) can never fire there, so before the timeout exit the whole group was
    // read on every tick - measured at 3 x budget and 3 permanently parked thread-pool workers
    // per tick for one wedged 3-interface device (ticks=4 group=3 exited=0 wall_ms=1478). One
    // timeout has already spent the budget and leaked a worker that no cancellation can recall,
    // so the group must end there.
    //
    // Driven through the injected launcher rather than three real blocked workers: the old
    // version spent a 250 ms budget per interface and proved "never read" by inspecting a
    // ManualResetEventSlim the thread pool may not have reached yet - the pool dependence the
    // comment on BestReading_TimeoutAfterUnreadable_KeepsTheMinusTwo warns about. Launched is
    // the same evidence with no pool involvement. The group budget is far larger than the reads
    // so that only the timeout exit can end this scan.
    [Fact]
    public void BestReading_FirstTimeout_EndsTheGroup()
    {
        var launcher = new RecordingLauncher();
        var group = new IBatteryDevice[]
        {
            ScriptedDevice.TimesOut(Col(1)),
            ScriptedDevice.Answers(Col(2), 70),
            ScriptedDevice.Answers(Col(3), 70),
        };

        int best = AdaptivePoller.BestReading(
            group, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), launcher.Launch);

        Assert.Equal(-1, best);
        Assert.Equal(new[] { Col(1) }, launcher.Launched);
    }

    // The price of that exit: on the tick it fires, every interface behind the wedged one goes
    // unread. Discover re-derives the same paths in the same order every tick and nothing in the
    // poller is keyed by path, so without a rotating start a live interface behind a wedge is
    // never read at all and the device reads unavailable forever - the "only an interface that
    // never reports survives" outcome DeviceRegistry refuses to cause one layer up. Each tick
    // below builds fresh interface objects because that is what Discover does; only scanStart
    // survives a tick.
    //
    // The wedge is now scripted, not really blocked: the old version spent a real 2 s budget per
    // failing tick and parked three workers for the rest of the suite to assert an ordering
    // property. Same assertions, no wall clock, no leaked workers.
    [Fact]
    public void ReadDevice_WedgedFirst_ReachesTheLiveInterfaceAndStaysThere()
    {
        var scanStart = new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase);
        var perRead = TimeSpan.FromSeconds(5);
        var groupBudget = TimeSpan.FromMinutes(1);

        var tick1 = new RecordingLauncher();
        Assert.Equal(-1, AdaptivePoller.ReadDevice(
            "Magic Mouse",
            [ScriptedDevice.TimesOut(Col(1)), ScriptedDevice.Answers(Col(2), 72)],
            perRead, groupBudget, scanStart, null, tick1.Launch));
        Assert.Equal(new[] { Col(1) }, tick1.Launched);

        // Next tick: the rotation puts the interface the wedge hid first, so it is read.
        var tick2 = new RecordingLauncher();
        Assert.Equal(72, AdaptivePoller.ReadDevice(
            "Magic Mouse",
            [ScriptedDevice.TimesOut(Col(1)), ScriptedDevice.Answers(Col(2), 72)],
            perRead, groupBudget, scanStart, null, tick2.Launch));
        Assert.Equal(new[] { Col(2) }, tick2.Launched);

        // And it stays: a device that answered keeps the start it answered from, so the wedge is
        // never read again and its leaked worker is paid once, not per tick. Were the start to
        // return to 0 on success the device would report a level only every other tick, and
        // leak a worker on all the others.
        var tick3 = new RecordingLauncher();
        Assert.Equal(72, AdaptivePoller.ReadDevice(
            "Magic Mouse",
            [ScriptedDevice.TimesOut(Col(1)), ScriptedDevice.Answers(Col(2), 72)],
            perRead, groupBudget, scanStart, null, tick3.Launch));
        Assert.Equal(new[] { Col(2) }, tick3.Launched);
    }

    // A read that never answered establishes nothing about the battery report. -2 means the
    // report is not exposed, and it is the sole trigger for the elevated keyboard SDP patch offer
    // (TrayMenu.ShowFixKeyboard) and RepairPlanner rule 2d, so a wedged read reporting -2 would
    // tell the user their pairing record is broken on the evidence of a driver that hung. This
    // device would answer 70 if it ever got there.
    [Fact]
    public void BestReading_TimedOutRead_IsNotFound_NotReportNotExposed()
    {
        var wedged = NewBlockingDevice(70);

        int best = AdaptivePoller.BestReading([wedged], TimeSpan.FromMilliseconds(250));

        Assert.Equal(-1, best);
    }

    // Ending the group at the timeout must not throw away what the group already established: an
    // earlier interface answered "present, report not exposed", which is a diagnosis, and the
    // wedged interface added no information to overwrite it with.
    //
    // This test asserts an ORDERING property, and the real read path cannot tell "the device did
    // not answer" from "the thread pool did not schedule the read": with workers parked by the
    // wedged tests above it, the 250 ms budget this used to run on expired before the first
    // device's instant read even started, the group ended on a phantom timeout and best was -1.
    // Windows CI caught exactly that. The scripted launcher removes the pool from the question
    // entirely, which is the only fix that does not trade wall time for flakiness.
    [Fact]
    public void BestReading_TimeoutAfterUnreadable_KeepsTheMinusTwo()
    {
        var launcher = new RecordingLauncher();
        var group = new IBatteryDevice[]
        {
            ScriptedDevice.Answers(Col(1), -2),
            ScriptedDevice.TimesOut(Col(2)),
            ScriptedDevice.Answers(Col(3), 55),
        };

        int best = AdaptivePoller.BestReading(
            group, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), launcher.Launch);

        Assert.Equal(-2, best);
        Assert.Equal(new[] { Col(1), Col(2) }, launcher.Launched);
    }

    // A throw is not a timeout: it came back at once and parked nothing, so it buys no reason to
    // stop and the live interface behind it must still be read. Discovery deliberately keeps a
    // device's dead collections next to its live one, and the live one is often not first.
    [Fact]
    public void BestReading_ThrowingInterface_DoesNotEndTheGroup()
    {
        var throwing = new ThrowingDevice();
        var real = new CountingDevice(63);

        int best = AdaptivePoller.BestReading([throwing, real], TimeSpan.FromSeconds(30));

        Assert.Equal(63, best);
        Assert.Equal(1, throwing.Reads);
        Assert.Equal(1, real.Reads);
    }

    // F5. Every interface got the same per-read budget and there was no cumulative deadline, so a
    // group's worst case was interfaces.Count x budget: the 4-collection v3 mouse could hold a
    // poll cycle for 20 s with nothing timing out, which is 20 s in which POLL_SCHEDULED does not
    // fire and the tray shows the previous reading. The group budget bounds the scan itself. The
    // fourth interface here would answer a real percentage, so returning -1 is the proof that the
    // deadline stopped the scan and not some other exit.
    [Fact]
    public void BestReading_GroupBudgetSpent_StopsBeforeLaunchingTheNextInterface()
    {
        var launcher = new RecordingLauncher();
        var group = new IBatteryDevice[]
        {
            ScriptedDevice.Answers(Col(1), -1, seconds: 4),
            ScriptedDevice.Answers(Col(2), -1, seconds: 4),
            ScriptedDevice.Answers(Col(3), -1, seconds: 4),
            ScriptedDevice.Answers(Col(4), 61, seconds: 4),
        };

        int best = AdaptivePoller.BestReading(
            group, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), launcher.Launch);

        Assert.Equal(-1, best);
        // Two reads at 4 s have spent 8 s of a 5 s group budget. The deadline is checked before
        // the launch, not after it: an interface launched past the deadline costs its whole
        // per-read budget on top, which is the cost this bound exists to refuse.
        Assert.Equal(new[] { Col(1), Col(2) }, launcher.Launched);
    }

    // The deadline is a cost bound, not a verdict. A -2 already seen is a diagnosis (it is the
    // sole trigger for RepairPlanner rule 2d and the elevated keyboard SDP offer) and running out
    // of wall clock establishes nothing that could demote it to -1.
    [Fact]
    public void BestReading_GroupBudgetSpent_KeepsTheMinusTwoAlreadySeen()
    {
        var launcher = new RecordingLauncher();
        var group = new IBatteryDevice[]
        {
            ScriptedDevice.Answers(Col(1), -2, seconds: 3),
            ScriptedDevice.Answers(Col(2), -1, seconds: 3),
            ScriptedDevice.Answers(Col(3), -1, seconds: 3),
        };

        int best = AdaptivePoller.BestReading(
            group, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), launcher.Launch);

        Assert.Equal(-2, best);
        Assert.Equal(new[] { Col(1), Col(2) }, launcher.Launched);
    }

    // An unbounded group budget must mean unbounded, including TimeSpan.MaxValue: that is the
    // value the 2-arg overload stands for, and it is what the poll loop's other call sites keep
    // meaning. A deadline computed as "now + budget" overflows on MaxValue and would turn every
    // one of those call sites into a throw inside the cycle guard.
    [Fact]
    public void BestReading_UnboundedGroupBudget_ReadsEveryInterfaceHoweverLongTheyTake()
    {
        var launcher = new RecordingLauncher();
        var group = new IBatteryDevice[]
        {
            ScriptedDevice.Answers(Col(1), -1, seconds: 3600),
            ScriptedDevice.Answers(Col(2), -2, seconds: 3600),
            ScriptedDevice.Answers(Col(3), -1, seconds: 3600),
            ScriptedDevice.Answers(Col(4), 61, seconds: 3600),
        };

        int best = AdaptivePoller.BestReading(
            group, TimeSpan.FromSeconds(5), TimeSpan.MaxValue, launcher.Launch);

        Assert.Equal(61, best);
        Assert.Equal(new[] { Col(1), Col(2), Col(3), Col(4) }, launcher.Launched);

        // Same group, same meaning, through the overload the eight existing call sites use.
        var unreadable = new CountingDevice(-2);
        var live = new CountingDevice(61);
        Assert.Equal(61, AdaptivePoller.BestReading(
            [new CountingDevice(-1), unreadable, new CountingDevice(-1), live],
            TimeSpan.FromSeconds(30)));
        Assert.Equal(1, unreadable.Reads);
        Assert.Equal(1, live.Reads);
    }

    // F6. Rotation is for a scan that learned nothing. -2 is not nothing: it says the interface
    // answered and the battery report is not exposed, and it is the only input to RepairPlanner
    // rule 2d and to the elevated keyboard SDP patch offer. Rotating on it hands the next tick's
    // first slot to the wedged interface, whose timeout ends that scan at -1 before the -2
    // interface is ever reached - so the sentinel survives only every other tick and the repair
    // offer flickers with it. The device here is stable across all four ticks; only the poller's
    // own state could change the answer.
    [Fact]
    public void ReadDevice_MinusTwoGroup_DoesNotRotateAndKeepsTheSentinelEveryTick()
    {
        var scanStart = new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase);

        for (int tick = 0; tick < 4; tick++)
        {
            var launcher = new RecordingLauncher();
            int best = AdaptivePoller.ReadDevice(
                "Magic Mouse",
                [ScriptedDevice.Answers(Col(1), -2), ScriptedDevice.TimesOut(Col(2))],
                TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), scanStart, null, launcher.Launch);

            Assert.Equal(-2, best);
            Assert.Equal(new[] { Col(1), Col(2) }, launcher.Launched);
            Assert.Equal(0, StartOf(scanStart, "Magic Mouse"));
        }
    }

    // The other two outcomes, for contrast: -1 learned nothing and must move on, a real
    // percentage must keep the start that produced it.
    [Fact]
    public void ReadDevice_RotatesOnMinusOne_AndStaysPutOnARealReading()
    {
        var scanStart = new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase);
        var perRead = TimeSpan.FromSeconds(5);
        var budget = TimeSpan.FromMinutes(1);

        Assert.Equal(-1, AdaptivePoller.ReadDevice(
            "Magic Mouse",
            [ScriptedDevice.Answers(Col(1), -1), ScriptedDevice.Answers(Col(2), -1)],
            perRead, budget, scanStart, null, new RecordingLauncher().Launch));
        Assert.Equal(1, StartOf(scanStart, "Magic Mouse"));

        Assert.Equal(44, AdaptivePoller.ReadDevice(
            "Magic Mouse",
            [ScriptedDevice.Answers(Col(1), -1), ScriptedDevice.Answers(Col(2), 44)],
            perRead, budget, scanStart, null, new RecordingLauncher().Launch));
        Assert.Equal(1, StartOf(scanStart, "Magic Mouse"));
    }

    // F13. PollLoop mints -3 ("Battery unavailable") after three consecutive -1 ticks, but
    // rotation needs up to interfaces.Count ticks to put a live interface first. A 4-collection
    // device whose live interface sorts last therefore showed the user "Battery unavailable" on
    // tick 3 and a real percentage on tick 4. Escalation must wait until the rotation has had its
    // full turn, which is what a completed rotation means.
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 3, false)]   // not enough failures, however many turns rotation has had
    [InlineData(3, 0, false)]   // enough failures, but some interface has still never been read
    [InlineData(3, 1, true)]
    [InlineData(7, 2, true)]
    public void ShouldEscalate_NeedsThreeFailuresAndACompletedRotation(
        int consecutiveFailures, int rotationsSinceLastReading, bool expected)
    {
        Assert.Equal(expected, AdaptivePoller.ShouldEscalate(
            consecutiveFailures, rotationsSinceLastReading));
    }

    // A rotation is completed when the start index wraps past the end - that is exactly the point
    // at which every interface has had the first slot once. Counting ticks instead would escalate
    // early on a 4-collection device and late on a 2-collection one.
    [Fact]
    public void ReadDevice_CountsARotationOnlyWhenTheStartWrapsPastTheEnd()
    {
        var scanStart = new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase);
        var expectedStarts = new[] { 1, 2, 0, 1 };
        var expectedRotations = new[] { 0, 0, 1, 1 };

        for (int tick = 0; tick < 4; tick++)
        {
            Assert.Equal(-1, AdaptivePoller.ReadDevice(
                "Magic Mouse",
                [ScriptedDevice.Answers(Col(1), -1), ScriptedDevice.Answers(Col(2), -1),
                 ScriptedDevice.Answers(Col(3), -1)],
                TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), scanStart,
                null, new RecordingLauncher().Launch));

            Assert.Equal(expectedStarts[tick], scanStart["Magic Mouse"].Start);
            Assert.Equal(expectedRotations[tick], scanStart["Magic Mouse"].Rotations);
        }
    }

    // A device with one interface completes a rotation on every failing tick, because that one
    // interface has had its turn every time. Nothing is owed to rotation there, so escalation is
    // governed by the failure count alone - the pre-rotation behaviour, preserved exactly where
    // it was already right.
    [Fact]
    public void ReadDevice_SingleInterface_CompletesARotationEveryFailingTick()
    {
        var scanStart = new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase);

        for (int tick = 1; tick <= 3; tick++)
        {
            Assert.Equal(-1, AdaptivePoller.ReadDevice(
                "Magic Mouse", [ScriptedDevice.Answers(Col(1), -1)],
                TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), scanStart,
                null, new RecordingLauncher().Launch));

            Assert.Equal(0, scanStart["Magic Mouse"].Start);
            Assert.Equal(tick, scanStart["Magic Mouse"].Rotations);
        }

        Assert.True(AdaptivePoller.ShouldEscalate(3, scanStart["Magic Mouse"].Rotations));
    }

    // A real reading proves the current start works, so the rotation debt is paid: the next
    // outage starts its own count. Without the reset a device that fails three ticks, recovers
    // for a week and fails once would escalate to "Battery unavailable" on that single tick.
    [Fact]
    public void ReadDevice_RealReading_ResetsTheRotationCount()
    {
        var scanStart = new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase);
        var perRead = TimeSpan.FromSeconds(5);
        var budget = TimeSpan.FromMinutes(1);

        for (int tick = 0; tick < 2; tick++)
            Assert.Equal(-1, AdaptivePoller.ReadDevice(
                "Magic Mouse",
                [ScriptedDevice.Answers(Col(1), -1), ScriptedDevice.Answers(Col(2), -1)],
                perRead, budget, scanStart, null, new RecordingLauncher().Launch));
        Assert.Equal(1, scanStart["Magic Mouse"].Rotations);

        Assert.Equal(58, AdaptivePoller.ReadDevice(
            "Magic Mouse",
            [ScriptedDevice.Answers(Col(1), 58), ScriptedDevice.Answers(Col(2), -1)],
            perRead, budget, scanStart, null, new RecordingLauncher().Launch));

        Assert.Equal(0, scanStart["Magic Mouse"].Rotations);
        Assert.False(AdaptivePoller.ShouldEscalate(3, scanStart["Magic Mouse"].Rotations));
    }

    // F7. A timeout is not a free failure: Task.Run cannot cancel a blocking HID IOCTL, so the
    // abandoned read keeps a thread-pool worker until the driver releases it, which on a wedged
    // interface is never. Rotation guarantees a never-answering group comes back to each wedged
    // interface forever, so without a per-path memory the tray leaks one worker per wedged
    // interface per tick, for the lifetime of the process.
    [Fact]
    public void WedgeTracker_ThreeConsecutiveTimeouts_SkipsThePath()
    {
        var wedges = new WedgeTracker();

        for (int strike = 1; strike < WedgeTracker.WedgeTimeoutStrikes; strike++)
        {
            wedges.RecordTimeout(Col(1));
            // Below the strike count the path is still read: one or two timeouts can be a busy
            // device, and refusing to read it would be the "only an interface that never reports
            // survives" outcome in reverse.
            Assert.False(wedges.ShouldSkip(Col(1)));
        }

        wedges.RecordTimeout(Col(1));
        Assert.True(wedges.ShouldSkip(Col(1)));
        // The memory is per path, not per device: the other collections of the same mouse are
        // exactly what rotation is trying to reach.
        Assert.False(wedges.ShouldSkip(Col(2)));
    }

    // The skip is a backoff, not a blacklist. A wedged interface is routinely a device that was
    // asleep or mid-reconnect, and a permanent skip would mean a reboot is the only way to read
    // it again. The window must therefore be bounded - and longer than one tick, or the backoff
    // buys nothing.
    [Fact]
    public void WedgeTracker_BackoffWindowElapses_RetriesThePath()
    {
        var wedges = new WedgeTracker();
        for (int strike = 0; strike < WedgeTracker.WedgeTimeoutStrikes; strike++)
            wedges.RecordTimeout(Col(1));

        wedges.Tick();
        Assert.True(wedges.ShouldSkip(Col(1)));

        for (int tick = 0; tick < WedgeTracker.WedgeBackoffTicks; tick++)
            wedges.Tick();
        Assert.False(wedges.ShouldSkip(Col(1)));
    }

    // An interface that answered is not wedged, whatever it did before: the strikes count
    // consecutive timeouts, so one answer clears them. Otherwise a flaky interface accumulates
    // strikes across hours of successful reads and is eventually skipped while working.
    [Fact]
    public void WedgeTracker_Answer_ClearsTheStrikesImmediately()
    {
        var wedges = new WedgeTracker();
        for (int strike = 0; strike < WedgeTracker.WedgeTimeoutStrikes; strike++)
            wedges.RecordTimeout(Col(1));
        Assert.True(wedges.ShouldSkip(Col(1)));

        wedges.RecordAnswer(Col(1));

        Assert.False(wedges.ShouldSkip(Col(1)));
        // Cleared, not merely un-skipped: the next outage starts from zero strikes.
        wedges.RecordTimeout(Col(1));
        Assert.False(wedges.ShouldSkip(Col(1)));
    }

    // Discovery re-derives paths every tick and a USB-C reconnect changes them, so the tracker is
    // keyed by something that disappears. Pruning to the live set is what keeps it from growing
    // for the life of the process, and a path that comes back is a different physical state that
    // has not earned a skip.
    [Fact]
    public void WedgeTracker_Prune_DropsPathsNoLongerDiscovered()
    {
        var wedges = new WedgeTracker();
        for (int strike = 0; strike < WedgeTracker.WedgeTimeoutStrikes; strike++)
        {
            wedges.RecordTimeout(Col(1));
            wedges.RecordTimeout(Col(2));
        }

        wedges.Prune([Col(2)]);

        Assert.False(wedges.ShouldSkip(Col(1)));
        Assert.True(wedges.ShouldSkip(Col(2)));
    }

    // The whole point of the tracker: a skipped path costs nothing, so the scan reaches the
    // interface behind it in the same tick instead of ending on the wedge's timeout. Without the
    // skip this group returns -1 and parks a worker, every tick, forever.
    [Fact]
    public void ReadDevice_SkippedPath_IsNotLaunchedAndTheLiveInterfaceIsReached()
    {
        var wedges = new WedgeTracker();
        for (int strike = 0; strike < WedgeTracker.WedgeTimeoutStrikes; strike++)
            wedges.RecordTimeout(Col(1));

        var launcher = new RecordingLauncher();
        int best = AdaptivePoller.ReadDevice(
            "Magic Mouse",
            [ScriptedDevice.TimesOut(Col(1)), ScriptedDevice.Answers(Col(2), 64)],
            TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1),
            new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase),
            wedges, launcher.Launch);

        Assert.Equal(64, best);
        Assert.Equal(new[] { Col(2) }, launcher.Launched);
    }

    // The strikes have to come from the read path itself, or the tracker never fills and the
    // skip never happens in production. Tick() is the poll cycle's, not ReadDevice's: the backoff
    // is measured in poll cycles.
    [Fact]
    public void ReadDevice_RepeatedTimeoutsOnAPath_StopLaunchingIt()
    {
        var wedges = new WedgeTracker();
        var scanStart = new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase);

        for (int tick = 0; tick < WedgeTracker.WedgeTimeoutStrikes; tick++)
        {
            var launcher = new RecordingLauncher();
            Assert.Equal(-1, AdaptivePoller.ReadDevice(
                "Magic Mouse", [ScriptedDevice.TimesOut(Col(1))],
                TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), scanStart, wedges, launcher.Launch));
            Assert.Equal(new[] { Col(1) }, launcher.Launched);
            wedges.Tick();
        }

        var after = new RecordingLauncher();
        Assert.Equal(-1, AdaptivePoller.ReadDevice(
            "Magic Mouse", [ScriptedDevice.TimesOut(Col(1))],
            TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), scanStart, wedges, after.Launch));
        Assert.Empty(after.Launched);
    }

    // Every path skipped is the expected steady state for a device that is present but wedged on
    // all collections. It must read as -1 (which PollLoop counts toward -3) and not as an empty
    // sequence, an index out of range, or a division by zero on a group that is "empty" only
    // after filtering.
    [Fact]
    public void ReadDevice_EveryPathSkipped_ReturnsMinusOneWithoutReading()
    {
        var wedges = new WedgeTracker();
        for (int strike = 0; strike < WedgeTracker.WedgeTimeoutStrikes; strike++)
        {
            wedges.RecordTimeout(Col(1));
            wedges.RecordTimeout(Col(2));
        }

        var launcher = new RecordingLauncher();
        int best = AdaptivePoller.ReadDevice(
            "Magic Mouse",
            [ScriptedDevice.TimesOut(Col(1)), ScriptedDevice.TimesOut(Col(2))],
            TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1),
            new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase),
            wedges, launcher.Launch);

        Assert.Equal(-1, best);
        Assert.Empty(launcher.Launched);
    }

    // F11. The scan state is per device name and shares one dictionary with every other device,
    // which only matters when more than one device is failing - a keyboard and a mouse both
    // wedged, which is the normal state after a USB-C reconnect. A single shared index would make
    // each device's rotation depend on how many other devices failed before it in the cycle, so
    // neither would get a reliable turn at its own interfaces.
    [Fact]
    public void ReadDevice_TwoDeviceNames_KeepIndependentStartIndices()
    {
        var scanStart = new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase);
        var perRead = TimeSpan.FromSeconds(5);
        var budget = TimeSpan.FromMinutes(1);
        IReadOnlyList<IBatteryDevice> Mouse() =>
            [ScriptedDevice.Answers(Col(1), -1), ScriptedDevice.Answers(Col(2), -1)];
        IReadOnlyList<IBatteryDevice> Keyboard() =>
            [ScriptedDevice.Answers(Col(1), -1), ScriptedDevice.Answers(Col(2), -1),
             ScriptedDevice.Answers(Col(3), -1)];

        for (int cycle = 0; cycle < 2; cycle++)
        {
            AdaptivePoller.ReadDevice("Magic Mouse", Mouse(), perRead, budget, scanStart,
                null, new RecordingLauncher().Launch);
            AdaptivePoller.ReadDevice("Magic Keyboard", Keyboard(), perRead, budget, scanStart,
                null, new RecordingLauncher().Launch);
        }

        // The mouse has two interfaces and has wrapped; the keyboard has three and has not.
        Assert.Equal(0, scanStart["Magic Mouse"].Start);
        Assert.Equal(1, scanStart["Magic Mouse"].Rotations);
        Assert.Equal(2, scanStart["Magic Keyboard"].Start);
        Assert.Equal(0, scanStart["Magic Keyboard"].Rotations);
    }

    // The stored start outlives the interface list it was computed from: a v3 mouse that drops
    // from four collections to one while unplugged leaves a start of 2 behind, and the next tick
    // must read the interface that is actually there rather than index past the end. This is the
    // case the second reduction on the read side exists for.
    [Fact]
    public void ReadDevice_InterfaceListShrankSinceLastTick_ReadsAValidInterface()
    {
        var scanStart = new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase)
        {
            ["Magic Mouse"] = new ScanState(2, 0),
        };
        var launcher = new RecordingLauncher();

        int best = AdaptivePoller.ReadDevice(
            "Magic Mouse", [ScriptedDevice.Answers(Col(1), 55)],
            TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), scanStart, null, launcher.Launch);

        Assert.Equal(55, best);
        Assert.Equal(new[] { Col(1) }, launcher.Launched);
    }

    // F1. RefreshNow used to start a whole new PollLoop while the previous one was still inside a
    // synchronous cycle that can last the full device budget. The two loops then raced
    // _consecutiveFailures, _lastSeen and the scan state, so a refresh during a slow cycle could
    // double-count failures into a spurious -3 or rotate a device twice in one tick. One loop,
    // woken by a signal, is the only arrangement in which that state has a single writer. Five
    // refreshes is a user double-clicking Refresh Now plus the repair paths that call it.
    [Fact]
    public void RefreshNow_AfterStart_DoesNotStartASecondPollLoop()
    {
        using var poller = new AdaptivePoller(Config.Load(_path));
        poller.Start();

        for (int i = 0; i < 5; i++)
            poller.RefreshNow();

        Assert.Equal(1, poller.LoopStarts);
    }

    // Dispose races every menu item that calls RefreshNow: tray shutdown disposes the poller
    // while a queued click or a finished repair is still on its way in. RefreshNow previously
    // cancelled a CancellationTokenSource it had just disposed, which throws
    // ObjectDisposedException on whichever thread got there - a repair thread or the UI thread.
    [Fact]
    public void RefreshNow_AfterDispose_IsAHarmlessNoOp()
    {
        var poller = new AdaptivePoller(Config.Load(_path));
        poller.Start();
        poller.Dispose();

        poller.RefreshNow();
        poller.RefreshNow();

        Assert.Equal(1, poller.LoopStarts);
    }

    // F2. The signal replaces the second PollLoop, so the poll loop's correctness is now this
    // object's. A signal must be remembered until the loop next waits (a refresh arriving while
    // the loop is mid-cycle must not be lost), and it must auto-reset so one refresh buys exactly
    // one extra cycle rather than a permanent 0 s poll.
    [Fact]
    public async Task RefreshSignal_SignalBeforeWait_WakesImmediatelyAndAutoResets()
    {
        var signal = new RefreshSignal();
        signal.Signal();
        Assert.True(signal.IsSignalled);

        // A zero timeout cannot wait, so true here can only come from the stored signal.
        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));

        Assert.False(signal.IsSignalled);
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    // A user clicking Refresh Now three times wants one refresh, not three queued cycles of HID
    // reads on a device that is already being read.
    [Fact]
    public async Task RefreshSignal_RepeatedSignals_CoalesceToOneWake()
    {
        var signal = new RefreshSignal();
        signal.Signal();
        signal.Signal();
        signal.Signal();

        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    // The ordinary case: the interval expired and nobody asked for anything. The loop must be
    // able to tell that apart from a refresh, because a refresh is the one case that must not
    // reset the drain-rate schedule.
    [Fact]
    public async Task RefreshSignal_NoSignal_ReturnsFalseAtTheTimeout()
    {
        var signal = new RefreshSignal();
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(signal.IsSignalled);
    }

    // A refresh arriving during the wait is the whole feature: the battery interval is up to 24 h
    // and Refresh Now must not wait for it. The wait is started first and signalled second, so
    // only a wait that observes a later signal can complete.
    [Fact]
    public async Task RefreshSignal_SignalDuringWait_WakesTheWaiter()
    {
        var signal = new RefreshSignal();
        var waiting = signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        signal.Signal();

        Assert.True(await waiting);
    }

    // Dispose cancels the token while the loop is parked on a 24 h wait. The wait must end
    // quietly: PollLoop's own cancellation path is what stops it, and an OperationCanceledException
    // thrown out of the wait would escape as an unobserved task fault instead.
    [Fact]
    public async Task RefreshSignal_CancelledToken_EndsTheWaitWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var signal = new RefreshSignal();

        Assert.False(await signal.WaitAsync(TimeSpan.FromMinutes(5), cts.Token));
    }

    // A device's start index before it has ever been stored is 0: the first tick reads discovery
    // order, which is what every call site assumes.
    static int StartOf(Dictionary<string, ScanState> scanStart, string name) =>
        scanStart.TryGetValue(name, out var state) ? state.Start : 0;

    // Distinct, realistic HID interface paths - the key the wedge tracker and the scripted
    // launcher both work in.
    static string Col(int n) => $@"\\?\hid#vid_05ac&pid_0323&col{n:00}#scripted";

    BlockingDevice NewBlockingDevice(int pct)
    {
        var device = new BlockingDevice(pct);
        _blocked.Add(device);
        return device;
    }

    // One HID interface whose read does not answer inside the budget, standing in for the
    // uncancellable HidD_GetFeature / HidD_GetInputReport call on a wedged interface. Only the
    // one test that pins what the REAL read path makes of a timeout still needs it; everything
    // else drives the exit through the injected launcher.
    sealed class BlockingDevice : IBatteryDevice
    {
        readonly int _pct;
        readonly ManualResetEventSlim _release = new();

        internal BlockingDevice(int pct) => _pct = pct;

        public string DeviceName => "Magic Mouse";
        public string Pid => "0323";
        public string DevicePath { get; init; } = @"\\?\hid#vid_05ac&pid_0323&col02#blocking";
        public DeviceKind Kind => DeviceKind.MagicMouseV3;

        public int GetBatteryPercent()
        {
            _release.Wait();
            return _pct;
        }

        internal void Release() => _release.Set();
    }

    // One HID interface whose read fails immediately - an opened handle that the driver rejects.
    sealed class ThrowingDevice : IBatteryDevice
    {
        internal int Reads { get; private set; }

        public string DeviceName => "Magic Mouse";
        public string Pid => "0323";
        public string DevicePath { get; init; } = @"\\?\hid#vid_05ac&pid_0323&col02#throwing";
        public DeviceKind Kind => DeviceKind.MagicMouseV3;

        public int GetBatteryPercent()
        {
            Reads++;
            throw new IOException("HidD_GetFeature failed");
        }
    }

    // One HID interface of one device, returning a fixed reading and counting how often
    // it was read, so a test can pin which interfaces BestReading actually touched.
    sealed class CountingDevice : IBatteryDevice
    {
        readonly int _pct;

        internal CountingDevice(int pct) => _pct = pct;

        internal int Reads { get; private set; }

        public string DeviceName => "Magic Mouse";
        public string Pid => "0323";
        public string DevicePath { get; init; } = @"\\?\hid#vid_05ac&pid_0323&col02#counting";
        public DeviceKind Kind => DeviceKind.MagicMouseV3;

        public int GetBatteryPercent()
        {
            Reads++;
            return _pct;
        }
    }

    // One HID interface whose read is scripted rather than performed. The injected ReadLauncher
    // returns the script verbatim, so a test can model a 4 s read, or one that never answers,
    // without spending any wall clock and without parking a thread-pool worker that the rest of
    // the suite then runs short of.
    sealed class ScriptedDevice : IBatteryDevice
    {
        ScriptedDevice(string path, GuardedRead result)
        {
            DevicePath = path;
            Result = result;
        }

        // An interface that answers - with a percentage or with a failure sentinel - after the
        // given elapsed time.
        internal static ScriptedDevice Answers(string path, int pct, double seconds = 0) =>
            new(path, new GuardedRead(pct, false, TimeSpan.FromSeconds(seconds)));

        // An interface whose read is abandoned at the budget: -1, because a read that never
        // answered has established nothing about the battery report, and TimedOut, because it
        // spent the whole budget and leaked the worker.
        internal static ScriptedDevice TimesOut(string path, double seconds = 5) =>
            new(path, new GuardedRead(-1, true, TimeSpan.FromSeconds(seconds)));

        internal GuardedRead Result { get; }

        public string DeviceName { get; init; } = "Magic Mouse";
        public string Pid => "0323";
        public string DevicePath { get; }
        public DeviceKind Kind => DeviceKind.MagicMouseV3;

        public int GetBatteryPercent() => throw new InvalidOperationException(
            $"{DevicePath} was read directly; these tests run through the injected launcher");
    }

    // Stands in for the guarded read. Launched, in order, is the evidence that an interface past
    // the group deadline, behind a timeout, or under a wedge skip was never touched at all -
    // which is the cost every one of those exits exists to avoid, and which a per-device read
    // count cannot show because the read never happens.
    sealed class RecordingLauncher
    {
        internal List<string> Launched { get; } = new();

        internal GuardedRead Launch(IBatteryDevice device, TimeSpan budget)
        {
            Launched.Add(device.DevicePath);
            return ((ScriptedDevice)device).Result;
        }
    }
}
