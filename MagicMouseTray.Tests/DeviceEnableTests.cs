// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DeviceEnableTests
{
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
    public void Matches_Bthenum030d_RequiresAppleVid()
    {
        const string bt =
            @"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&030d\8&def";
        Assert.True(DeviceEnable.MatchesInstance(bt, "030d"));
        Assert.False(DeviceEnable.MatchesInstance(
            @"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0000045e_PID&030d\8&def",
            "030d"));
    }

    [Fact]
    public void DisableScript_PnputilOnly_NoDriverInstallers()
    {
        var script = DeviceEnable.BuildScript("030d", enable: false);
        Assert.Contains("pnputil.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/disable-device", script, StringComparison.Ordinal);
        Assert.Contains("030d", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/enable-device", script, StringComparison.Ordinal);
        foreach (var name in DeviceEnable.ForbiddenNames)
            Assert.DoesNotContain(name, script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnableScript_UsesEnableDevice()
    {
        var script = DeviceEnable.BuildScript("030d", enable: true);
        Assert.Contains("/enable-device", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/disable-device", script, StringComparison.Ordinal);
    }
}
