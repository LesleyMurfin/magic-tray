// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class AdaptivePollerTests : IDisposable
{
    readonly string _dir;
    readonly string _path;

    public AdaptivePollerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mm-tray-poll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.ini");
    }

    public void Dispose()
    {
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
}
