// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class MouseBatteryDeviceTests
{
    [Fact]
    public void ParseRid90Percent_UsesBuf2_MatchesLiveHidProbe()
    {
        Assert.Equal(47, MouseBatteryDevice.ParseRid90Percent([0x90, 0x00, 47]));
        Assert.Equal(47, MouseBatteryDevice.ParseRid90Percent([0x90, 0x00, 47, 0x00, 0x00]));
        Assert.Equal(46, MouseBatteryDevice.ParseRid90Percent([0x90, 0x04, 0x2E])); // live mm-hid-probe
        Assert.Equal(43, MouseBatteryDevice.ParseRid90Percent([0x90, 0x04, 43]));   // live debug.log
        Assert.Null(MouseBatteryDevice.ParseRid90Percent([0x47, 60]));             // Feature 0x47 is not the mouse path
        Assert.Null(MouseBatteryDevice.ParseRid90Percent([0x90, 0x00]));
        Assert.Null(MouseBatteryDevice.ParseRid90Percent([0x90, 0x00, 101]));
        Assert.Null(MouseBatteryDevice.ParseRid90Percent([0x12, 0x00, 47]));
    }

    [Theory]
    [InlineData(@"\\?\bthhfenum#{0000111e-...}#7&iphone&hands-free")]
    [InlineData(@"\\?\BTHHFENUM\Dev_AABBCCDDEEFF")]
    [InlineData(@"\\?\hid#handsfree#1")]
    [InlineData(@"\\?\hid#vid_05ac&pid_12a8#iphone")]
    public void HandsFreeOrIphone_IsRejected(string path)
    {
        Assert.True(DeviceRegistry.IsHandsFreeOrIphonePath(path));
        Assert.Empty(DeviceRegistry.DiscoverFromPaths([path]));
    }

    [Fact]
    public void V3_OnlyCol02_IsBatteryCollection()
    {
        const string col02 = @"\\?\hid#vid_004c&pid_0323&col02#7&abc&0000#{4d1e55b2}";
        const string col01 = @"\\?\hid#vid_004c&pid_0323&col01#7&abc&0000#{4d1e55b2}";
        const string btCol02 = @"\\?\hid#{00001124-...}_vid&0001004c_pid&0323&col02#9&x";

        Assert.True(DeviceRegistry.Is0323BatteryCollectionPath(col02));
        Assert.True(DeviceRegistry.Is0323BatteryCollectionPath(btCol02));
        Assert.False(DeviceRegistry.Is0323BatteryCollectionPath(col01));
        Assert.False(DeviceRegistry.IsHandsFreeOrIphonePath(col02));
    }

    [Fact]
    public void Discover_0323_SkipsCol01AndHandsFree()
    {
        var paths = new[]
        {
            @"\\?\hid#vid_004c&pid_0323&col01#7&ptr#{4d1e55b2}",
            @"\\?\bthhfenum#iphone-hands-free",
            @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323&col02#9&batt",
        };
        var found = DeviceRegistry.DiscoverFromPaths(paths);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicMouseV3, found[0].Kind);
        Assert.Equal("0323", found[0].Pid);
        Assert.Equal("Magic Mouse 2024", found[0].DeviceName);
    }

    [Theory]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&0269#9&classic")]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0269#9&ble")]
    [InlineData(@"\\?\hid#vid_05ac&pid_0269#7&usb")]
    public void Discover_0269_IsMagicMouseV2_NotHandsFree(string path)
    {
        Assert.False(DeviceRegistry.IsHandsFreeOrIphonePath(path));
        var found = DeviceRegistry.DiscoverFromPaths(
            [path, @"\\?\bthhfenum#iphone-hands-free"]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicMouseV2, found[0].Kind);
        Assert.Equal("0269", found[0].Pid);
        Assert.Equal("Magic Mouse v2", found[0].DeviceName);
    }

    [Theory]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&0310#9&bt")]
    [InlineData(@"\\?\hid#vid_05ac&pid_0310#7&usb")]
    public void Discover_0310_IsAppleWirelessMouse(string path)
    {
        var found = DeviceRegistry.DiscoverFromPaths([path]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicMouseV1, found[0].Kind);
        Assert.Equal("0310", found[0].Pid);
        Assert.Equal("Apple Wireless Mouse", found[0].DeviceName);
    }

    [Theory]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&030d#9&bt")]
    [InlineData(@"\\?\hid#vid_05ac&pid_030d#7&usb")]
    public void Discover_030D_IsMagicMouseV1(string path)
    {
        var found = DeviceRegistry.DiscoverFromPaths([path]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicMouseV1, found[0].Kind);
        Assert.Equal("030d", found[0].Pid);
        Assert.Equal("Magic Mouse v1", found[0].DeviceName);
    }

    [Theory]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&030e#9&bt")]
    [InlineData(@"\\?\hid#vid_05ac&pid_030e#7&usb")]
    public void Discover_030E_IsMagicTrackpadV1(string path)
    {
        var found = DeviceRegistry.DiscoverFromPaths([path]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicTrackpadV1, found[0].Kind);
        Assert.Equal("030e", found[0].Pid);
        Assert.Equal("Magic Trackpad", found[0].DeviceName);
    }

    [Fact]
    public void EveryKnownMousePid_HasUsbVid05acRow()
    {
        var pids = MouseBatteryDevice.KnownMice
            .Select(m => m.PidPattern[^4..].ToUpperInvariant())
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        var usb = MouseBatteryDevice.KnownMice
            .Where(m => m.VidPattern.Equals("VID_05AC", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.PidPattern[^4..].ToUpperInvariant())
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        Assert.Equal(pids, usb);
    }

    [Fact]
    public void Logitech_StaysGated_UnlessThirdPartyEnabled()
    {
        const string path = @"\\?\hid#vid_046d&pid_b023#7&direct";
        Assert.Empty(DeviceRegistry.DiscoverFromPaths([path]));
        var on = DeviceRegistry.DiscoverFromPaths([path], enableThirdParty: true);
        Assert.Single(on);
        Assert.Equal(DeviceKind.LogitechMouse, on[0].Kind);
    }

    [Theory]
    [InlineData("030e", "Magic Trackpad", DeviceKind.MagicTrackpadV1)]
    [InlineData("0265", "Magic Trackpad 2", DeviceKind.MagicTrackpadV2)]
    [InlineData("0324", "Magic Trackpad 2024", DeviceKind.MagicTrackpadV3)]
    public void TryKnownMouse_TrackpadPids(string pid, string expectedName, DeviceKind expectedKind)
    {
        Assert.True(MouseBatteryDevice.TryKnownMouse(pid, out var name, out var kind));
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedKind, kind);
    }

}
