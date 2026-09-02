// SPDX-License-Identifier: MIT
using Microsoft.Win32;
using System.IO;

namespace MagicMouseTray;

public enum DriverStatus
{
    Ok,                // v1/v2 applewirelessmouse bound (or no Apple mouse paired)
    NotInstalled,      // expected package absent for this PID
    NotBound,          // package present, this PID not bound to it
    UnknownAppleMouse, // Apple-vendor HID mouse PID not in our known list
    Error,             // transient registry error
    StockKmdf,         // 0323 on stock HidBth — MagicMouseDriver not bound
    PatchedKmdf,       // 0323 bound to MagicMouseDriver (v3-fix KMDF package)
    PathAPatched,      // 0323 bound to applewirelessmouse (patched Apple)
}

internal sealed record DeviceDriverHealth(
    string DeviceId,
    string Pid,
    DriverStatus Status,
    string? BoundDriverName);

// Read-only health. BTHENUM Service=HidBth is the BT HID function driver,
// not the mouse driver. Merge Enum\HID Service/LowerFilters (PID match).
// 0323 prefers MagicMouseDriver, else applewirelessmouse (PathAPatched),
// else HidBth / null. v1 applewirelessmouse on BTHENUM LowerFilters stays Ok.
internal static class DriverHealthChecker
{
    const string AppleServiceKey = @"SYSTEM\CurrentControlSet\Services\AppleWirelessMouse";
    const string KmdfServiceKey = @"SYSTEM\CurrentControlSet\Services\MagicMouseDriver";
    const string BtHidEnumBase = @"SYSTEM\CurrentControlSet\Enum\BTHENUM";
    const string HidEnumBase = @"SYSTEM\CurrentControlSet\Enum\HID";
    const string HidUuidPrefix = "{00001124-0000-1000-8000-00805f9b34fb}";

    static readonly string[] AppleVidSegments = ["_VID&000205ac_", "_VID&0001004c_"];

    internal static readonly string[] KnownMousePids = ["030d", "0310", "0269", "0323"];
    internal static readonly string[] AppleFilterPids = ["030d", "0310", "0269"];
    internal static readonly string[] KmdfFilterPids = ["0323"];

    // Trackpads + keyboards: never feed mouse driver status (#4 unknown-keyboard false positive).
    static readonly string[] NonScrollApplePids =
    [
        "030e", "0265", "0324",
        "0239", "023a", "023b", "024f", "0250", "0267", "026c",
        "029c", "029a", "029f", "0320", "0321", "0322",
        "0255", "0256", "0257",
    ];

    static readonly string[] KeyboardPids =
    [
        "0239", "023a", "023b", "024f", "0250", "0267", "026c",
        "029c", "029a", "029f", "0320", "0321", "0322",
        "0255", "0256", "0257",
    ];

    internal static bool IsV3Pid(string pid) =>
        Array.Exists(KmdfFilterPids, p => p == pid.ToLowerInvariant());

    internal static bool IsKeyboardPid(string pid) =>
        Array.Exists(KeyboardPids, p => p == pid.ToLowerInvariant());

    internal static bool IsPatchedKmdfName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Equals(DriverPackageCatalog.PatchedKmdfServiceName, StringComparison.OrdinalIgnoreCase);

    internal static bool IsAppleFilterName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Equals(DriverPackageCatalog.AppleFilterServiceName, StringComparison.OrdinalIgnoreCase);

    // 0323: MagicMouseDriver wins; else applewirelessmouse; else Service or null.
    internal static string? PreferredBoundName(string pid, string? service, string[]? filters)
    {
        pid = pid.ToLowerInvariant();
        if (IsV3Pid(pid))
        {
            if (IsPatchedKmdfName(service))
                return DriverPackageCatalog.PatchedKmdfServiceName;
            var kmdf = FindFilter(filters, DriverPackageCatalog.PatchedKmdfServiceName);
            if (kmdf is not null)
                return DriverPackageCatalog.PatchedKmdfServiceName;
            if (FindFilter(filters, DriverPackageCatalog.AppleFilterServiceName) is not null)
                return DriverPackageCatalog.AppleFilterServiceName;
            if (IsAppleFilterName(service))
                return DriverPackageCatalog.AppleFilterServiceName;
            if (!string.IsNullOrEmpty(service))
                return service;
            return null;
        }

        var awm = FindFilter(filters, DriverPackageCatalog.AppleFilterServiceName);
        if (awm is not null)
            return DriverPackageCatalog.AppleFilterServiceName;
        if (IsAppleFilterName(service))
            return DriverPackageCatalog.AppleFilterServiceName;
        return null;
    }

    static string? FindFilter(string[]? filters, string name)
    {
        if (filters is null) return null;
        return Array.Find(filters, f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // LowerFilters is REG_MULTI_SZ or REG_SZ depending on who wrote it.
    static string[] ReadFilters(RegistryKey? key)
    {
        if (key is null) return [];
        var value = key.GetValue("LowerFilters");
        if (value is string[] arr) return arr;
        if (value is string s && !string.IsNullOrEmpty(s)) return [s];
        return [];
    }

    static string[] ConcatFilters(params string[][] parts)
    {
        var list = new List<string>();
        foreach (var p in parts)
        {
            if (p is { Length: > 0 }) list.AddRange(p);
        }
        return list.Count == 0 ? [] : list.ToArray();
    }

    // BTHENUM + HID layers. HID Service is a bind candidate (KMDF on the child).
    internal static string? MergeBoundLayers(
        string pid,
        string? bthService,
        string[]? bthFilters,
        string? hidService,
        string[]? hidFilters)
    {
        if ((bthFilters is null || bthFilters.Length == 0)
            && (hidFilters is null || hidFilters.Length == 0)
            && string.IsNullOrEmpty(hidService))
            return PreferredBoundName(pid, bthService, bthFilters);

        var merged = new List<string>();
        if (bthFilters is { Length: > 0 }) merged.AddRange(bthFilters);
        if (hidFilters is { Length: > 0 }) merged.AddRange(hidFilters);
        if (!string.IsNullOrEmpty(hidService)) merged.Add(hidService);
        return PreferredBoundName(pid, bthService, merged.ToArray());
    }

    // Enum\HID keys whose name contains the 4-hex PID. KMDF Service wins for logging.
    internal static void CollectHidLayer(string pid, out string? hidService, out string[] hidFilters)
    {
        hidService = null;
        var filterList = new List<string>();
        using var hidRoot = Registry.LocalMachine.OpenSubKey(HidEnumBase, writable: false);
        if (hidRoot == null)
        {
            hidFilters = [];
            return;
        }

        foreach (var name in hidRoot.GetSubKeyNames())
        {
            if (name.IndexOf(pid, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            using var deviceKey = hidRoot.OpenSubKey(name, writable: false);
            if (deviceKey == null) continue;

            foreach (var instanceName in deviceKey.GetSubKeyNames())
            {
                using var instance = deviceKey.OpenSubKey(instanceName, writable: false);
                var svc = instance?.GetValue("Service") as string;
                var lf = ConcatFilters(ReadFilters(deviceKey), ReadFilters(instance));
                if (!string.IsNullOrEmpty(svc))
                {
                    if (IsPatchedKmdfName(svc))
                        hidService = svc;
                    else if (hidService is null || (IsAppleFilterName(svc) && !IsPatchedKmdfName(hidService)))
                        hidService = svc;
                }
                if (lf.Length > 0)
                    filterList.AddRange(lf);
            }
        }

        hidFilters = filterList.ToArray();
    }

    static bool ServiceKeyExists(string keyPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
        return key != null;
    }

    internal static bool KmdfPackagePresent()
    {
        if (ServiceKeyExists(KmdfServiceKey))
            return true;
        try
        {
            var sys = Path.Combine(
                Environment.SystemDirectory,
                "drivers",
                DriverPackageCatalog.PatchedKmdfSysFileName);
            return File.Exists(sys);
        }
        catch
        {
            return false;
        }
    }

    internal static bool AppleFilterPackagePresent() => ServiceKeyExists(AppleServiceKey);

    // Pure classifier for tests (#4 mixed-device; 0323 PathAPatched is first-class).
    internal static DriverStatus Classify(
        string pid,
        string? boundDriverName,
        bool appleFilterPackagePresent,
        bool kmdfPackagePresent,
        string? lastingChoice = null)
    {
        pid = pid.ToLowerInvariant();

        if (!Array.Exists(KnownMousePids, p => p == pid))
            return DriverStatus.UnknownAppleMouse;

        if (IsV3Pid(pid))
        {
            if (IsPatchedKmdfName(boundDriverName))
                return DriverStatus.PatchedKmdf;
            if (IsAppleFilterName(boundDriverName))
                return DriverStatus.PathAPatched;
            // Mode A clears Apple LowerFilters. Sticky pathA is still PathAPatched, not Stock.
            if (Config.IsPathALastingChoice(lastingChoice))
                return DriverStatus.PathAPatched;
            // HidBth (or any non-KMDF/Apple service) is stock even if a KMDF
            // leftover is on disk. NotBound only when bound is null/empty AND
            // the KMDF package is present.
            if (string.IsNullOrEmpty(boundDriverName) && kmdfPackagePresent)
                return DriverStatus.NotBound;
            return DriverStatus.StockKmdf;
        }

        if (IsAppleFilterName(boundDriverName))
            return DriverStatus.Ok;
        if (appleFilterPackagePresent)
            return DriverStatus.NotBound;
        return DriverStatus.NotInstalled;
    }

    // Worst-state wins. Ok only when every paired Apple mouse is healthy
    // (v1/v2 Ok or 0323 PatchedKmdf). Bound v1 + unbound 2024 is NOT Ok (#4).
    internal static DriverStatus Aggregate(IReadOnlyList<DeviceDriverHealth> devices)
    {
        if (devices.Count == 0)
            return DriverStatus.Ok;

        bool anyError = false, anyUnknown = false, anyNotInstalled = false;
        bool anyNotBound = false, anyStock = false;

        foreach (var d in devices)
        {
            switch (d.Status)
            {
                case DriverStatus.Error: anyError = true; break;
                case DriverStatus.UnknownAppleMouse: anyUnknown = true; break;
                case DriverStatus.NotInstalled: anyNotInstalled = true; break;
                case DriverStatus.NotBound: anyNotBound = true; break;
                case DriverStatus.StockKmdf: anyStock = true; break;
            }
        }

        if (anyError) return DriverStatus.Error;
        if (anyUnknown) return DriverStatus.UnknownAppleMouse;
        if (anyNotInstalled) return DriverStatus.NotInstalled;
        if (anyNotBound) return DriverStatus.NotBound;
        if (anyStock) return DriverStatus.StockKmdf;
        return DriverStatus.Ok;
    }

    internal static IReadOnlyList<DeviceDriverHealth> GetPerDeviceStatus(string? lasting0323Choice = null)
    {
        try
        {
            using var btEnumKey = Registry.LocalMachine.OpenSubKey(BtHidEnumBase, writable: false);
            if (btEnumKey == null)
                return [];

            bool applePkg = AppleFilterPackagePresent();
            bool kmdfPkg = KmdfPackagePresent();
            var list = new List<DeviceDriverHealth>();
            var hidCache = new Dictionary<string, (string? service, string[] filters)>(StringComparer.OrdinalIgnoreCase);

            foreach (var subkeyName in btEnumKey.GetSubKeyNames())
            {
                if (!TryParseAppleHidKey(subkeyName, out var pid, skipNonScroll: true))
                    continue;

                using var deviceKey = btEnumKey.OpenSubKey(subkeyName, writable: false);
                if (deviceKey == null) continue;
                var instances = deviceKey.GetSubKeyNames();
                if (instances.Length == 0) continue;

                if (!hidCache.TryGetValue(pid, out var hidLayer))
                {
                    CollectHidLayer(pid, out var hidService, out var hidFilters);
                    hidLayer = (hidService, hidFilters);
                    hidCache[pid] = hidLayer;
                }

                foreach (var instanceName in instances)
                {
                    using var instance = deviceKey.OpenSubKey(instanceName, writable: false);
                    var service = instance?.GetValue("Service") as string;
                    var filters = ConcatFilters(ReadFilters(deviceKey), ReadFilters(instance));
                    var hidBound = PreferredBoundName(pid, hidLayer.service, hidLayer.filters)
                        ?? hidLayer.service;
                    var bound = MergeBoundLayers(pid, service, filters, hidLayer.service, hidLayer.filters);
                    var status = Classify(pid, bound, applePkg, kmdfPkg, lasting0323Choice);
                    var deviceId = $@"{BtHidEnumBase}\{subkeyName}\{instanceName}";
                    Logger.Log($"DRIVER_CHECK pid=0x{pid.ToUpperInvariant()} bth={service ?? "none"} hid={hidBound ?? "none"} bound={bound ?? "none"} status={status}");
                    list.Add(new DeviceDriverHealth(deviceId, pid, status, bound));
                }
            }

            return list;
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_CHECK_FAILED err={ex.Message}");
            return [new DeviceDriverHealth("", "", DriverStatus.Error, null)];
        }
    }

    internal static DriverStatus GetStatus(string? lasting0323Choice = null)
    {
        try
        {
            var per = GetPerDeviceStatus(lasting0323Choice);
            var status = Aggregate(per);
            Logger.Log($"DRIVER_CHECK status={status} devices={per.Count}");
            return status;
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_CHECK_FAILED err={ex.Message}");
            return DriverStatus.Error;
        }
    }

    // Returns true and the 4-hex PID when this BTHENUM subkey is an Apple HID device.
    // skipNonScroll: trackpads/keyboards never affect mouse driver status.
    internal static bool TryParseAppleHidKey(string subkeyName, out string pid, bool skipNonScroll = true)
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

        if (skipNonScroll && Array.Exists(NonScrollApplePids, p => p == parsedPid))
        {
            Logger.Log($"DRIVER_CHECK skip_non_scroll_apple pid=0x{parsedPid.ToUpperInvariant()}");
            return false;
        }
        return true;
    }
}
