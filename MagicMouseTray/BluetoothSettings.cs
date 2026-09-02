// SPDX-License-Identifier: MIT
using System.Diagnostics;

namespace MagicMouseTray;

// Opens Windows Bluetooth Settings for pair/toggle. Rename of a Bluetooth
// HID device on Windows 11 lives in Devices and Printers, not Settings.
internal static class BluetoothSettings
{
    internal const string DevicesUri = "ms-settings:bluetooth";

    internal const string RenamePageFileName = "control";
    internal const string RenamePageArguments = "/name Microsoft.DevicesAndPrinters";

    internal const string MenuLabel = "Bluetooth";
    internal const string AddOrChangeDevices = "Add or change devices…";
    internal const string TurnBluetoothOnOrOff = "Turn Bluetooth on or off…";
    internal const string RenameADevice = "Rename a device…";

    internal static readonly string[] MenuItemLabels =
    [
        AddOrChangeDevices,
        TurnBluetoothOnOrOff,
        RenameADevice,
    ];

    internal static void OpenDevicesPage()
    {
        Process.Start(new ProcessStartInfo(DevicesUri) { UseShellExecute = true });
    }

    internal static void OpenRenamePage()
    {
        Process.Start(new ProcessStartInfo(RenamePageFileName)
        {
            Arguments = RenamePageArguments,
            UseShellExecute = true,
        });
    }
}
