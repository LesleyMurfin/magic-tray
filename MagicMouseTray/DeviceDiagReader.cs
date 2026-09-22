// SPDX-License-Identifier: MIT
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MagicMouseTray;

// Per-capability health evidence the registration-only reader cannot supply:
// is the multitouch report stream actually MOVING, is the Bluetooth pointer
// child actually PRESENT, and is the driver package's F1 watcher installed and
// alive. Read-only throughout: registry reads plus one capped tail read of the
// watcher log. No process spawn, no HID open, no writes - this runs from the
// snapshot read on menu open and on poll ticks.
//
// Every member is tri-state. null means "no evidence" and the planner must
// never turn it into a finding; nagging a healthy PC is the failure mode this
// whole file is shaped around.
internal static class DeviceDiagReader
{
    const string ServicesBase = @"SYSTEM\CurrentControlSet\Services";
    const string HidEnumBase = @"SYSTEM\CurrentControlSet\Enum\HID";

    // Bluetooth HID-profile transport GUID. A live BT HID child key is
    //   {00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0323&Col01
    // (docs/ENABLE-DISABLE.md, "Two scroll drivers registered on one mouse",
    // shows the same device-key form under BTHENUM in its worked example;
    // the Enum\HID children append a collection suffix only when the device
    // splits its collections - see ClassifyPointerKey for the v1 shape, which
    // has exactly one collection and therefore no suffix).
    const string BtTransportGuid = "{00001124-0000-1000-8000-00805f9b34fb}";

    // NORMAL = present devices only, same reason as DeviceStackReader's CM_LOCATE_DEVNODE_NORMAL:
    // a charge-cable phantom must never be allowed to answer a question about
    // the live stack.
    const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    const uint CR_SUCCESS = 0x00000000;

    internal const string WatcherDataDir = @"C:\ProgramData\MagicMouseDriver";
    const string WatcherScriptName = "mm-auto-f1-watcher.ps1";
    const string WatcherLogName = "auto-f1-watcher.log";

    // The log grows one heartbeat line every 5 minutes plus F1 lines, so the
    // newest heartbeat and the newest SetFeature result are always inside the
    // last few KB. 64 KB is the cap: never load the file whole.
    const int LogTailBytes = 64 * 1024;

    // The watcher's FATAL text is one short line, but it is quoted into a
    // dialog, so it is capped rather than trusted to be short. Well past any
    // line the watcher writes; only a corrupt log can reach it.
    const int MaxFatalReasonChars = 120;

    // WatcherState is the only member that touches the filesystem, and it is
    // called from the same snapshot read as the registry probes - i.e. up to
    // once per second. The watcher writes a heartbeat every 5 minutes, so a
    // 30 s cache costs no freshness that any consumer can observe.
    static readonly TimeSpan WatcherCacheTtl = TimeSpan.FromSeconds(30);
    static readonly object WatcherLock = new();
    static F1WatcherState? _watcherCache;
    static DateTime _watcherCacheAtUtc;

    // Previous counter sample per filter service name, with the time it was
    // taken. MultitouchAdvancing is a delta measurement, so it needs history -
    // but a snapshot sweep reads the same PID several times within one second
    // (menu Opening, then the poll tick, then AfterFindingHandled). Overwriting
    // the baseline on every one of those reads compared 169098 against 169098
    // and reported "no evidence" on a mouse that was actively scrolling, which
    // is exactly what the live log showed. The baseline is therefore only
    // replaced once it is at least BaselineMinAge old, so bursts measure
    // against a sample far enough back for the counter to have moved.
    static readonly TimeSpan BaselineMinAge = TimeSpan.FromSeconds(2);

    // Once movement is proven, it stays proven for this long. The counter only
    // climbs while a hand is on the mouse, so without this the same healthy
    // device would alternate between "working" and "not verified" from one menu
    // open to the next. This is a claim about the recent past, which is all a
    // delta can ever support.
    static readonly TimeSpan AliveMemory = TimeSpan.FromSeconds(60);

    readonly record struct CounterSample(long Value, DateTime AtUtc);

    static readonly object SampleLock = new();
    static readonly Dictionary<string, CounterSample> BaselineByService =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, DateTime> LastAdvanceAtUtc =
        new(StringComparer.OrdinalIgnoreCase);

    // true = the driver's multitouch translate counter INCREASED since our
    //        previous sample. That is positive proof the multitouch stream is
    //        flowing right now; nothing else in the tray can prove it.
    // null = no evidence: first sample, counter unchanged, counter went
    //        backwards (driver reloaded / counter reset), missing key or value,
    //        a service name outside the driver family, or any failure.
    // NEVER false. "Counter not advancing" cannot distinguish an idle mouse -
    // a hand off the mouse for two seconds - from a broken one, and the two are
    // byte-identical in the registry. Returning false there would fire a
    // finding on every user who is not touching their mouse.
    //
    // Why not Diag\LastAclReceived, which looks like the obvious signal:
    // measured on the reference PC (Lesleys-PC, PID 0323) with scroll WORKING,
    // sampled 4x at 2.5 s intervals -
    //   t=1 LastAclReceived=23  AclTranslateCount=142271
    //   t=2 LastAclReceived=23  AclTranslateCount=142513
    //   t=3 LastAclReceived=9   AclTranslateCount=142614
    //   t=4 LastAclReceived=23  AclTranslateCount=142827
    // It oscillates 23/9 on a healthy device because 9 only means the last ACL
    // frame happened to be short. Mapping 9 to "multitouch off" would raise a
    // false finding whenever a sample landed on a 9 - the same class of error as
    // the rejected zeroed GET_REPORT(Input, 0x90) pull on COL02. The value is
    // also last-seen, not timestamped, and REG-persisted across reboot, so it
    // can read "healthy" after a boot during which no ACL ever arrived; the
    // driver repo's own watcher refuses to gate on it for that reason
    // (mm-auto-f1-watcher.ps1:140-146). It is therefore deliberately unused.
    //
    // The Diag key itself is rewritten wholesale every 1000 ms by a WDF timer
    // -> work item in the filter (Driver.c:164-180, 741-748, 862-953). That
    // cadence is why consecutive snapshot reads can see a moved counter at all.
    internal static bool? MultitouchAdvancing(string boundFilterName)
    {
        if (!IsSafeFamilyServiceName(boundFilterName))
            return null;

        try
        {
            var (counter, value) = ReadCounter(boundFilterName);
            if (counter is null || value is null)
            {
                Logger.Log($"DEVICE_DIAG_MT svc={boundFilterName} advancing=unknown counter=none");
                return null;
            }

            var now = DateTime.UtcNow;
            var advancing = EvaluateCounterSample(boundFilterName, value.Value, now,
                out var previous, out var lastAdvance);

            Logger.Log($"DEVICE_DIAG_MT svc={boundFilterName} advancing={Describe(advancing)} "
                + $"counter={counter} value={value.Value} prev={(previous is null ? "none" : previous.Value)} "
                + $"last_advance={(lastAdvance is null ? "never" : $"{(int)(now - lastAdvance.Value).TotalSeconds}s")}");
            return advancing;
        }
        catch (Exception ex)
        {
            // Registry security, a hive that went away under us - we learned
            // nothing, which is null, not a fault.
            Logger.Log($"DEVICE_DIAG_MT_FAILED svc={boundFilterName} err={ex.Message}");
            return null;
        }
    }

    // The whole multitouch decision, with the registry left outside: given the
    // counter value this read saw and the time it was read, fold it into the
    // per-service sampling state and answer the tri-state above. The registry
    // is the only untestable part of MultitouchAdvancing, so it is the only
    // part that stays there; this is where the burst/baseline/alive-memory
    // rules live and the only place they live.
    //
    // previous / lastAdvanceAtUtc are what the caller's log line needs, handed
    // back rather than recomputed: the decision must not be made twice.
    // Tests drive this directly with an injected nowUtc, and isolate through
    // distinct service names - there is deliberately no way to clear the state,
    // because nothing in a real run may ever forget a proven advance.
    internal static bool? EvaluateCounterSample(string service, long value, DateTime nowUtc,
        out long? previous, out DateTime? lastAdvanceAtUtc)
    {
        bool? advancing = null;
        previous = null;
        lastAdvanceAtUtc = null;

        lock (SampleLock)
        {
            if (BaselineByService.TryGetValue(service, out var baseline))
            {
                previous = baseline.Value;
                if (value > baseline.Value)
                {
                    advancing = true;
                    LastAdvanceAtUtc[service] = nowUtc;
                    BaselineByService[service] = new CounterSample(value, nowUtc);
                }
                else if (nowUtc - baseline.AtUtc >= BaselineMinAge)
                {
                    // A real interval passed with no movement: no evidence,
                    // and the baseline moves forward so the next read is
                    // measured against something recent. This is also how a
                    // counter that went BACKWARDS is recovered from - the new
                    // lower value becomes the baseline to beat.
                    BaselineByService[service] = new CounterSample(value, nowUtc);
                }
                // Otherwise this is a burst read - keep the older baseline.
            }
            else
            {
                BaselineByService[service] = new CounterSample(value, nowUtc);
            }

            if (LastAdvanceAtUtc.TryGetValue(service, out var seen))
            {
                lastAdvanceAtUtc = seen;
                if (advancing is null && nowUtc - seen <= AliveMemory)
                    advancing = true;
            }
        }

        return advancing;
    }

    // true  = the Bluetooth-transport pointer HID child for this PID resolves
    //         as PRESENT through CM. The pointer child is live.
    // false = such a key exists in Enum\HID but no instance of it resolves as
    //         present - the registration is there and the devnode is not.
    // null  = there is no pointer child key for this PID at all, the candidate
    //         keys are AMBIGUOUS (see SelectPointerKeys), or the walk failed.
    //         Never a finding.
    //
    // USB / HID\VID_05AC...&MI_..&COL01 charge-cable phantoms are EXCLUDED.
    // After a USB-C charge Windows leaves phantom
    // HID\VID_05AC&PID_xxxx&MI_yy&COLzz interfaces behind, all CM_PROB_PHANTOM,
    // so a phantom COL01 exists on the reference PC after any charge. That is
    // exactly why the older substring flags Col01Present/Col02Present read true
    // even when the Bluetooth pointer child is gone
    // (DeviceSnapshotReader.ReadHidLayer matches COL01 across ALL Enum\HID
    // subkeys for the PID). Presence is therefore resolved through
    // CM_Locate_DevNodeW, never through key existence alone.
    internal static bool? PointerChildLive(string pid)
    {
        if (string.IsNullOrEmpty(pid))
            return null;

        int keys = 0;
        int present = 0;
        bool? live = null;
        var selection = PointerKeySet.Empty;

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(HidEnumBase, writable: false);
            if (root is not null)
            {
                // The whole walk is decided before a single device key is
                // opened: which node is the pointer child is a question about
                // the SET of keys for this PID, not about one key in isolation.
                selection = SelectPointerKeys(root.GetSubKeyNames(), pid);

                foreach (var deviceKeyName in selection.Keys)
                {
                    using var deviceKey = root.OpenSubKey(deviceKeyName, writable: false);
                    if (deviceKey is null)
                        continue;

                    foreach (var instanceName in deviceKey.GetSubKeyNames())
                    {
                        keys++;
                        // Instance id of an Enum\HID child is
                        // HID\<device key>\<instance key>.
                        var instanceId = @"HID\" + deviceKeyName + "\\" + instanceName;
                        if (CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS)
                            present++;
                    }
                }
            }

            // Only a key we actually found can prove absence of the devnode.
            // An ambiguous selection contributes no keys, so it can only ever
            // come out as null - it must never reach PointerChildMissing.
            if (keys > 0)
                live = present > 0;
        }
        catch (Exception ex)
        {
            // DllNotFoundException / EntryPointNotFoundException on a stripped
            // Windows, registry security, a key deleted mid-walk.
            Logger.Log($"DEVICE_DIAG_POINTER_FAILED pid={pid} err={ex.Message}");
            return null;
        }

        // keys=N present=N are unchanged in name and meaning (instance nodes
        // examined / resolved). shape and the two candidate counts are what
        // make the v1-vs-v3 key layout readable in the live log: a pid that
        // reads live=unknown shape=ambiguous is a selection refusal, while
        // live=unknown shape=none is genuinely no key at all.
        Logger.Log($"DEVICE_DIAG_POINTER pid={pid} live={Describe(live)} keys={keys} present={present} "
            + $"shape={selection.Shape} col01={selection.Col01Count} nocol={selection.SoleCount}");
        return live;
    }

    // What the driver package's multitouch watcher looks like from outside.
    // The tray never sends F1 and never reimplements the watcher
    // (docs/ENABLE-DISABLE.md, "Magic Tray does not send F1, and must not
    // start", makes a second sender an explicit non-goal); it may only READ
    // this state and recommend.
    //
    // Installed       - mm-auto-f1-watcher.ps1 present in C:\ProgramData\
    //                   MagicMouseDriver\, where mm-auto-f1-watcher-install.ps1
    //                   copies it. false means we looked and it is not there;
    //                   null means the look itself failed. No schtasks spawn,
    //                   no admin.
    // LastHeartbeatUtc- newest "heartbeat alive" line (the watcher writes one
    //                   every 5 minutes), converted from LOCAL time - see
    //                   ParseStamp.
    // LastF1Ok        - newest "SetFeature ok=True|False" line, which the
    //                   watcher relays from mm-f1-once.ps1.
    // FatalReason     - the watcher's OWN last words, verbatim, when it exited
    //                   by printing a FATAL line and wrote nothing afterwards -
    //                   e.g. "FATAL missing F1 script", which it prints when
    //                   C:\mm-dev-queue\mm-f1-once.ps1 is absent (driver-repo
    //                   issue #34). null means no such line, which is NOT
    //                   evidence of health: a silent watcher and a dead one are
    //                   different states and the three are kept apart here.
    //                   Installed+heartbeat = running; Installed+no heartbeat+no
    //                   FatalReason = silent, cause unknown; Installed+
    //                   FatalReason = dead, for the reason it published itself.
    internal sealed record F1WatcherState(
        bool? Installed,
        DateTime? LastHeartbeatUtc,
        bool? LastF1Ok,
        string? FatalReason = null);

    static readonly F1WatcherState UnknownWatcher = new(null, null, null);

    internal static F1WatcherState WatcherState()
    {
        lock (WatcherLock)
        {
            if (_watcherCache is not null && DateTime.UtcNow - _watcherCacheAtUtc < WatcherCacheTtl)
                return _watcherCache;
        }

        var state = ReadWatcherState();

        lock (WatcherLock)
        {
            _watcherCache = state;
            _watcherCacheAtUtc = DateTime.UtcNow;
        }

        Logger.Log($"DEVICE_DIAG_WATCHER installed={Describe(state.Installed)} "
            + $"heartbeat={(state.LastHeartbeatUtc is null ? "none" : state.LastHeartbeatUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))} "
            + $"f1_ok={Describe(state.LastF1Ok)} "
            + $"fatal={(state.FatalReason is null ? "none" : state.FatalReason)}");
        return state;
    }

    static F1WatcherState ReadWatcherState()
    {
        bool? installed;
        try
        {
            // File.Exists is false - not an exception - for a missing directory,
            // so an absent C:\ProgramData\MagicMouseDriver is simply "not
            // installed". null is reserved for a probe that could not run.
            installed = File.Exists(Path.Combine(WatcherDataDir, WatcherScriptName));
        }
        catch
        {
            installed = null;
        }

        try
        {
            return ScanWatcherLog(installed, ReadLogTail());
        }
        catch (Exception ex)
        {
            // Missing file, missing directory, a lock we could not share, an
            // unreadable sector: the log told us nothing. Installed may still be
            // known, so only the log-derived fields go null. An unreadable log
            // is never a fault - it is the absence of evidence.
            Logger.Log($"DEVICE_DIAG_WATCHER_LOG_FAILED err={ex.Message}");
            return new F1WatcherState(installed, null, null);
        }
    }

    // Split out from the file read so the watcher's line grammar can be
    // exercised with synthetic logs; nothing below touches the filesystem.
    // Lines arrive oldest-first, exactly as ReadLogTail yields them, because
    // the ORDER is load-bearing: a FATAL followed by a later heartbeat is a
    // watcher that was restarted, not a dead one.
    internal static F1WatcherState ScanWatcherLog(bool? installed, IEnumerable<string> lines)
    {
        DateTime? heartbeat = null;
        bool? f1Ok = null;
        string? fatal = null;

        foreach (var raw in lines)
        {
            // Split('\n') leaves the CR of a CRLF log attached, and the FATAL
            // text is quoted verbatim to the user, so trim once here.
            var line = raw.Trim();

            var reason = ParseFatal(line);
            if (reason is not null)
            {
                fatal = reason;
                continue;
            }

            // Newest wins, so keep scanning and overwrite: the tail is a
            // few hundred lines at most. A line with a garbage stamp leaves
            // the previous good heartbeat in place rather than erasing it.
            bool wroteAfterwards = false;
            if (line.Contains("heartbeat alive", StringComparison.OrdinalIgnoreCase))
            {
                var stamp = ParseStamp(line);
                if (stamp is not null)
                    heartbeat = stamp;
                wroteAfterwards = true;
            }

            if (line.Contains("SetFeature ok=True", StringComparison.OrdinalIgnoreCase))
            {
                f1Ok = true;
                wroteAfterwards = true;
            }
            else if (line.Contains("SetFeature ok=False", StringComparison.OrdinalIgnoreCase))
            {
                f1Ok = false;
                wroteAfterwards = true;
            }

            // The watcher printed this AFTER announcing its own death, so the
            // death was superseded by a restart. Carrying the stale FATAL
            // forward would report a dead watcher that is demonstrably alive.
            if (wroteAfterwards)
                fatal = null;
        }

        return installed is null && heartbeat is null && f1Ok is null && fatal is null
            ? UnknownWatcher
            : new F1WatcherState(installed, heartbeat, f1Ok, fatal);
    }

    // The watcher's failure grammar, as measured: "[yyyy-MM-dd HH:mm:ss] FATAL
    // missing F1 script", printed immediately before it exits because it hard-
    // depends on C:\mm-dev-queue\mm-f1-once.ps1 (driver-repo issue #34).
    //
    // Only that shape is matched, and deliberately no more: FATAL must open the
    // message body, so a heartbeat or SetFeature line that merely contains the
    // word is not read as a death, and an explanation is required, because a
    // bare "FATAL" is not a line this watcher writes - inferring a death from
    // one would be guesswork. Anything unrecognised leaves the state unknown,
    // which is not a fault.
    //
    // The explanation is returned VERBATIM. It is the watcher's claim about
    // itself, quoted, never paraphrased or interpreted by the tray.
    static string? ParseFatal(string line)
    {
        int close = line.IndexOf(']');
        var body = (line.StartsWith('[') && close > 0 ? line[(close + 1)..] : line).Trim();

        const string Marker = "FATAL";
        if (!body.StartsWith(Marker, StringComparison.Ordinal))
            return null;

        // Require real text after the marker, not just "FATAL" or "FATALITY".
        var rest = body[Marker.Length..];
        if (rest.Length == 0 || !char.IsWhiteSpace(rest[0]) || rest.Trim().Length == 0)
            return null;

        body = Marker + " " + rest.Trim();
        return body.Length <= MaxFatalReasonChars
            ? body
            : body[..MaxFatalReasonChars].TrimEnd() + "...";
    }

    // Tail only, never the whole file. FileShare.ReadWrite|Delete is required:
    // the watcher appends to this file continuously and may roll it.
    static IEnumerable<string> ReadLogTail()
    {
        var path = Path.Combine(WatcherDataDir, WatcherLogName);
        if (!File.Exists(path))
            return [];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        long start = Math.Max(0, stream.Length - LogTailBytes);
        if (start > 0)
            stream.Seek(start, SeekOrigin.Begin);

        // The watcher writes with -Encoding ASCII (mm-auto-f1-watcher.ps1:40).
        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // After a mid-file seek the first line is a fragment - drop it rather
        // than parse half a timestamp.
        return start > 0 && lines.Length > 0 ? lines[1..] : lines;
    }

    // Watcher lines are "[yyyy-MM-dd HH:mm:ss] <text>" written with plain
    // Get-Date (mm-auto-f1-watcher.ps1:39), i.e. MACHINE LOCAL time with no
    // offset and no Z. So it is parsed as local and converted, not assumed UTC;
    // guessing UTC would shift every heartbeat by the timezone offset and make
    // a live watcher look hours stale. null for a garbage or absent stamp.
    static DateTime? ParseStamp(string line)
    {
        int open = line.IndexOf('[');
        int close = line.IndexOf(']');
        if (open < 0 || close <= open + 1)
            return null;

        var text = line[(open + 1)..close].Trim();
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return null;

        return DateTime.SpecifyKind(parsed, DateTimeKind.Local).ToUniversalTime();
    }

    // AclTranslateCount is the driver's multitouch-translate counter; Rid12Count
    // counts the RID 0x12 input reports the same stream carries, and is the
    // fallback for a filter build that does not publish the former. Both are
    // REG_DWORDs in the Diag key (Driver.c:862-953).
    static (string? Name, long? Value) ReadCounter(string service)
    {
        using var diag = Registry.LocalMachine.OpenSubKey(
            ServicesBase + "\\" + service + "\\Diag", writable: false);
        if (diag is null)
            return (null, null);

        var value = ReadDword(diag, "AclTranslateCount");
        if (value is not null)
            return ("AclTranslateCount", value);

        value = ReadDword(diag, "Rid12Count");
        return value is not null ? ("Rid12Count", value) : (null, null);
    }

    // REG_DWORD marshals to int and can carry the high bit once the counter
    // passes 2^31, so it is widened through uint to stay monotonic.
    static long? ReadDword(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int i => unchecked((uint)i),
        uint u => u,
        long l => l,
        _ => null,
    };

    // Two gates before a caller-supplied string is ever concatenated into a key
    // path: it must be one of the driver families the planner knows
    // (RepairPlanner.IsKmdfFamily / IsAppleFamily), and it must look like a
    // service name - no separators, no dots, nothing that could climb out of Services\.
    static bool IsSafeFamilyServiceName(string? service)
    {
        if (string.IsNullOrEmpty(service))
            return false;
        if (!RepairPlanner.IsKmdfFamily(service) && !RepairPlanner.IsAppleFamily(service))
            return false;

        foreach (var c in service)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }
        return true;
    }

    // Which node under Enum\HID can be THE pointer child of a PID.
    //
    // Two key layouts are real, both measured on the reference PC:
    //
    //   v3 (0323) SPLITS its collections AND keeps a collection-less parent
    //     node. All three of these were enumerated live on 2026-09-15 while
    //     the user was actively using the mouse, verbatim:
    //     HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323&COL01\A&31E5D054&2A&0000
    //         Status OK,      class Mouse    - the POINTER collection.
    //     HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323&COL02\A&31E5D054&2A&0001
    //         Status OK,      class HIDClass - vendor/battery, NOT the pointer.
    //     HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323\A&31E5D054&2A&0000
    //         Status Unknown, class Mouse    - the collection-less parent.
    //     So COL01 WINS whenever it exists: it is the pointer collection by
    //     construction, while the collection-less node of a splitting device
    //     is an aggregate whose presence reads Unknown - worthless as pointer
    //     evidence, and the wrong devnode for the elevated
    //     pnputil /restart-device of PointerChildMissing. Treating that pair
    //     as "ambiguous" is what regressed this PID from live=true keys=1
    //     present=1 to live=unknown keys=0 present=0 while its pointer worked.
    //     COL02 stays rejected outright: a healthy battery channel must never
    //     vouch for a dead cursor.
    //
    //   v1 (030D) exposes exactly ONE collection, so Windows writes NO
    //     collection suffix at all. Captured live 2026-09-15, pointer and
    //     scroll both confirmed working by the user at that moment:
    //     HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&000205AC_PID&030D\A&137E1BF2&9&0000
    //     i.e. device key "{00001124-...}_VID&000205AC_PID&030D", instance
    //     "A&137E1BF2&9&0000". Requiring COL01 made this shape match nothing
    //     and the log read "live=unknown keys=0 present=0" forever, so a v1/v2
    //     owner could never be told the pointer works. The collection-less
    //     node is therefore a FALLBACK: it is read only when no COL01 exists
    //     for the PID, and only when it is the single such candidate.
    //
    // The HID\VID_05AC... / &MI_ / USB\ forms are the USB charge-cable
    // phantoms and are rejected outright - they are the whole reason this is a
    // predicate on the key SHAPE instead of a COL01 substring test.
    internal enum PointerKeyKind
    {
        // Not a pointer child: wrong PID, non-Apple VID, non-Bluetooth
        // transport, a charge-cable phantom, or COL02 and higher.
        None,
        // The first collection of a device that splits them (v3).
        Col01,
        // The single collection-less node of a device that has just one (v1/v2).
        SoleCollection,
    }

    internal static PointerKeyKind ClassifyPointerKey(string? deviceKeyName, string pid)
    {
        if (string.IsNullOrEmpty(deviceKeyName) || string.IsNullOrEmpty(pid))
            return PointerKeyKind.None;

        // Transport first. VID_ (as opposed to the Bluetooth _VID& form) is the
        // USB enumerator's spelling, so it also catches a non-Apple
        // HID\VID_046D... node that happens to sit in the same hive.
        if (deviceKeyName.Contains("VID_", StringComparison.OrdinalIgnoreCase)
            || deviceKeyName.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
            || deviceKeyName.Contains(@"USB\", StringComparison.OrdinalIgnoreCase))
            return PointerKeyKind.None;

        if (!deviceKeyName.Contains(BtTransportGuid, StringComparison.OrdinalIgnoreCase))
            return PointerKeyKind.None;

        // Apple VID plus this exact PID, through the one BTHENUM matcher the
        // repo already has (DeviceSnapshotReader.BthenumKeyMatchesPid, which covers both
        // _VID&000205ac_ and _VID&0001004c_ via DriverHealthChecker's
        // AppleVidSegments). No second copy of the VID table lives here.
        if (!DeviceSnapshotReader.BthenumKeyMatchesPid(deviceKeyName, pid))
            return PointerKeyKind.None;

        int col = deviceKeyName.LastIndexOf("&COL", StringComparison.OrdinalIgnoreCase);
        if (col < 0)
            return PointerKeyKind.SoleCollection;

        return IsCollectionOne(deviceKeyName.AsSpan(col + 4))
            ? PointerKeyKind.Col01
            : PointerKeyKind.None;
    }

    // "01" is the pointer collection. Parsed rather than string-compared so
    // Col1/Col001 cannot slip past, and NumberStyles.None keeps signs and
    // whitespace out: anything that is not plain digits worth 1 is not COL01.
    static bool IsCollectionOne(ReadOnlySpan<char> suffix) =>
        int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n == 1;

    // The chosen pointer-child device keys for a PID, plus what the candidate
    // field looked like so the log line can say why. Col01Count and SoleCount
    // are what was SEEN, not what was taken: shape=col01 col01=1 nocol=1 is
    // the live v3, where the collection-less aggregate was seen and ignored.
    //   Shape "col01"     - a COL01 key exists; the COL01 key(s) are it, and
    //                       any collection-less node is ignored.
    //   Shape "sole"      - no COL01 for this PID and exactly one distinct
    //                       collection-less key; that key is it.
    //   Shape "ambiguous" - no COL01 and two or more distinct collection-less
    //                       keys. Nothing to prefer, so no evidence.
    //   Shape "none"      - no candidate at all.
    internal readonly record struct PointerKeySet(
        string[] Keys, int Col01Count, int SoleCount, string Shape)
    {
        internal static readonly PointerKeySet Empty = new PointerKeySet([], 0, 0, "none");
    }

    // COL01 is the pointer collection by construction, so it takes precedence
    // unconditionally: on a splitting device the collection-less sibling is an
    // aggregate parent whose presence reads Unknown (see PointerKeyKind for the
    // three live v3 keys), which is no evidence about the cursor and the wrong
    // devnode to restart. The collection-less node is only the FALLBACK for the
    // single-collection v1/v2 shape, where Windows wrote no suffix at all.
    //
    // Ambiguity is reserved for the case with no principled winner: no COL01
    // and two or more DISTINCT collection-less candidates - e.g. one PID under
    // both Apple VID spellings, a stale pairing record beside the live one.
    // Which of those belongs to this mouse is not decidable from key names, and
    // a wrong pick would report a HEALTHY pointer on a broken mouse or aim the
    // elevated pnputil /restart-device of PointerChildMissing at the wrong
    // node. So that field resolves to NOTHING: no keys, which PointerChildLive
    // can only report as null.
    internal static PointerKeySet SelectPointerKeys(IEnumerable<string> deviceKeyNames, string pid)
    {
        List<string> col01 = [];
        List<string> sole = [];
        HashSet<string> seenSole = new(StringComparer.OrdinalIgnoreCase);

        foreach (var name in deviceKeyNames)
        {
            switch (ClassifyPointerKey(name, pid))
            {
                case PointerKeyKind.Col01:
                    col01.Add(name);
                    break;
                case PointerKeyKind.SoleCollection:
                    if (seenSole.Add(name))
                        sole.Add(name);
                    break;
            }
        }

        if (col01.Count > 0)
            return new PointerKeySet([.. col01], col01.Count, sole.Count, "col01");
        if (sole.Count > 1)
            return new PointerKeySet([], 0, sole.Count, "ambiguous");
        if (sole.Count == 1)
            return new PointerKeySet([.. sole], 0, 1, "sole");
        return PointerKeySet.Empty;
    }

    static string Describe(bool? value) =>
        value is null ? "unknown" : value.Value ? "true" : "false";

    // Duplicate of the CM_Get_DevNode_PropertyW declaration in DeviceStackReader, which is
    // private to that class. Keeping it private here avoids editing that file;
    // if the two are ever merged, the shared home is a P/Invoke holder, not
    // either reader.
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
}
