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

    [Fact]
    public void DisableScript_QuotesInstanceId_WalksAllEnum_CatalogVids()
    {
        var script = DeviceEnable.BuildScript("030d", enable: false);
        Assert.Contains("pnputil.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"/$verb\" \"$id\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/$verb $id", script, StringComparison.Ordinal);
        Assert.Contains("if ($ids.Count -eq 0)", script, StringComparison.Ordinal);
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
        Assert.Contains("GetSubKeyNames()", script, StringComparison.Ordinal);
        Assert.Contains("000205AC", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VID_05AC", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("030d", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/enable-device", script, StringComparison.Ordinal);
        foreach (var name in DeviceEnable.ForbiddenNames)
            Assert.DoesNotContain(name, script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisableScript_SortsBthenumLast()
    {
        var script = DeviceEnable.BuildScript("030d", enable: false);
        Assert.Contains("StartsWith('BTHENUM\\'", script, StringComparison.Ordinal);
        Assert.Contains("Sort-Object $rank)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableScript_UsesEnableDevice_BthenumFirst()
    {
        var script = DeviceEnable.BuildScript("030d", enable: true);
        Assert.Contains("$verb = 'enable-device'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$verb = 'disable-device'", script, StringComparison.Ordinal);
        Assert.Contains("Sort-Object $rank -Descending", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScript_UnknownPid_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeviceEnable.BuildScript("abcd", enable: false));
        Assert.Contains("No catalog VID", ex.Message, StringComparison.Ordinal);
    }
}
