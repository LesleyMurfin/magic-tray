// SPDX-License-Identifier: MIT
using System.Linq;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class KindForNameTests
{
    [Fact]
    public void AllKnownMouseNames_ResolveToTheirKind()
    {
        foreach (var m in MouseBatteryDevice.KnownMice)
            Assert.Equal(m.Kind, DeviceCapability.KindForName(m.DisplayName));
    }

    [Fact]
    public void AllKnownKeyboardNames_ResolveToMagicKeyboard()
    {
        foreach (var k in KeyboardBatteryDevice.KnownKeyboards)
            Assert.Equal(DeviceKind.MagicKeyboard, DeviceCapability.KindForName(k.DisplayName));
    }

    [Fact]
    public void AllDistinctDisplayNames_Resolve()
    {
        // 6 distinct mouse/trackpad names (BT+USB pairs share a name) + 16 keyboard = 22.
        // Update the count when a device is added; the sibling tests above cover per-entry resolution.
        var names = MouseBatteryDevice.KnownMice.Select(m => m.DisplayName)
            .Concat(KeyboardBatteryDevice.KnownKeyboards.Select(k => k.DisplayName))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(22, names.Count);
        Assert.All(names, n => Assert.NotNull(DeviceCapability.KindForName(n)));
    }

    [Fact]
    public void UnknownName_ReturnsNull()
    {
        Assert.Null(DeviceCapability.KindForName("Logitech MX Master"));
        Assert.Null(DeviceCapability.KindForName(""));
    }
}
