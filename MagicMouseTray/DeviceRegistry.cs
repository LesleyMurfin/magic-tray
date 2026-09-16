// Discovers all connected Apple HID battery devices by scanning the HID device interface list.
// Returns a fresh snapshot per call — no caching. AdaptivePoller drives the poll cadence.

namespace MagicMouseTray;

internal static class DeviceRegistry
{
    /// <summary>
    /// Scans all present HID interfaces and returns one IBatteryDevice per matched Apple device.
    /// Matching priority: mouse VID/PID checked first, then keyboard VID/PID.
    /// </summary>
    public static IReadOnlyList<IBatteryDevice> Discover(bool enableThirdParty = false)
        => DiscoverFromPaths(HidNative.EnumerateHidPaths(), enableThirdParty);

    // Test hook - same classify rules as Discover, no live HID scan.
    internal static IReadOnlyList<IBatteryDevice> DiscoverFromPaths(
        IEnumerable<string> paths, bool enableThirdParty = false)
    {
        var matched = new List<(string Path, IBatteryDevice Device)>();
        foreach (var path in paths)
        {
            var device = TryClassify(path, enableThirdParty);
            if (device is not null)
                matched.Add((path, device));
        }

        // One entry per physical device. After a USB-C charge Windows leaves phantom
        // HID\VID_05AC&PID_xxxx&MI_yy&COLzz interfaces behind (live: all CM_PROB_PHANTOM);
        // they still answer HidD_GetInputReport, with a zero percent byte, so the same
        // Magic Mouse was listed twice and the dead cable interface reported a false 0%.
        // Prefer the live Bluetooth interface; keep USB only when it is the sole transport
        // for that PID (a genuinely cabled mouse must still report battery).
        var results = new List<IBatteryDevice>(matched.Count);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, device) in matched)
        {
            var pid = ExtractPid(path);
            bool bluetooth = IsBluetoothTransportPath(path);

            if (!bluetooth && IsUsbTransportPath(path) && HasBluetoothTransport(matched, pid))
            {
                Logger.Log($"DISCOVER_SKIP_DUPLICATE pid={pid} transport=usb path={path}");
                continue;
            }

            if (!claimed.Add(pid))
            {
                Logger.Log($"DISCOVER_SKIP_DUPLICATE pid={pid} transport={(bluetooth ? "bt" : "usb")} path={path}");
                continue;
            }

            results.Add(device);
        }
        return results;
    }

    static bool HasBluetoothTransport(List<(string Path, IBatteryDevice Device)> matched, string pid)
    {
        foreach (var (path, _) in matched)
        {
            if (IsBluetoothTransportPath(path) &&
                ExtractPid(path).Equals(pid, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // A Bluetooth / HID-over-GATT interface carries the BT service GUID plus the
    // "_vid&<bt-vid>_pid&<pid>" form, e.g.
    // \\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323&col02#...
    // 0001004C = Bluetooth SIG Apple vendor id, 000205AC = USB-IF Apple vendor id over BT.
    static readonly string[] BluetoothVidForms = ["_vid&0001004c_", "_vid&000205ac_"];

    internal static bool IsBluetoothTransportPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!path.Contains("_pid&", StringComparison.OrdinalIgnoreCase)) return false;
        return Array.Exists(BluetoothVidForms,
            form => path.Contains(form, StringComparison.OrdinalIgnoreCase));
    }

    // A USB interface carries the "vid_xxxx&pid_yyyy" form (plus mi_/col for the
    // charge-cable phantoms), e.g. \\?\hid#vid_05ac&pid_0323&mi_01&col02#...
    internal static bool IsUsbTransportPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.Contains("vid_", StringComparison.OrdinalIgnoreCase)
            && path.Contains("pid_", StringComparison.OrdinalIgnoreCase);
    }

    // iPhone Hands-Free / HFP exposes a WMI battery (live 60%) that is not the mouse.
    internal static bool IsHandsFreeOrIphonePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase)
            || path.Contains("hands-free", StringComparison.OrdinalIgnoreCase)
            || path.Contains("handsfree", StringComparison.OrdinalIgnoreCase)
            || path.Contains("bthhf", StringComparison.OrdinalIgnoreCase)
            || path.Contains("iphone", StringComparison.OrdinalIgnoreCase);
    }

    // Live 0323 battery is COL02 Input 0x90. COL01 is pointer (no wheel 0x0038).
    internal static bool Is0323BatteryCollectionPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (ExtractPid(path) != "0323") return false;
        return path.Contains("col02", StringComparison.OrdinalIgnoreCase);
    }

    static IBatteryDevice? TryClassify(string path, bool enableThirdParty)
    {
        if (IsHandsFreeOrIphonePath(path))
            return null;

        // Magic Mouse — check all variants
        foreach (var entry in MouseBatteryDevice.KnownMice)
        {
            if (path.Contains(entry.VidPattern, StringComparison.OrdinalIgnoreCase) &&
                path.Contains(entry.PidPattern, StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Kind == DeviceKind.MagicMouseV3 && !Is0323BatteryCollectionPath(path))
                    return null;
                return new MouseBatteryDevice(path, entry.DisplayName, entry.Kind);
            }
        }

        // B2 (experimental, flag-gated): directly-connected Logitech HID++ devices ONLY.
        // Checked BEFORE the keyboard col02 gate below: Logitech HID paths have no col02
        // collection, so gating on col02 first would make this branch unreachable. The
        // VID 046d match keeps it from interfering with Apple-keyboard classification.
        // Unifying/Bolt RECEIVERS are intentionally excluded — they require per-device-index
        // (1-6) addressing, not 0xFF; reading them is out of scope.
        if (enableThirdParty
            && path.Contains("vid_046d", StringComparison.OrdinalIgnoreCase)
            && !IsLogitechReceiver(path))
            return new LogitechBatteryDevice(path);

        // Magic Keyboard — col02 only (col01=keyboard, col03=consumer+vendor; battery cap is on col02).
        // Filtering here prevents 3 instances per physical keyboard (one per collection).
        if (!path.Contains("col02", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var entry in KeyboardBatteryDevice.KnownKeyboards)
        {
            if (path.Contains(entry.VidPattern, StringComparison.OrdinalIgnoreCase) &&
                path.Contains(entry.PidPattern, StringComparison.OrdinalIgnoreCase))
                return new KeyboardBatteryDevice(path, entry.DisplayName);
        }

        return null;
    }

    // Known Logitech receiver PIDs (Unifying / Bolt / nano variants). Receivers are excluded
    // from B2 because battery must be queried per paired device index, not at 0xFF.
    static readonly string[] LogitechReceiverPids = ["c52b", "c548", "c531", "c52f", "c534"];

    static bool IsLogitechReceiver(string path) =>
        Array.Exists(LogitechReceiverPids, pid => path.Contains(pid, StringComparison.OrdinalIgnoreCase));

    internal static string ExtractPid(string path)
    {
        int idx = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = path.IndexOf("pid&", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0 && idx + 8 <= path.Length) return path.Substring(idx + 4, 4).ToLowerInvariant();
        return "0000";
    }
}
