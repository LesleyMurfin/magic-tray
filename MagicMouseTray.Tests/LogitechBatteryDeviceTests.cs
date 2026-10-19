// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class LogitechBatteryDeviceTests
{
    // The HID++ response byte gets the same floor as the Apple paths, pinned at its boundaries.
    // raw=0 and raw=101 kill relaxing the gate to `raw is >= 0 and <= 100` (which rejects
    // nothing, since the response byte is a byte); raw=1 and raw=100 kill over-tightening it.
    // The expected -1 kills the -2 sentinel swap: this path completed a read, and -2 means the
    // battery report is not exposed, which is what opens the elevated SDP-cache offer
    // (TrayMenu.ShowFixKeyboard) and RepairPlanner rule 2d. The distinct markers kill logging
    // a rejected value as a blocked read, or logging nothing at all as this path used to.
    [Theory]
    [InlineData(0, -1, "LOGI_BATTERY_ZERO")]
    [InlineData(1, 1, "LOGI_BATTERY_OK")]
    [InlineData(100, 100, "LOGI_BATTERY_OK")]
    [InlineData(101, -1, "LOGI_BATTERY_BAD")]
    public void ClassifyLevelByte_FloorIsOne_AndRejectionsAreMinusOne(
        byte raw, int expectedPct, string expectedMarker)
    {
        Assert.Equal((expectedPct, expectedMarker), LogitechBatteryDevice.ClassifyLevelByte(raw));
    }
}
