// SPDX-License-Identifier: MIT
using System.Linq;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class KeyboardBatteryDeviceTests
{
    static IEnumerable<KeyboardBatteryDevice.VidPidEntry> UsbRows =>
        KeyboardBatteryDevice.KnownKeyboards
            .Where(k => k.VidPattern.Equals("VID_05AC", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void EveryKeyboardPid_HasUsbVid05acRow()
    {
        var pids = KeyboardBatteryDevice.KnownKeyboards
            .Select(k => k.PidPattern[^4..].ToUpperInvariant())
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        var usb = UsbRows
            .Select(k => k.PidPattern[^4..].ToUpperInvariant())
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        Assert.Equal(pids, usb);
        Assert.Equal(16, usb.Count);
    }

    [Fact]
    public void UsbVid05ac_EveryKeyboardPid_DiscoversOnCol02()
    {
        Assert.NotEmpty(UsbRows);
        foreach (var k in UsbRows)
        {
            var pid = k.PidPattern[^4..];
            var path = $@"\\?\hid#vid_05ac&pid_{pid}&col02#7&usb";
            var found = DeviceRegistry.DiscoverFromPaths([path]);
            Assert.Single(found);
            Assert.Equal(DeviceKind.MagicKeyboard, found[0].Kind);
            Assert.Equal(pid.ToLowerInvariant(), found[0].Pid);
            Assert.Equal(k.DisplayName, found[0].DeviceName);
        }
    }

    [Fact]
    public void UsbKeyboard_WithoutCol02_IsIgnored()
    {
        Assert.Empty(DeviceRegistry.DiscoverFromPaths(
            [@"\\?\hid#vid_05ac&pid_0239&col01#7&usb"]));
    }

    [Fact]
    public void Discover_0239_BtCol02_IsAppleWirelessKeyboard()
    {
        const string path =
            @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&0239&col02#9&bt";
        var found = DeviceRegistry.DiscoverFromPaths([path]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicKeyboard, found[0].Kind);
        Assert.Equal("0239", found[0].Pid);
        Assert.Equal("Apple Wireless Keyboard (2011)", found[0].DeviceName);
    }

    // Pins the two decisions the col02 Feature byte carries, at their boundaries.
    // raw=0 and raw=101 kill relaxing the gate to `raw is >= 0 and <= 100` (the bound this
    // path shipped with, which rejects nothing at all since fbuf[1] is a byte); raw=1 and
    // raw=100 kill over-tightening it. The expected -1 kills the -2 sentinel swap: -2 is
    // "battery report not exposed" and is what opens the elevated SDP-cache offer
    // (TrayMenu.ShowFixKeyboard) and RepairPlanner rule 2d, so a read that succeeded must
    // never produce it. The marker kills folding a rejected value into KB_FEATURE_BLOCKED,
    // which would claim the SDP patch is missing on a keyboard whose Feature read worked.
    [Theory]
    [InlineData(0, -1, "KB_BATTERY_ZERO")]
    [InlineData(1, 1, "KB_BATTERY_OK")]
    [InlineData(100, 100, "KB_BATTERY_OK")]
    [InlineData(101, -1, "KB_BATTERY_BAD")]
    public void ClassifyFeatureByte_FloorIsOne_AndRejectionsAreMinusOne(
        byte raw, int expectedPct, string expectedMarker)
    {
        Assert.Equal((expectedPct, expectedMarker), KeyboardBatteryDevice.ClassifyFeatureByte(raw));
    }
}
