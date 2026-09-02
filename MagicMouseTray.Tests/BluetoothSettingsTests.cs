// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class BluetoothSettingsTests
{
    [Fact]
    public void DevicesUri_IsMsSettingsBluetooth()
    {
        Assert.Equal("ms-settings:bluetooth", BluetoothSettings.DevicesUri);
    }

    [Fact]
    public void RenamePage_IsDevicesAndPrinters_NotBluetoothSettingsOrAbout()
    {
        Assert.Equal("control", BluetoothSettings.RenamePageFileName);
        Assert.Equal("/name Microsoft.DevicesAndPrinters", BluetoothSettings.RenamePageArguments);

        Assert.NotEqual("ms-settings:bluetooth", BluetoothSettings.RenamePageFileName);
        Assert.NotEqual("ms-settings:about", BluetoothSettings.RenamePageFileName);
        Assert.NotEqual("ms-settings:bluetooth", BluetoothSettings.RenamePageArguments);
        Assert.NotEqual("ms-settings:about", BluetoothSettings.RenamePageArguments);
        Assert.DoesNotContain("ms-settings:bluetooth", BluetoothSettings.RenamePageArguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ms-settings:about", BluetoothSettings.RenamePageArguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ms-settings:bluetooth", BluetoothSettings.RenamePageFileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ms-settings:about", BluetoothSettings.RenamePageFileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Labels_ExactCopy_NoPathA()
    {
        Assert.Equal("Bluetooth", BluetoothSettings.MenuLabel);
        Assert.Equal("Add or change devices…", BluetoothSettings.AddOrChangeDevices);
        Assert.Equal("Turn Bluetooth on or off…", BluetoothSettings.TurnBluetoothOnOrOff);
        Assert.Equal("Rename a device…", BluetoothSettings.RenameADevice);
        Assert.Equal(
            new[]
            {
                "Add or change devices…",
                "Turn Bluetooth on or off…",
                "Rename a device…",
            },
            BluetoothSettings.MenuItemLabels);

        Assert.DoesNotContain("PATH-A", BluetoothSettings.MenuLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH-A", BluetoothSettings.DevicesUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH-A", BluetoothSettings.RenamePageFileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH-A", BluetoothSettings.RenamePageArguments, StringComparison.OrdinalIgnoreCase);
        foreach (var label in BluetoothSettings.MenuItemLabels)
            Assert.DoesNotContain("PATH-A", label, StringComparison.OrdinalIgnoreCase);
    }
}
