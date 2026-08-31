// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// Per-device status row for the tray menu. Driver SELECT lives on the device
// row and pulls a public GitHub / Boot Camp package — not from driver/.
internal static class DeviceCapability
{
    internal readonly record struct Row(
        string ReadMethod,    // short, human label for how battery is read
        string Status,        // e.g. "Connected · MagicMouseDriver"
        string? ActionLabel,  // null = no action (install/repair never offered)
        string? ActionUrl);   // app-update URL only, never a driver download

    const string ReleasesUrl = "https://github.com/LesleyMurfin/magic-tray/releases";

    internal static DeviceKind? KindForName(string name)
    {
        foreach (var m in MouseBatteryDevice.KnownMice)
            if (string.Equals(m.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                return m.Kind;
        foreach (var k in KeyboardBatteryDevice.KnownKeyboards)
            if (string.Equals(k.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                return DeviceKind.MagicKeyboard;
        return null;
    }

    internal static string BatteryLabel(int lastPct) => lastPct switch
    {
        >= 0 => $"{lastPct}%",
        -2 => "Battery unavailable",
        -3 => "Battery unavailable",
        _ => "No reading",
    };

    internal static string DriverLabel(DriverStatus driver, string? boundFilter)
    {
        if (!string.IsNullOrEmpty(boundFilter))
            return boundFilter;
        return driver switch
        {
            DriverStatus.Ok => "Connected",
            DriverStatus.NotBound => "Scroll driver not bound",
            DriverStatus.NotInstalled => "Scroll driver not detected",
            DriverStatus.UnknownAppleMouse => "Unknown Apple mouse",
            DriverStatus.Error => "Driver status unavailable",
            _ => "Unknown",
        };
    }

    internal static Row Describe(DeviceKind kind, int lastPct, DriverStatus driver)
        => Describe(kind, lastPct, driver, boundFilter: null);

    internal static Row Describe(DeviceKind kind, int lastPct, DriverStatus driver, string? boundFilter)
    {
        var battery = BatteryLabel(lastPct);
        var driverText = DriverLabel(driver, boundFilter);

        switch (kind)
        {
            case DeviceKind.MagicMouseV1:
            case DeviceKind.MagicMouseV2:
                return new("HID battery report",
                           lastPct >= 0
                               ? $"{battery} · {driverText}"
                               : $"{battery} · {driverText}",
                           ActionForUnknownOnly(driver),
                           UrlForUnknownOnly(driver));

            case DeviceKind.MagicMouseV3:
                // Live 0323 bind is MagicMouseDriver. Battery is a normal HID read —
                // the tray does not flip LowerFilters to take a reading.
                return new("HID battery report",
                           $"{battery} · {driverText}",
                           ActionForUnknownOnly(driver),
                           UrlForUnknownOnly(driver));

            case DeviceKind.MagicKeyboard:
                if (lastPct >= 0)
                    return new("HID battery report", battery, null, null);
                if (lastPct == -2)
                    return new("HID battery report", "Keyboard battery unavailable", null, null);
                return new("HID battery report", "No reading", null, null);

            default:
                return new("HID battery report",
                           lastPct >= 0 ? battery : "No reading",
                           null, null);
        }
    }

    static string? ActionForUnknownOnly(DriverStatus driver) =>
        driver == DriverStatus.UnknownAppleMouse ? "Check for app update" : null;

    static string? UrlForUnknownOnly(DriverStatus driver) =>
        driver == DriverStatus.UnknownAppleMouse ? ReleasesUrl : null;
}
