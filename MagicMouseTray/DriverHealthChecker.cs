// SPDX-License-Identifier: MIT
using Microsoft.Win32;

namespace MagicMouseTray;

internal enum DriverStatus
{
    Ok,                // expected filter bound for that PID (or no Apple mouse paired)
    NotInstalled,      // no relevant filter service and no recognized filter on a paired mouse
    NotBound,          // service present, known Apple PID paired, expected filter not in LowerFilters
    UnknownAppleMouse, // Apple-vendor HID device with a PID not in our known list
    Error              // transient registry error
}

// Read-only health check for the menu badge. Driver SELECT pulls KMDF from
// LesleyMurfin/magic-mouse-v3-windows-fix (never from magic-tray/driver/).
//
// Live split (do not fight this until the user picks a package):
//   PID 0323 — may still show MagicMouseDriver (May 20 custom). Best pull is
//              sbagirici WHQL AppleWirelessMouse.sys bound to 0323.
//   PID 030D / 0269 / 0310 — applewirelessmouse (tealtadpole Boot Camp INF).
//
// A 0323 still showing applewirelessmouse is treated as bound so we do not nag.
// Changing the bind happens only when the user picks a package.
internal static class DriverHealthChecker
{
    internal const string AppleFilterName = "applewirelessmouse";
    internal const string KmdfFilterName = "MagicMouseDriver";

    const string AppleServiceKey = @"SYSTEM\CurrentControlSet\Services\AppleWirelessMouse";
    const string KmdfServiceKey = @"SYSTEM\CurrentControlSet\Services\MagicMouseDriver";
    const string BtHidEnumBase = @"SYSTEM\CurrentControlSet\Enum\BTHENUM";

    // Bluetooth HID UUID (classic BT HID profile)
    const string HidUuidPrefix = "{00001124-0000-1000-8000-00805f9b34fb}";

    // Apple Bluetooth VID segments as they appear in BTHENUM subkey names.
    // VID&000205ac = Apple USB-IF VID (0x05AC), VID&0001004c = Apple BLE company ID (0x004C).
    static readonly string[] AppleVidSegments = ["_VID&000205ac_", "_VID&0001004c_"];

    // PIDs this tray knows how to show status for (lower-case, 4 hex digits).
    internal static readonly string[] KnownPids = ["030d", "0310", "0269", "0323"];

    // Older mice stay on applewirelessmouse. Do not treat MagicMouseDriver as expected here.
    internal static readonly string[] AppleFilterPids = ["030d", "0310", "0269"];

    // Magic Mouse 2024 — sole MagicMouseDriver in live state.
    internal static readonly string[] KmdfFilterPids = ["0323"];

    // Apple BT-HID devices that do not use a mouse scroll filter (trackpads + keyboards).
    static readonly string[] NonScrollApplePids =
    [
        "030e", "0265", "0324",                                       // trackpads (v1, v2, v3)
        "0239", "023a", "023b", "024f", "0250", "0267", "026c",       // existing keyboard rows
        "029c", "029a", "029f", "0320", "0321", "0322",               // Magic Keyboards 2021/2024
        "0255", "0256", "0257",                                       // Apple Wireless Keyboard 2011
    ];

    internal static bool IsV3Pid(string pid) =>
        Array.Exists(KmdfFilterPids, p => p == pid.ToLowerInvariant());

    // True when this LowerFilters entry is an acceptable bind for the PID.
    // 0323: MagicMouseDriver (live) or applewirelessmouse (legacy — do not nag).
    // 030D/0269/0310: applewirelessmouse only.
    internal static bool FilterMatchesPid(string filter, string pid)
    {
        if (string.IsNullOrEmpty(filter)) return false;
        pid = pid.ToLowerInvariant();
        if (IsV3Pid(pid))
        {
            return filter.Equals(KmdfFilterName, StringComparison.OrdinalIgnoreCase)
                || filter.Equals(AppleFilterName, StringComparison.OrdinalIgnoreCase);
        }
        return filter.Equals(AppleFilterName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool FiltersIncludeMatch(string[]? filters, string pid)
    {
        if (filters is null || filters.Length == 0) return false;
        return Array.Exists(filters, f => FilterMatchesPid(f, pid));
    }

    // First recognized filter name on this PID, or null. Prefer MagicMouseDriver on 0323.
    internal static string? PreferredBoundFilter(string[]? filters, string pid)
    {
        if (filters is null) return null;
        pid = pid.ToLowerInvariant();
        if (IsV3Pid(pid))
        {
            var kmdf = Array.Find(filters, f => f.Equals(KmdfFilterName, StringComparison.OrdinalIgnoreCase));
            if (kmdf is not null) return KmdfFilterName;
            var apple = Array.Find(filters, f => f.Equals(AppleFilterName, StringComparison.OrdinalIgnoreCase));
            if (apple is not null) return AppleFilterName;
            return null;
        }
        var awm = Array.Find(filters, f => f.Equals(AppleFilterName, StringComparison.OrdinalIgnoreCase));
        return awm is not null ? AppleFilterName : null;
    }

    static bool ServiceKeyExists(string keyPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
        return key != null;
    }

    static bool AnyFilterServicePresent() =>
        ServiceKeyExists(KmdfServiceKey) || ServiceKeyExists(AppleServiceKey);

    internal static DriverStatus GetStatus()
    {
        try
        {
            using var btEnumKey = Registry.LocalMachine.OpenSubKey(BtHidEnumBase, writable: false);
            if (btEnumKey == null)
            {
                Logger.Log("DRIVER_CHECK status=Ok (BTHENUM absent)");
                return DriverStatus.Ok;
            }

            bool anyAppleMouse = false;
            bool anyUnknownPid = false;
            bool anyNotBound = false;
            bool anyUnboundWithoutService = false;

            foreach (var subkeyName in btEnumKey.GetSubKeyNames())
            {
                if (!TryParseAppleHidKey(subkeyName, out var pid)) continue;

                using var deviceKey = btEnumKey.OpenSubKey(subkeyName, writable: false);
                if (deviceKey == null) continue;
                var instances = deviceKey.GetSubKeyNames();
                if (instances.Length == 0) continue;

                anyAppleMouse = true;
                bool pidKnown = Array.Exists(KnownPids, p => p == pid);

                foreach (var instanceName in instances)
                {
                    using var instance = deviceKey.OpenSubKey(instanceName, writable: false);
                    var filters = instance?.GetValue("LowerFilters") as string[];
                    bool isBound = FiltersIncludeMatch(filters, pid);
                    var boundName = PreferredBoundFilter(filters, pid);

                    if (!pidKnown)
                    {
                        Logger.Log($"DRIVER_CHECK unknown_apple_pid=0x{pid.ToUpper()} bound={isBound}");
                        anyUnknownPid = true;
                    }
                    else if (isBound)
                    {
                        Logger.Log($"DRIVER_CHECK pid=0x{pid.ToUpper()} LowerFilters={boundName}");
                    }
                    else
                    {
                        Logger.Log($"DRIVER_CHECK pid=0x{pid.ToUpper()} LowerFilters=missing");
                        anyNotBound = true;
                        if (!AnyFilterServicePresent())
                            anyUnboundWithoutService = true;
                    }
                }
            }

            if (!anyAppleMouse)
            {
                Logger.Log("DRIVER_CHECK status=Ok (no Apple BT HID mouse paired)");
                return DriverStatus.Ok;
            }

            if (anyUnknownPid)
            {
                Logger.Log("DRIVER_CHECK status=UnknownAppleMouse (PID not in known list)");
                return DriverStatus.UnknownAppleMouse;
            }

            if (anyNotBound)
            {
                if (anyUnboundWithoutService && !AnyFilterServicePresent())
                {
                    Logger.Log("DRIVER_CHECK status=NotInstalled (no filter service, known PID unbound)");
                    return DriverStatus.NotInstalled;
                }
                Logger.Log("DRIVER_CHECK status=NotBound (known PID, expected filter missing)");
                return DriverStatus.NotBound;
            }

            Logger.Log("DRIVER_CHECK status=Ok (expected filter bound)");
            return DriverStatus.Ok;
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_CHECK_FAILED err={ex.Message}");
            return DriverStatus.Error;
        }
    }

    // Per-PID variant — 0323 status must not inherit a 030D problem (and vice versa).
    internal static DriverStatus GetStatusForPid(string pid)
    {
        pid = pid.ToLowerInvariant();
        try
        {
            using var btEnumKey = Registry.LocalMachine.OpenSubKey(BtHidEnumBase, writable: false);
            if (btEnumKey == null) return DriverStatus.Ok;

            foreach (var subkeyName in btEnumKey.GetSubKeyNames())
            {
                if (!TryParseAppleHidKey(subkeyName, out var keyPid)) continue;
                if (keyPid != pid) continue;

                using var deviceKey = btEnumKey.OpenSubKey(subkeyName, writable: false);
                if (deviceKey == null) continue;
                var instances = deviceKey.GetSubKeyNames();
                if (instances.Length == 0) continue;

                foreach (var instanceName in instances)
                {
                    using var instance = deviceKey.OpenSubKey(instanceName, writable: false);
                    var filters = instance?.GetValue("LowerFilters") as string[];
                    bool isBound = FiltersIncludeMatch(filters, pid);
                    Logger.Log($"DRIVER_CHECK_PID pid=0x{pid.ToUpper()} bound={isBound} filter={PreferredBoundFilter(filters, pid) ?? "none"}");
                    if (isBound) return DriverStatus.Ok;
                    return AnyFilterServicePresent() ? DriverStatus.NotBound : DriverStatus.NotInstalled;
                }
            }

            return DriverStatus.Ok; // PID not currently paired
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_CHECK_PID_FAILED pid={pid} err={ex.Message}");
            return DriverStatus.Error;
        }
    }

    internal static string? GetBoundFilterForPid(string pid)
    {
        pid = pid.ToLowerInvariant();
        try
        {
            using var btEnumKey = Registry.LocalMachine.OpenSubKey(BtHidEnumBase, writable: false);
            if (btEnumKey == null) return null;

            foreach (var subkeyName in btEnumKey.GetSubKeyNames())
            {
                if (!TryParseAppleHidKey(subkeyName, out var keyPid)) continue;
                if (keyPid != pid) continue;

                using var deviceKey = btEnumKey.OpenSubKey(subkeyName, writable: false);
                if (deviceKey == null) continue;

                foreach (var instanceName in deviceKey.GetSubKeyNames())
                {
                    using var instance = deviceKey.OpenSubKey(instanceName, writable: false);
                    var filters = instance?.GetValue("LowerFilters") as string[];
                    var name = PreferredBoundFilter(filters, pid);
                    if (name is not null) return name;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_FILTER_PID_FAILED pid={pid} err={ex.Message}");
        }
        return null;
    }

    // Returns true and the 4-hex PID when this BTHENUM subkey is an Apple HID mouse/trackpad/keyboard.
    // Non-scroll Apple devices return false so they never affect mouse driver status.
    static bool TryParseAppleHidKey(string subkeyName, out string pid)
    {
        pid = "";
        if (!subkeyName.StartsWith(HidUuidPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        bool isApple = false;
        foreach (var seg in AppleVidSegments)
            if (subkeyName.Contains(seg, StringComparison.OrdinalIgnoreCase))
            { isApple = true; break; }
        if (!isApple) return false;

        int pidIdx = subkeyName.LastIndexOf("_PID&", StringComparison.OrdinalIgnoreCase);
        if (pidIdx < 0 || pidIdx + 9 > subkeyName.Length) return false;
        pid = subkeyName.Substring(pidIdx + 5, 4).ToLowerInvariant();
        var parsedPid = pid;

        if (Array.Exists(NonScrollApplePids, p => p == parsedPid))
        {
            Logger.Log($"DRIVER_CHECK skip_non_scroll_apple pid=0x{parsedPid.ToUpper()}");
            return false;
        }
        return true;
    }
}
