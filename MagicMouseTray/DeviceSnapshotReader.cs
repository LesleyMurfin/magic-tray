// SPDX-License-Identifier: MIT
using Microsoft.Win32;
using System.IO;

namespace MagicMouseTray;

// Reads the live PnP picture for every catalog PID and turns it into the plain
// DeviceSnapshot records RepairPlanner decides on. Read-only: this file opens
// registry keys with writable:false and never starts, restarts or removes
// anything. Charge-cable leftovers under Enum\USB / Enum\HID are counted so the
// planner can say "these are not your mouse", never so they can be touched.
internal static class DeviceSnapshotReader
{
    // internal: DeviceStackReader walks the same hive with the same PID matcher
    // (BthenumKeyMatchesPid) so exactly one BTHENUM convention exists.
    internal const string BtEnumBase = @"SYSTEM\CurrentControlSet\Enum\BTHENUM";
    const string HidEnumBase = @"SYSTEM\CurrentControlSet\Enum\HID";
    const string UsbEnumBase = @"SYSTEM\CurrentControlSet\Enum\USB";
    const string ServicesBase = @"SYSTEM\CurrentControlSet\Services";

    // Mirrors DriverHealthChecker's private AppleVidSegments. Only used as a
    // fallback when TryParseAppleHidKey refuses a subkey (BLE keyboards can sit
    // under a different service UUID than the HID one it insists on).
    static readonly string[] AppleVidSegments = ["_VID&000205ac_", "_VID&0001004c_"];

    readonly record struct BthLayer(int InstanceCount, string? Service, string[] Filters);

    readonly record struct HidLayer(int PhantomCount, bool Col01, bool Col02);

    internal static IReadOnlyList<DeviceSnapshot> Read(Config config) => Read(config, null);

    // lastBatteryByPid: the tray's most recent battery reading per PID, with
    // the MouseBatteryDevice / KeyboardBatteryDevice sentinels intact (-1 no
    // reading, -2 present but blocked, -3 three consecutive no-readings). A
    // PID absent from the map has not been measured and stays null, which the
    // planner must never turn into a finding. Passed in rather than polled
    // here: this reader never opens a HID handle.
    internal static IReadOnlyList<DeviceSnapshot> Read(
        Config config, IReadOnlyDictionary<string, int>? lastBatteryByPid)
    {
        // sc query spawns a process, so each distinct bound service name is
        // queried once for the whole menu open.
        var serviceCache = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        var snapshots = new List<DeviceSnapshot>();

        // The watcher is machine-wide, and reading its state means touching the
        // filesystem under C:\ProgramData. The sweep covers ~two dozen catalog
        // PIDs, so it is read ONCE here and threaded through, never per PID.
        // DeviceDiagReader.WatcherState does memoize for 30 s, but this reader
        // does not lean on that: one machine-wide fact belongs in one call.
        var watcher = ReadWatcherHealth();

        foreach (var pid in CatalogPids())
        {
            try
            {
                snapshots.Add(ReadPid(pid, config, serviceCache, watcher, lastBatteryByPid));
            }
            catch (Exception ex)
            {
                Logger.Log($"REPAIR_SNAPSHOT_FAILED pid={pid} err={ex.Message}");
            }
        }

        return snapshots;
    }

    internal static DeviceSnapshot ReadPid(string pid, Config config) =>
        ReadPid(
            pid,
            config,
            new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase),
            ReadWatcherHealth(),
            null);

    // How stale the watcher's heartbeat may be before it counts as stopped.
    // mm-auto-f1-watcher.ps1 logs a "heartbeat alive" line every 5 minutes, so
    // three missed beats is the threshold: long enough that one skipped write,
    // a slow scheduled-task start or a resume from sleep does not accuse a
    // working watcher, short enough that a task which died stays out of the
    // "healthy" wording.
    const int WatcherHeartbeatStaleMinutes = 15;

    // Tri-state, machine-wide: true installed and beating, false installed but
    // silent, null not installed / nothing readable. Never a finding by itself;
    // it only sharpens the tray's user-initiated scroll guidance, so an
    // unreadable ProgramData folder must degrade to null and never break the
    // whole sweep.
    static bool? ReadWatcherHealth()
    {
        try
        {
            var state = DeviceDiagReader.WatcherState();
            if (state.Installed != true)
                return null;
            // Installed but its log carries no heartbeat at all: the scheduled
            // task is not running it. Same conclusion as a stale beat.
            if (state.LastHeartbeatUtc is not DateTime beat)
                return false;
            return DateTime.UtcNow - beat <= TimeSpan.FromMinutes(WatcherHeartbeatStaleMinutes);
        }
        catch
        {
            return null;
        }
    }

    static DeviceSnapshot ReadPid(
        string pid,
        Config config,
        Dictionary<string, bool?> serviceCache,
        bool? watcherHealthy,
        IReadOnlyDictionary<string, int>? lastBatteryByPid)
    {
        pid = pid.ToLowerInvariant();
        bool v3 = DriverHealthChecker.IsV3Pid(pid);

        var bth = ReadBthenumLayer(pid);
        var hid = ReadHidLayer(pid);
        int usbPhantoms = hid.PhantomCount + CountUsbPhantoms(pid);

        DriverHealthChecker.CollectHidLayer(pid, out var hidService, out var hidFilters);

        // Whatever the registry actually names, verbatim: on the live 0323 the
        // stack carries BOTH "MagicMouseDriver204Scroll" (running, the real
        // scroll filter) and an older "MagicMouseDriver" (stopped leftover).
        // Windows loads every name in LowerFilters, so the effective filter is
        // the one that is running; taking the first name in registry order read
        // the leftover's state and reported a dead wheel on a healthy mouse.
        var candidates = CollectFilterCandidates(bth, hidService, hidFilters);

        // Queried lazily: a candidate that answers RUNNING wins outright
        // (ResolveBoundFilter), so the names after it are never spawned in the
        // healthy case. Only the all-negative path needs every answer, because
        // telling "none of them runs" from "one of them did not answer" needs
        // the whole list; the cache keeps even that to one sc.exe per distinct
        // name for the whole sweep.
        var states = new List<(string Name, bool? Running)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var running = KernelServiceRunning(candidate, serviceCache);
            states.Add((candidate, running));
            if (running == true)
                break;
        }

        bool? serviceRunning = ResolveBoundFilter(states, out var bound);

        bool? packagePresent;
        if (bound is null)
        {
            // Nothing filter-like is bound, so there is no service to ask about:
            // fall back to the package probes to tell "not installed" apart from
            // "installed but not bound".
            packagePresent = v3
                ? DriverHealthChecker.KmdfPackagePresent()
                : DriverHealthChecker.AppleFilterPackagePresent();
        }
        else
        {
            packagePresent = ServicePackagePresent(bound);
        }

        // Registration is not attachment. LowerFilters plus a RUNNING service
        // both read healthy after a reboot that rebuilt the stack without the
        // filter, so the live device stack is asked directly - the same property
        // scripts/capture-state.ps1:152-157 measures. Only asked when there is
        // something to prove: no live BTHENUM instance, or nothing bound, means
        // a PC with no Apple mouse never pays for a CM query.
        bool? filterInStack = bth.InstanceCount > 0 && bound is not null
            ? DeviceStackReader.ContainsFilter(pid, bound)
            : null;

        // Same "do not pay for devices this PC never had" discipline as the
        // stack query above: both probes are skipped entirely unless there is
        // a live BTHENUM instance for this PID.
        //
        // PointerChildLive asks PnP whether the Bluetooth COL01 HID child of
        // this PID resolves as present. It is deliberately not the Col01Present
        // substring flag: charge-cable phantom HID\VID_05AC...COL01 keys make
        // that flag read true on the reference PC even with no Bluetooth
        // pointer child at all.
        bool? pointerChildLive = bth.InstanceCount > 0
            ? DeviceDiagReader.PointerChildLive(pid)
            : null;

        // MultitouchAdvancing is POSITIVE evidence only: true means the bound
        // filter's ACL translate counter moved between two samples, so the
        // multitouch stream really is flowing. It never returns false, because
        // a counter standing still cannot tell an idle mouse from a broken one
        // (and Diag\LastAclReceived, which looks like it could, was measured
        // oscillating 23/9 on a mouse whose wheel was working).
        bool? multitouchAdvancing = bth.InstanceCount > 0 && bound is not null
            ? DeviceDiagReader.MultitouchAdvancing(bound)
            : null;

        // Whatever the poller last saw for this PID, sentinels included; null
        // when nothing has been measured. This reader opens no HID handle.
        int? lastBatteryPct =
            lastBatteryByPid is not null && lastBatteryByPid.TryGetValue(pid, out var pct)
                ? pct
                : null;

        // Null means there is no enabled_<pid> line at all, which the planner
        // reads as "this PC does not own this catalog device".
        bool? configEnabled = config.HasDeviceEnabledEntry(pid)
            ? config.IsDeviceEnabled(pid)
            : (bool?)null;

        // bound/svc are the RESOLVED service and its state, cands the whole
        // family list, so a phantom "stopped" report can be traced back to the
        // exact service name it came from. A PID with nothing on this machine
        // and no config entry is not this PC's device: logging it once per
        // catalog entry buried the two real devices under 22 useless lines.
        if (bth.InstanceCount > 0 || usbPhantoms > 0 || configEnabled is not null)
        {
            // "unknown" spelled out rather than omitted: a missing field in a
            // log line is indistinguishable from a field that read false.
            Logger.Log(
                $"REPAIR_SNAPSHOT pid={pid} bt={bth.InstanceCount} usb={usbPhantoms} "
                + $"bound={bound ?? "none"} svc={Tri(serviceRunning)} pkg={Tri(packagePresent)} "
                + $"cands={(candidates.Count > 0 ? string.Join(",", candidates) : "none")} "
                + $"pointer={Tri(pointerChildLive)} mt_advancing={Tri(multitouchAdvancing)} "
                + $"watcher={Tri(watcherHealthy)} "
                + $"batt={(lastBatteryPct?.ToString() ?? "unknown")}");
        }

        return new DeviceSnapshot(
            pid,
            bth.InstanceCount,
            usbPhantoms,
            hid.Col01,
            hid.Col02,
            bound,
            packagePresent,
            serviceRunning,
            configEnabled,
            // The planner needs the whole family list, not just the resolved
            // name, to spot a stale rival filter registered beside the running
            // one; verbatim casing because a repair writes these names back.
            [.. candidates],
            // Tri-state: null means the stack could not be read, which the
            // planner must not confuse with "the filter is not attached".
            filterInStack,
            // Tri-state: null means "no COL01 child key for this PID, or the
            // lookup failed", which must never be read as "the pointer is
            // gone".
            pointerChildLive,
            // true only. null covers first sample, unchanged counter, nothing
            // bound and every failure.
            multitouchAdvancing,
            // Machine-wide watcher state, read once per sweep.
            watcherHealthy,
            lastBatteryPct);
    }

    static string Tri(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => "unknown",
    };

    // Every filter-family service named on the live stack, kept verbatim so a
    // repair acts on the names the device really loads. Order is the caller's
    // precedence: KMDF family before Apple family, and within a family the
    // registry collection order (BTHENUM LowerFilters, HID LowerFilters, HID
    // Service, BTHENUM Service). Duplicates are dropped case-insensitively,
    // first spelling wins. Nothing filter-like bound -> empty. No text
    // heuristics: the wording of DeviceDesc / FriendlyName proves nothing.
    static IReadOnlyList<string> CollectFilterCandidates(
        BthLayer bth, string? hidService, string[] hidFilters)
    {
        var raw = new List<string?>(bth.Filters.Length + hidFilters.Length + 2);
        raw.AddRange(bth.Filters);
        raw.AddRange(hidFilters);
        raw.Add(hidService);
        raw.Add(bth.Service);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kmdf = new List<string>();
        List<string>? apple = null;
        foreach (var candidate in raw)
        {
            var name = candidate?.Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
                continue;
            if (RepairPlanner.IsKmdfFamily(name))
                kmdf.Add(name);
            else if (RepairPlanner.IsAppleFamily(name))
                (apple ??= new List<string>()).Add(name);
        }

        if (apple is not null)
            kmdf.AddRange(apple);
        return kmdf;
    }

    // Which filter on this stack is the effective one, and is it running?
    // Pure: the live sc.exe queries are done by the caller and handed in as the
    // tri-state each one answered, in the stack's own precedence order. The
    // caller may stop querying at the first RUNNING answer, so the list can end
    // there - which changes nothing below, since that answer wins outright.
    //
    // Registry order is not precedence of truth - the reference 0323 names the
    // stopped leftover "MagicMouseDriver" first and the running
    // "MagicMouseDriver204Scroll" second - so a candidate that ANSWERED
    // RUNNING wins outright, whatever any other query said or failed to say.
    // Past that:
    //   nothing filter-like on the stack  - nothing was asked, nothing is
    //                                       bound and nothing is claimed.
    //   every query answered, none running - a measured false, which is the
    //                                       USB-C charge incident rule 1
    //                                       exists for. bound falls back to the
    //                                       first name so a repair has a
    //                                       service to act on.
    //   any query did not answer           - unknown: the name that failed
    //                                       could be the running filter, and
    //                                       calling that "stopped" is what let
    //                                       a healthy mouse be offered an
    //                                       elevated stack restart.
    internal static bool? ResolveBoundFilter(
        IReadOnlyList<(string Name, bool? Running)> candidates, out string? bound)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Running == true)
            {
                bound = candidate.Name;
                return true;
            }
        }

        if (candidates.Count == 0)
        {
            bound = null;
            return null;
        }

        bound = candidates[0].Name;
        foreach (var candidate in candidates)
        {
            if (candidate.Running is null)
                return null;
        }
        return false;
    }

    // sc query spawns a process, so every distinct name is asked at most once
    // per menu open - a stack with two family filters must not double the cost.
    // The cache holds the tri-state answer, so a query that failed is not
    // retried as if it had come back "stopped".
    static bool? KernelServiceRunning(string serviceName, Dictionary<string, bool?> cache)
    {
        if (cache.TryGetValue(serviceName, out var running))
            return running;
        running = DriverHealthChecker.KernelServiceRunning(serviceName);
        cache[serviceName] = running;
        return running;
    }

    // Present means the service key exists AND the .sys it would load - the one
    // its ImagePath names, or the drivers-directory default the SCM falls back
    // to - is on disk. A leftover key whose file was deleted is not a usable
    // package.
    //
    // keyPresent: true the key opened, false there is no such key on this PC,
    //             null the registry read threw.
    // imageOnDisk: true the resolved ImagePath was found, false it was not,
    //              null there was no path to test at all - ImagePath was
    //              unparseable, or absent on a service that has no default.
    // A read that threw must not be reported as "not installed": rule 6 sends
    // that user to install a driver that may well be there already.
    internal static bool? InterpretServicePackage(bool? keyPresent, bool? imageOnDisk)
    {
        if (keyPresent is null)
            return null;
        if (keyPresent == false)
            return false;
        return imageOnDisk;
    }

    static bool? ServicePackagePresent(string serviceName)
    {
        bool? keyPresent = null;
        bool? imageOnDisk = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                ServicesBase + "\\" + serviceName, writable: false);
            keyPresent = key is not null;
            if (key is not null)
            {
                var image = ResolveServiceImage(
                    serviceName, key.GetValue("ImagePath"), key.GetValue("Type") as int?);
                if (image is not null)
                    imageOnDisk = File.Exists(image);
            }
        }
        catch
        {
            // Whatever was learned before the throw stands, and the rest stays
            // unknown: a failed read leaves the fields null, which the snapshot
            // log prints as pkg=unknown.
        }

        return InterpretServicePackage(keyPresent, imageOnDisk);
    }

    // Which file this service would load, or null when that cannot be decided.
    //
    // An ABSENT ImagePath is not a failed read: for a driver service the SCM
    // loads %SystemRoot%\System32\drivers\<ServiceName>.sys instead, so the
    // answer is still computable and the probe stays measured. KB103000
    // ("CurrentControlSet\Services Subkey Entries") documents that default
    // under ImagePath and Type, and it is a DRIVER default: a Win32 service
    // with no ImagePath has nothing to fall back on.
    //
    // A value that is PRESENT but unparseable - a blob instead of a string, or
    // a string ResolveDriverImagePath refuses - is a different state: the key
    // named something and we could not read it, which stays unknown. An empty
    // or whitespace value names nothing, so it counts as absent.
    internal static string? ResolveServiceImage(
        string serviceName, object? declaredImagePath, int? serviceType) =>
        declaredImagePath switch
        {
            string declared when !string.IsNullOrWhiteSpace(declared)
                => ResolveDriverImagePath(declared),
            null or string => DefaultDriverImage(serviceName, serviceType),
            _ => null,
        };

    // SERVICE_KERNEL_DRIVER (1) and SERVICE_FILE_SYSTEM_DRIVER (2) are the two
    // types the drivers-directory default applies to; every other type, and a
    // Type that is missing or not a DWORD, leaves the question unanswered.
    static string? DefaultDriverImage(string serviceName, int? serviceType)
    {
        if (serviceType is not (1 or 2) || string.IsNullOrWhiteSpace(serviceName))
            return null;
        return UnderWindows(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\drivers\" + serviceName + ".sys");
    }

    // ImagePath forms seen in the wild: "\SystemRoot\System32\drivers\x.sys",
    // "System32\drivers\x.sys", "\??\C:\dir\x.sys" and plain absolute paths.
    static string? ResolveDriverImagePath(string? imagePath)
    {
        var path = imagePath?.Trim().Trim('"');
        if (string.IsNullOrEmpty(path))
            return null;

        if (path.Contains('%'))
            path = Environment.ExpandEnvironmentVariables(path);
        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            path = path[4..];

        const string systemRootPrefix = @"\SystemRoot\";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (path.StartsWith(systemRootPrefix, StringComparison.OrdinalIgnoreCase))
            return UnderWindows(windows, path[systemRootPrefix.Length..]);

        // Drive-qualified or UNC: already absolute.
        if ((path.Length >= 2 && path[1] == ':')
            || path.StartsWith(@"\\", StringComparison.Ordinal))
            return path;

        return UnderWindows(windows, path);
    }

    static string? UnderWindows(string windows, string relative)
    {
        var rest = relative.TrimStart('\\', '/');
        if (string.IsNullOrEmpty(windows) || string.IsNullOrEmpty(rest))
            return null;
        return Path.Combine(windows, rest);
    }

    static BthLayer ReadBthenumLayer(string pid)
    {
        using var root = Registry.LocalMachine.OpenSubKey(BtEnumBase, writable: false);
        if (root is null)
            return new BthLayer(0, null, []);

        int instanceCount = 0;
        string? service = null;
        var filters = new List<string>();

        foreach (var subkeyName in root.GetSubKeyNames())
        {
            if (!BthenumKeyMatchesPid(subkeyName, pid))
                continue;

            using var deviceKey = root.OpenSubKey(subkeyName, writable: false);
            if (deviceKey is null) continue;

            filters.AddRange(ReadFilters(deviceKey));

            foreach (var instanceName in deviceKey.GetSubKeyNames())
            {
                instanceCount++;
                using var instance = deviceKey.OpenSubKey(instanceName, writable: false);
                if (instance is null) continue;

                if (instance.GetValue("Service") is string svc
                    && !string.IsNullOrEmpty(svc)
                    && string.IsNullOrEmpty(service))
                    service = svc;

                filters.AddRange(ReadFilters(instance));
            }
        }

        return new BthLayer(instanceCount, service, [.. filters]);
    }

    // TryParseAppleHidKey with skipNonScroll:false keeps keyboards and trackpads
    // in; the fallback covers Apple keys it does not recognise as HID-profile.
    internal static bool BthenumKeyMatchesPid(string subkeyName, string pid)
    {
        if (DriverHealthChecker.TryParseAppleHidKey(subkeyName, out var parsed, skipNonScroll: false))
            return string.Equals(parsed, pid, StringComparison.OrdinalIgnoreCase);

        foreach (var seg in AppleVidSegments)
        {
            if (subkeyName.Contains(seg, StringComparison.OrdinalIgnoreCase)
                && subkeyName.Contains("_PID&" + pid, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Enum\HID: COL01/COL02 collections of the live BT stack, plus any
    // VID_05AC&PID_xxxx nodes left behind by a charging cable.
    static HidLayer ReadHidLayer(string pid)
    {
        using var root = Registry.LocalMachine.OpenSubKey(HidEnumBase, writable: false);
        if (root is null)
            return new HidLayer(0, false, false);

        int phantoms = 0;
        bool col01 = false, col02 = false;

        foreach (var subkeyName in root.GetSubKeyNames())
        {
            if (subkeyName.IndexOf(pid, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            bool phantom = IsUsbStyleNode(subkeyName, pid);
            col01 |= HasCollection(subkeyName, "COL01");
            col02 |= HasCollection(subkeyName, "COL02");

            using var deviceKey = root.OpenSubKey(subkeyName, writable: false);
            string[] instances = deviceKey?.GetSubKeyNames() ?? [];

            foreach (var instanceName in instances)
            {
                col01 |= HasCollection(instanceName, "COL01");
                col02 |= HasCollection(instanceName, "COL02");
            }

            if (phantom)
                phantoms += instances.Length > 0 ? instances.Length : 1;
        }

        return new HidLayer(phantoms, col01, col02);
    }

    static int CountUsbPhantoms(string pid)
    {
        using var root = Registry.LocalMachine.OpenSubKey(UsbEnumBase, writable: false);
        if (root is null) return 0;

        int phantoms = 0;
        foreach (var subkeyName in root.GetSubKeyNames())
        {
            if (!IsUsbStyleNode(subkeyName, pid))
                continue;

            using var deviceKey = root.OpenSubKey(subkeyName, writable: false);
            string[] instances = deviceKey?.GetSubKeyNames() ?? [];
            phantoms += instances.Length > 0 ? instances.Length : 1;
        }
        return phantoms;
    }

    static bool IsUsbStyleNode(string subkeyName, string pid) =>
        subkeyName.Contains("VID_05AC", StringComparison.OrdinalIgnoreCase)
        && subkeyName.Contains("PID_" + pid, StringComparison.OrdinalIgnoreCase);

    static bool HasCollection(string name, string collection) =>
        name.Contains(collection, StringComparison.OrdinalIgnoreCase);

    // Known mice first (the two field incidents live there), then the rest of the
    // battery catalog. Config keys cannot be enumerated (the map is private and
    // this file adds nothing to Config), so the catalog is the whole sweep.
    static IReadOnlyList<string> CatalogPids()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pids = new List<string>();

        foreach (var pid in DriverHealthChecker.KnownMousePids)
            AddPid(pids, seen, pid);
        foreach (var entry in MouseBatteryDevice.KnownMice)
            AddPid(pids, seen, TrailingPid(entry.PidPattern));
        foreach (var entry in KeyboardBatteryDevice.KnownKeyboards)
            AddPid(pids, seen, TrailingPid(entry.PidPattern));

        return pids;
    }

    static void AddPid(List<string> pids, HashSet<string> seen, string pid)
    {
        if (pid.Length == 4 && seen.Add(pid))
            pids.Add(pid);
    }

    // "PID&0323" / "PID_0323" -> "0323"
    static string TrailingPid(string pattern) =>
        pattern.Length < 4 ? "" : pattern[^4..].ToLowerInvariant();

    // LowerFilters is REG_MULTI_SZ or REG_SZ depending on who wrote it.
    static string[] ReadFilters(RegistryKey key)
    {
        var value = key.GetValue("LowerFilters");
        if (value is string[] arr) return arr;
        if (value is string s && !string.IsNullOrEmpty(s)) return [s];
        return [];
    }
}
