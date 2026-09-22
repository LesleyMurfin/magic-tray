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
    [Fact]
    public void BestReading_FirstTimeout_EndsTheGroup()
    {
        var wedged = NewBlockingDevice(70);
        var second = NewBlockingDevice(70);
        var third = NewBlockingDevice(70);

        int best = AdaptivePoller.BestReading(
            [wedged, second, third], TimeSpan.FromMilliseconds(250));

        Assert.Equal(-1, best);
        // The abandoned read is still in flight when BestReading returns, so wait for its entry
        // rather than racing the thread pool's scheduling. The later interfaces would each have
        // been waited on for the full budget, so an un-entered one really was never read.
        Assert.True(wedged.Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(second.Entered.IsSet);
        Assert.False(third.Entered.IsSet);
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
    [Fact]
    public void BestReading_TimeoutAfterUnreadable_KeepsTheMinusTwo()
    {
        var present = new CountingDevice(-2);
        var wedged = NewBlockingDevice(70);
        var later = new CountingDevice(55);

        int best = AdaptivePoller.BestReading(
            [present, wedged, later], TimeSpan.FromMilliseconds(250));

        Assert.Equal(-2, best);
        Assert.Equal(1, present.Reads);
        Assert.Equal(0, later.Reads);
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

    BlockingDevice NewBlockingDevice(int pct)
    {
        var device = new BlockingDevice(pct);
        _blocked.Add(device);
        return device;
    }

    // One HID interface whose read does not answer inside the budget, standing in for the
    // uncancellable HidD_GetFeature / HidD_GetInputReport call on a wedged interface.
    sealed class BlockingDevice : IBatteryDevice
    {
        readonly int _pct;
        readonly ManualResetEventSlim _release = new();

        internal BlockingDevice(int pct) => _pct = pct;

        // Set on the thread-pool thread the instant the read starts, so a test can prove the
        // interface was touched without depending on how fast the pool schedules it.
        internal ManualResetEventSlim Entered { get; } = new();

        public string DeviceName => "Magic Mouse";
        public string Pid => "0323";
        public DeviceKind Kind => DeviceKind.MagicMouseV3;

        public int GetBatteryPercent()
        {
            Entered.Set();
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
        public DeviceKind Kind => DeviceKind.MagicMouseV3;

        public int GetBatteryPercent()
        {
            Reads++;
            return _pct;
        }
    }
}
