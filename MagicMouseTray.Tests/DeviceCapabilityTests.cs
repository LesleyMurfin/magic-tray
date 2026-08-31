// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DeviceCapabilityTests
{
    static readonly DriverStatus[] AllDriverStates =
    {
        DriverStatus.Ok, DriverStatus.NotInstalled,
        DriverStatus.NotBound, DriverStatus.UnknownAppleMouse,
        DriverStatus.Error,
    };

    static readonly int[] AllSentinels = { 75, 0, -2, -1 };

    [Fact]
    public void Describe_CoversEveryCombination_WithoutThrowing()
    {
        foreach (DeviceKind kind in Enum.GetValues(typeof(DeviceKind)))
            foreach (var pct in AllSentinels)
                foreach (var drv in AllDriverStates)
                {
                    var row = DeviceCapability.Describe(kind, pct, drv);
                    Assert.False(string.IsNullOrWhiteSpace(row.ReadMethod));
                    Assert.False(string.IsNullOrWhiteSpace(row.Status));
                    if (row.ActionUrl is not null)
                        Assert.NotNull(row.ActionLabel);
                }
    }

    [Theory]
    [InlineData(DeviceKind.MagicMouseV1)]
    [InlineData(DeviceKind.MagicMouseV2)]
    [InlineData(DeviceKind.MagicMouseV3)]
    public void Mice_NeverOfferDriverInstallOrRepair(DeviceKind kind)
    {
        foreach (var drv in AllDriverStates)
        {
            var row = DeviceCapability.Describe(kind, 50, drv, boundFilter: "MagicMouseDriver");
            Assert.DoesNotContain("Install", row.ActionLabel ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("scroll fix", row.ActionLabel ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Read Battery Now", row.ActionLabel ?? "", StringComparison.OrdinalIgnoreCase);
            if (row.ActionUrl is not null)
                Assert.Contains("LesleyMurfin/magic-tray/releases", row.ActionUrl);
        }
    }

    [Fact]
    public void V3_BoundKmdf_ShowsFilterName_NoAction()
    {
        var row = DeviceCapability.Describe(DeviceKind.MagicMouseV3, 88, DriverStatus.Ok, "MagicMouseDriver");
        Assert.Contains("MagicMouseDriver", row.Status);
        Assert.Contains("88%", row.Status);
        Assert.Null(row.ActionLabel);
        Assert.Null(row.ActionUrl);
    }

    [Fact]
    public void V3_Unreadable_HasNoFlipAction()
    {
        var row = DeviceCapability.Describe(DeviceKind.MagicMouseV3, -2, DriverStatus.Ok, "MagicMouseDriver");
        Assert.Equal("Battery unavailable", DeviceCapability.BatteryLabel(-2));
        Assert.Null(row.ActionLabel);
        Assert.Null(row.ActionUrl);
    }

    [Fact]
    public void Keyboard_Unreadable_IsStatusOnly()
    {
        var row = DeviceCapability.Describe(DeviceKind.MagicKeyboard, -2, DriverStatus.Ok);
        Assert.Equal("Keyboard battery unavailable", row.Status);
        Assert.Null(row.ActionLabel);
        Assert.Null(row.ActionUrl);
    }

    [Fact]
    public void Keyboard_Readable_IsOk_NoAction()
    {
        var row = DeviceCapability.Describe(DeviceKind.MagicKeyboard, 60, DriverStatus.Ok);
        Assert.Equal("60%", row.Status);
        Assert.Null(row.ActionLabel);
    }

    [Fact]
    public void UnknownAppleMouse_OnlyOffersAppUpdate()
    {
        var row = DeviceCapability.Describe(DeviceKind.MagicMouseV1, -1, DriverStatus.UnknownAppleMouse);
        Assert.Equal("Check for app update", row.ActionLabel);
        Assert.Equal("https://github.com/LesleyMurfin/magic-tray/releases", row.ActionUrl);
    }

    [Fact]
    public void DriverLabel_UsesBoundFilterWhenPresent()
    {
        Assert.Equal("MagicMouseDriver", DeviceCapability.DriverLabel(DriverStatus.Ok, "MagicMouseDriver"));
        Assert.Equal("applewirelessmouse", DeviceCapability.DriverLabel(DriverStatus.Ok, "applewirelessmouse"));
        Assert.Equal("Scroll driver not bound", DeviceCapability.DriverLabel(DriverStatus.NotBound, null));
    }
}
