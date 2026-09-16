// Discovers connected Apple HID battery interfaces by scanning the HID device interface list.
// One IBatteryDevice per matched interface path, so one physical device can yield several.
// Returns a fresh snapshot per call — no caching. AdaptivePoller drives the poll cadence.

namespace MagicMouseTray;

internal static class DeviceRegistry
{
    /// <summary>
    /// Scans all present HID interfaces and returns one IBatteryDevice per matched Apple
    /// interface PATH, not one per physical device. The multiplicity is deliberate: a single
    /// mouse exposes several interfaces that pass the same gate and only some of them answer
    /// a battery read, so every candidate is handed to AdaptivePoller, which groups by
    /// DeviceName and keeps the best-ranked reading. Only identical path strings collapse.
    /// Matching priority: mouse VID/PID checked first, then keyboard VID/PID.
    /// </summary>
    public static IReadOnlyList<IBatteryDevice> Discover(bool enableThirdParty = false)
        => DiscoverFromPaths(HidNative.EnumerateHidPaths(), enableThirdParty);

    // Test hook - same classify rules as Discover, no live HID scan.
    internal static IReadOnlyList<IBatteryDevice> DiscoverFromPaths(
        IEnumerable<string> paths, bool enableThirdParty = false)
    {
        var results = new List<IBatteryDevice>();

        // Collapse only genuinely identical interface paths. Windows enumerated the same
        // USB col02 path twice in one cycle (live log 2026-09-16: two identical
        // DISCOVER_SKIP_DUPLICATE lines for ...&mi_01&col02#a&16288706&0&0001), so the
        // same path string must not produce two devices.
        //
        // Everything else survives. One physical mouse exposes several distinct live col02
        // interfaces (live REPAIR_SNAPSHOT pid=0323: bt=2 usb=6 after a USB-C charge) and
        // only some of them answer HidD_GetInputReport with a real level; the rest return
        // [90 00 00]. Discovery cannot tell which is which without reading, so it keeps them
        // all and AdaptivePoller picks the winner: it groups by DeviceName and ranks a real
        // percentage above -2 above -1 (AdaptivePoller.ReadingRank), and stops reading a
        // group's remaining interfaces once one answers with a real percentage. Dropping
        // candidates here ran before that ranking and could leave only an interface that
        // never reports.
        //
        // There is deliberately no transport preference and no one-device-per-PID rule. The
        // false 0% from a charge-cable phantom that those rules were written for is rejected
        // at parse level instead: every IBatteryDevice read path gates on
        // MouseBatteryDevice.IsRealLevel, and each logs the rejected zero under its own marker
        // (MOUSE_BATTERY_ZERO, KB_BATTERY_ZERO) rather than a blocked-read marker. All three
        // implementations need that floor because a real 0 outranks -2 and -1 in
        // AdaptivePoller.ReadingRank and ends AdaptivePoller.BestReading's scan, so without it
        // a dead interface answering zero would beat the live interface's failure sentinel.
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var device = TryClassify(path, enableThirdParty);
            if (device is null)
                continue;

            // Renamed from DISCOVER_SKIP_DUPLICATE: that marker used to cover skipped
            // transports and extra interfaces as well, which is no longer what happens.
            // This one fires only for a repeated path string, and only below TryClassify so
            // that only a path that really classified as an Apple battery interface is
            // reported - a repeated dock or non-Apple keyboard path writes nothing.
            if (!seenPaths.Add(path))
            {
                Logger.Log($"DISCOVER_SKIP_SAME_PATH pid={ExtractPid(path)} path={path}");
                continue;
            }

            results.Add(device);
        }
        return results;
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
