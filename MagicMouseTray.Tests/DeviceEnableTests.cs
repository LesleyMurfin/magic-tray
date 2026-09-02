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
    public void Matches_BthledeviceKeyboard_RequiresAppleVid()
    {
        const string ble =
            @"BTHLEDEVICE\{00001812-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&0239\8&abc";
        Assert.True(DeviceEnable.MatchesInstance(ble, "0239"));
        Assert.False(DeviceEnable.MatchesInstance(ble, "030d"));
        Assert.False(DeviceEnable.MatchesInstance(
            @"BTHLEDEVICE\{00001812-0000-1000-8000-00805f9b34fb}_VID&0000045e_PID&0239\8&abc",
            "0239"));
    }

    [Fact]
    public void DisableScript_QuotesInstanceId_AndFailsIfNone()
    {
        var script = DeviceEnable.BuildScript("030d", enable: false);
        Assert.Contains("pnputil.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"/$verb\" \"$id\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/$verb $id", script, StringComparison.Ordinal);
        Assert.Contains("if ($ids.Count -eq 0)", script, StringComparison.Ordinal);
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
        Assert.Contains("BTHLEDEVICE", script, StringComparison.Ordinal);
        Assert.Contains("030d", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/enable-device", script, StringComparison.Ordinal);
        foreach (var name in DeviceEnable.ForbiddenNames)
            Assert.DoesNotContain(name, script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisableScript_ChildrenBeforeRadio()
    {
        var script = DeviceEnable.BuildScript("030d", enable: false);
        var hid = script.IndexOf("'HID'", StringComparison.Ordinal);
        var bt = script.IndexOf("'BTHENUM'", StringComparison.Ordinal);
        Assert.True(hid >= 0 && bt > hid);
    }

    [Fact]
    public void EnableScript_UsesEnableDevice_RadioBeforeChildren()
    {
        var script = DeviceEnable.BuildScript("030d", enable: true);
        Assert.Contains("$verb = 'enable-device'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$verb = 'disable-device'", script, StringComparison.Ordinal);
        var bt = script.IndexOf("'BTHENUM'", StringComparison.Ordinal);
        var hid = script.LastIndexOf("'HID'", StringComparison.Ordinal);
        Assert.True(bt >= 0 && hid > bt);
    }
}
