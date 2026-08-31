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
        Assert.Null(MouseBatteryDevice.ParseRid90Percent([0x47, 60]));
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
}
