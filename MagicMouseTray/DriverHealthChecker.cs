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

    // Family membership, not exact equality: the live 2024 stack binds
    // MagicMouseDriver204Scroll, a suffixed variant of the catalog constant.
    // The rule itself lives in RepairPlanner - never duplicated here.
    internal static bool IsPatchedKmdfName(string? name) => RepairPlanner.IsKmdfFamily(name);

    internal static bool IsAppleFilterName(string? name) => RepairPlanner.IsAppleFamily(name);

    // 0323: KMDF family wins; else Apple filter family; else Service or null.
    // Returns the VERBATIM registry name (e.g. MagicMouseDriver204Scroll), never a
    // normalized catalog constant - callers query SCM with whatever this returns.
    internal static string? PreferredBoundName(string pid, string? service, string[]? filters)
    {
        pid = pid.ToLowerInvariant();
        if (IsV3Pid(pid))
        {
            if (IsPatchedKmdfName(service))
                return service;
            var kmdf = FindKmdfFilter(filters);
            if (kmdf is not null)
                return kmdf;
            var v3Awm = FindAppleFilter(filters);
            if (v3Awm is not null)
                return v3Awm;
            if (IsAppleFilterName(service))
                return service;
            if (!string.IsNullOrEmpty(service))
                return service;
            return null;
        }

        var awm = FindAppleFilter(filters);
        if (awm is not null)
            return awm;
        if (IsAppleFilterName(service))
            return service;
        return null;
    }

    // Every family filter named on the live stack for this PID, in the SAME
    // precedence order PreferredBoundName uses, de-duplicated case-insensitively
    // with verbatim registry casing. Pure - no SCM, no registry - so the caller
    // (and tests) decide which candidate is EFFECTIVE via PickEffectiveBound.
    internal static string[] BoundCandidates(string pid, string? service, string[]? filters)
    {
        pid = pid.ToLowerInvariant();
        var ordered = new List<string>();

        void Add(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return;
            if (ordered.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                return;
            ordered.Add(name);
        }

        if (IsV3Pid(pid))
        {
            if (IsPatchedKmdfName(service))
                Add(service);
            if (filters is not null)
                foreach (var f in filters)
                    if (IsPatchedKmdfName(f))
                        Add(f);
        }

        if (filters is not null)
            foreach (var f in filters)
                if (IsAppleFilterName(f))
                    Add(f);
        if (IsAppleFilterName(service))
            Add(service);

        return ordered.Count == 0 ? [] : ordered.ToArray();
    }

    // Windows PnP loads EVERY filter named in LowerFilters, so the effective one is
    // whichever is actually RUNNING - live 2024 lists a stale MagicMouseDriver
    // (Stopped) ahead of the working MagicMouseDriver204Scroll (Running). With none
    // running the first candidate stands, keeping the stopped-but-bound diagnosis.
    internal static string? PickEffectiveBound(string[] candidates, Func<string, bool> isRunning)
    {
        if (candidates is not { Length: > 0 })
            return null;
        foreach (var c in candidates)
            if (isRunning(c))
                return c;
        return candidates[0];
    }

    // First family member, original casing preserved.
    static string? FindKmdfFilter(string[]? filters) =>
        filters is null ? null : Array.Find(filters, RepairPlanner.IsKmdfFamily);

    static string? FindAppleFilter(string[]? filters) =>
        filters is null ? null : Array.Find(filters, RepairPlanner.IsAppleFamily);

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

    // The one composition of the bind layers: BTHENUM LowerFilters, HID
    // LowerFilters, then the HID Service (a bind candidate - KMDF on the child).
    // MergeBoundLayers and BoundCandidates must see the same list.
    static string[] MergedFilterLayers(string[]? bthFilters, string[]? hidFilters, string? hidService)
    {
        var merged = new List<string>();
        if (bthFilters is { Length: > 0 }) merged.AddRange(bthFilters);
        if (hidFilters is { Length: > 0 }) merged.AddRange(hidFilters);
        if (!string.IsNullOrEmpty(hidService)) merged.Add(hidService);
        return merged.Count == 0 ? [] : merged.ToArray();
    }

    // BTHENUM + HID layers. HID Service is a bind candidate (KMDF on the child).
    internal static string? MergeBoundLayers(
        string pid,
        string? bthService,
        string[]? bthFilters,
        string? hidService,
        string[]? hidFilters) =>
        PreferredBoundName(pid, bthService, MergedFilterLayers(bthFilters, hidFilters, hidService));

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

    // Registry bind name is not SCM. Leftover LowerFilters=MagicMouseDriver with
    // the service STOPPED (win32 31) must not stay PatchedKmdf — that locks the
    // KMDF radio and hides a dead wheel.
    internal static DriverStatus AfterFilterServiceState(
        DriverStatus status, string pid, bool filterServiceRunning)
    {
        pid = pid.ToLowerInvariant();
        if (IsV3Pid(pid) && status == DriverStatus.PatchedKmdf && !filterServiceRunning)
            return DriverStatus.NotBound;
        if (!IsV3Pid(pid) && status == DriverStatus.Ok && !filterServiceRunning)
            return DriverStatus.NotBound;
        return status;
    }

    internal static bool KernelServiceRunning(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "query " + name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
                return false;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }
            var text = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            return text.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
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
            // sc.exe per name, once per call: this runs on every menu open and poll.
            var scmCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            bool Running(string name)
            {
                if (!scmCache.TryGetValue(name, out var run))
                {
                    run = KernelServiceRunning(name);
                    scmCache[name] = run;
                }
                return run;
            }

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
                    var merged = MergedFilterLayers(filters, hidLayer.filters, hidLayer.service);
                    var hidBound = PreferredBoundName(pid, hidLayer.service, hidLayer.filters)
                        ?? hidLayer.service;
                    // LowerFilters can name several family filters (live 2024: stale
                    // MagicMouseDriver first, working MagicMouseDriver204Scroll second).
                    // The RUNNING one is effective; collection order must not decide.
                    var candidates = BoundCandidates(pid, service, merged);
                    var bound = PickEffectiveBound(candidates, Running)
                        ?? PreferredBoundName(pid, service, merged);
                    var status = Classify(pid, bound, applePkg, kmdfPkg, lasting0323Choice);
                    // SCM state of the ACTUAL bound service, not the catalog constant.
                    var scmName = string.IsNullOrEmpty(bound)
                        ? (IsV3Pid(pid)
                            ? DriverPackageCatalog.PatchedKmdfServiceName
                            : DriverPackageCatalog.AppleFilterServiceName)
                        : bound;
                    var scm = Running(scmName);
                    status = AfterFilterServiceState(status, pid, scm);
                    var deviceId = $@"{BtHidEnumBase}\{subkeyName}\{instanceName}";
                    Logger.Log($"DRIVER_CHECK pid=0x{pid.ToUpperInvariant()} bth={service ?? "none"} hid={hidBound ?? "none"} bound={bound ?? "none"} status={status} scm={(scm ? "running" : "stopped")} cands={(candidates.Length == 0 ? "none" : string.Join(",", candidates))}");
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
