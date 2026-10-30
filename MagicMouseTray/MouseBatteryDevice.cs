// IBatteryDevice implementation for Apple Magic Mouse (all generations).
//
// Battery read strategy (read-only HID - the tray never binds or flips filters).
// Never WMI / Hands-Free / iPhone as a mouse percent, on any model.
//
//   0323 / v3: HID Input RID 0x90 on COL02, buf[2]=pct. Never Feature 0x47
//   (live 2026-09-01: Input 0x90 COL02 43%/46%, bytes=[90 04 2E]; Feature 0x90 FAIL.
//   live 2026-09-21: two interfaces, &col01 0x0001/0x0002 with no battery cap and
//   &col02 0xFF00/0x0014 Input 0x90 = 73%, bytes=[90 04 49]).
//
//   v1/v2 (030D / 0269 / 0310) and every trackpad (030E / 0265 / 0324) share ONE entry
//   point, ReadV1V2Feature, which tries BOTH channels on the node it opened (see PROBE,
//   LEARN, RELEARN below) because the channel that answers is NOT the same on every model
//   and the descriptor only suggests which to try first:
//     030D: the unified Generic Device (0x0006) battery-strength (0x0020) FEATURE
//       report, RID 0x47. Measured by this project on its own hardware (97%; again
//       2026-09-21, 91%, bytes=[47 5B]). That mouse enumerates as ONE interface with
//       no &col0x suffix and carries the Feature on the mouse collection itself, which
//       is why nothing about collections can take its percent away - a 030D fact only.
//     0269: its public descriptor declares no Generic Device battery-strength Feature at
//       all, so feature47 on it is a blind try that is expected to fail. The channel it
//       does declare is the second top-level collection - usage page 0xFF00, usage 0x0014,
//       Input RID 0x90, percent byte at buf[2] - the same shape the 0323 uses and which
//       Windows exposes as the &Col02 interface. Source: public HID descriptor dump of a
//       Magic Mouse 2 at
//       https://github.com/svartalf/hid-magicmouse2/blob/master/dumps/debugfs-rdesc.log
//       Numeric facts re-parsed from that byte string only: the repo is kernel-derived,
//       so no prose, code or comments are copied from it (same rule as hid-ids.h).
//       This project has never read a 0269 itself - two testers, independently, reported
//       that Magic Tray finds no battery on the Boot Camp route and does find one on
//       Stock Windows, the second of them 83% (issues #124 and #164, both on 1.1.0, which
//       predates this fix). The descriptor is also why discovery must keep EVERY
//       collection of a mouse as a read candidate: collapse a 0269 onto its pointer
//       collection and neither channel has anywhere to answer from.
//     0310 (Apple Wireless Mouse): never read here, no descriptor ever seen, unknown.
//   BLE v2 is VID 0001004C PID&0269 (#73).
//   Of this family only the 030D has ever been measured here (97%, unified Feature 0x47),
//   so DriverAdvisor keys its battery CLAIM on the PID and not on Kind (#137), and the
//   trackpad prose describes this probe rather than the v3's fixed 0x90 (#134).
//
// PROBE, LEARN, RELEARN (issues #124 / #164 follow-up). The descriptor is no longer allowed
// to VETO a channel, only to order the attempts: ReadV1V2Feature tries the declared channel
// first because it is the cheapest likely hit, then tries the other one anyway. An Apple
// device answering a report it never declared is ordinary behaviour, and this project
// cannot predict per-model channels - the three shapes it knows disagree (030D unified
// Feature on one interface and 0323 Input 0x90 on col02, both measured here; 0269 Input
// 0x90 on col02, from its public descriptor, never read by this project). Apple's driver
// choice changes the shape again (measured on the 0323: filter bound = one unified node
// with no readable battery, filter removed = &col02 appears and reads). So the app stops
// guessing: it probes, logs EVERY attempt with its outcome, remembers the pair that worked
// in config.ini (BatteryChannelMemory, one battery call per poll from then on), and throws
// that memory away after THREE consecutive failed reads of it - which is what makes a Boot
// Camp to Stock driver switch self-healing without the user doing anything. One failed read
// is an idle Bluetooth mouse far more often than it is a driver change, and forgetting on it
// would make an idle mouse thrash learn - forget - relearn every poll. The exception is the
// structural one: when the remembered COLLECTION is no longer among the mouse's interfaces
// the memory is dropped at once, because that is a reshaped device rather than a failed read
// (the battery_channel_<pid> paragraph of docs/ENABLE-DISABLE.md states both rules to the
// user).
//
// LOG VOLUME on the learned path (review nit N4). That read costs exactly ONE HID call and
// writes THREE lines - MOUSE_CHANNEL_TRY, MOUSE_CHANNEL_OK, MOUSE_BATTERY_OK - for an outcome
// identical to the last few thousand. Logger rotates at 1 MB keeping ONE generation
// (Logger.cs:22,51-59) and has no level or dedup of its own, so a steady mouse evicts exactly
// the history a bug reporter needs: the probe walk, the learn, the driver change. So the
// learned path BUFFERS its attempt lines and flushes them only when they differ word-for-word
// from the lines that PID last wrote (BatteryChannelMemory.RepeatsLastLearnedLog). Measured on
// the real Logger: 30 lines over 10 steady polls down to 3.
//
// Word-for-word rather than "same percent", because MOUSE_CHANNEL_OK carries bytes=[...]: a
// report head that moves while the percent holds still prints, and only a genuinely identical
// outcome goes quiet. A FAILING read prints every single time and clears the remembered set,
// so the success after it prints too. The full probe walk (ReadV1V2Feature - unlearned devices
// and the third-strike re-probe) passes NO sink and logs every attempt exactly as before: that
// walk is the diagnosis, it is not repetitive, and nothing about it may be dropped.
//
// A well-formed 0x90 report whose percent byte is 0 now answers -2 where the pre-fix code
// answered -1. That is user-visible: DeviceCapability renders -1 as "No reading" and -2 as
// "Battery unavailable (Windows sends no report)". The device DID answer, so -1 - "nothing
// came back" - is the one claim the tray may not make about it; -2 says what actually
// happened. The case this reaches is the just-charged cable phantom of DeviceRegistry.cs:27-30,
// which answers HidD_GetInputReport with a zero percent byte.
using System.Runtime.InteropServices;
using System.Threading;

namespace MagicMouseTray;

internal sealed class MouseBatteryDevice : IBatteryDevice
{
    internal record struct VidPidEntry(string VidPattern, string PidPattern, string DisplayName, DeviceKind Kind);

    internal static readonly VidPidEntry[] KnownMice =
    [
        // DESIGN: USB HID is VID_05AC&PID_xxxx for every numeric PID already in this table
        // (hid-ids.h: 030D Magic Mouse v1, 030E Magic Trackpad v1). Do not invent BLE 0001004C PIDs.
        new("0001004C", "PID&0323", "Magic Mouse 2024", DeviceKind.MagicMouseV3), // BT v3
        new("VID_05AC",  "PID_0323", "Magic Mouse 2024", DeviceKind.MagicMouseV3), // USB v3
        new("000205AC", "PID&030D", "Magic Mouse v1",   DeviceKind.MagicMouseV1), // BT v1
        new("VID_05AC",  "PID_030D", "Magic Mouse v1",   DeviceKind.MagicMouseV1), // USB v1
        new("000205AC", "PID&0269", "Magic Mouse v2",   DeviceKind.MagicMouseV2), // BT-classic v2
        new("0001004C", "PID&0269", "Magic Mouse v2",   DeviceKind.MagicMouseV2), // BLE v2 (#73)
        // 0310 is the Apple Wireless Mouse: a DIFFERENT, older device from the 030D Magic
        // Mouse v1, and nothing on it has ever been measured here. The Kind stays
        // MagicMouseV1 because Kind carries battery CHEMISTRY in this app -
        // BatteryAlertPolicy.cs:31-33 treats MagicMouseV1 as AA cells ("replace the
        // batteries"), which is right for an AWM, while MagicMouseV2 is told to plug in a
        // Lightning cable (BatteryAlertPolicy.cs:166), which would be a lie here. The
        // never-measured battery CLAIM is applied by PID in DriverAdvisor.OptionsFor via
        // HasMeasuredBattery instead (#137, driver repo #22). DisplayName is the user's
        // truth: this is not a Magic Mouse v1.
        new("000205AC", "PID&0310", "Apple Wireless Mouse", DeviceKind.MagicMouseV1), // BT AWM
        new("VID_05AC",  "PID_0310", "Apple Wireless Mouse", DeviceKind.MagicMouseV1), // USB AWM
        // PIDs below: numeric facts only from hid-ids.h (GPL) — no kernel code/comments copied.
        new("000205AC", "PID&0265", "Magic Trackpad 2",   DeviceKind.MagicTrackpadV2), // BT v2
        new("VID_05AC",  "PID_0265", "Magic Trackpad 2",   DeviceKind.MagicTrackpadV2), // USB v2
        new("000205AC", "PID&030E", "Magic Trackpad",     DeviceKind.MagicTrackpadV1), // BT v1
        new("VID_05AC",  "PID_030E", "Magic Trackpad",     DeviceKind.MagicTrackpadV1), // USB v1
        new("0001004C", "PID&0324", "Magic Trackpad 2024", DeviceKind.MagicTrackpadV3), // BLE v3
        new("VID_05AC",  "PID_0324", "Magic Trackpad 2024", DeviceKind.MagicTrackpadV3), // USB v3
        new("VID_05AC",  "PID_0269", "Magic Mouse v2",     DeviceKind.MagicMouseV2),    // USB v2
    ];

    internal static bool TryKnownMouse(string pid, out string displayName, out DeviceKind kind)
    {
        foreach (var m in KnownMice)
        {
            if (m.PidPattern.EndsWith(pid, StringComparison.OrdinalIgnoreCase))
            {
                displayName = m.DisplayName;
                kind = m.Kind;
                return true;
            }
        }
        displayName = "";
        kind = default;
        return false;
    }


    const ushort UP_VENDOR_BATTERY     = 0xFF00;
    const ushort USG_VENDOR_BATTERY    = 0x0014;
    const ushort UP_GENDEV_BATTERY     = 0x0006;
    const ushort USG_GENDEV_BATTSTRENG = 0x0020;
    internal const byte BatteryReportId = 0x90;
    // Apple firmware reports 1..100; 0 is only ever seen from a dead/phantom interface.
    internal const int MinValidPercent = 1;
    // The report id to try for feature47 when the opened node declares NONE. A declared id is
    // a fact read out of this device; 0x47 is a fallback GUESS - it is the id every Apple
    // device measured by this project has carried (030D live 2026-09-21: 91%, bytes=[47 5B]),
    // which makes it the best blind try and nothing more.
    internal const byte FallbackFeatureReportId = 0x47;

    // Ordered read candidates for ONE physical mouse, primary first. A mouse usually has a
    // single interface, but a v1/v2 can expose several collections (pointer plus vendor) and
    // discovery hands all of them over: nothing in the PATH says which collection carries
    // the battery report, only opening the node and reading its descriptor can (issues #124
    // and #164, two testers whose Magic Mouse v2 reported no battery at all on the Boot
    // Camp route).
    readonly IReadOnlyList<string> _candidates;

    // Index into _candidates of the interface that last produced a real percent. Sticky, so
    // a working mouse is not re-walked on every 30s poll. Only ever written as a whole int
    // by a poll read, so overlapping polls cannot tear it.
    int _preferred;

    public string DeviceName { get; }
    public string Pid { get; }
    public DeviceKind Kind { get; }

    // The HID interface this instance currently reads. Lets discovery tests assert which
    // transport survived de-duplication.
    internal string DevicePath => _candidates[_preferred];

    // Every interface this instance may read, in discovery order, primary first.
    internal IReadOnlyList<string> CandidatePaths => _candidates;

    internal MouseBatteryDevice(string path, string displayName, DeviceKind kind)
        : this(new[] { path }, displayName, kind) { }

    internal MouseBatteryDevice(IReadOnlyList<string> candidatePaths, string displayName, DeviceKind kind)
    {
        _candidates = candidatePaths;
        DeviceName = displayName;
        Kind = kind;
        Pid = DeviceRegistry.ExtractPid(candidatePaths[0]);
    }

    public int GetBatteryPercent()
    {
        // A remembered answer is read with exactly ONE battery call: the collection that
        // answered last time, over the channel that answered last time, and no probe walk.
        // This is the maintainer's "once it figures out which way is right, forget about
        // trying the other way".
        var learned = LearnedAnswer;
        if (learned is { } answer)
        {
            int idx = IndexOfCollection(answer.Collection);
            if (idx < 0)
            {
                // The remembered collection is not among this mouse's interfaces any more. A
                // driver change reshapes them (the 0323 loses &col02 when Apple's filter is
                // bound), so this is a relearn, not an error.
                BatteryChannelMemory.Forget(Pid, answer, "collection_absent");
            }
            else
            {
                int pct = ReadCandidate(_candidates[idx], answer);
                if (pct >= MinValidPercent)
                {
                    _preferred = idx;
                    BatteryChannelMemory.ClearFailedReads(Pid);
                    return pct;
                }
                // Three consecutive failures before the answer is dropped: one failed read is a
                // Bluetooth mouse that has gone idle, not a driver change, and forgetting on it
                // makes an idle mouse thrash learn - forget - relearn every poll. The count is a
                // near-neighbour of AdaptivePoller's own three-failure collapse to -3 and not the
                // same test - that one keys on DeviceName and only counts -1, so a -2 resets it
                // (AdaptivePoller.cs:156-165) - so do not reason about one from the other.
                //
                // The price, paid by exactly the case this is for: strikes one and two return the
                // sentinel WITHOUT running the probe walk, where the pre-change code re-probed on
                // the very first failure. So a genuine Boot Camp to Stock switch renders "No
                // reading" (-1) or "Battery unavailable (Windows sends no report)" (-2) for two
                // whole poll cycles before the third read probes and relearns - and this path is
                // never the v3's, so those cycles are DrainRateTracker.FloorNonV3 (30 minutes)
                // apart at the fastest and CeilingNormal (24h) apart on a healthy battery.
                // AdaptivePoller's -3 collapse ("Disconnected") does not pile on, because the
                // third cycle succeeds. Still judged cheaper than an idle mouse rewriting
                // config.ini every poll: a stale sentinel is recoverable, a thrashing relearn is
                // not.
                if (!BatteryChannelMemory.CountFailedRead(Pid))
                    return pct;
                // Third strike: forget and fall through to the full probe walk on THIS read, so
                // one poll both notices the change and finds the new channel. The next success
                // writes the new answer.
                BatteryChannelMemory.Forget(Pid, answer,
                    pct == -2 ? "answered_unusable" : "read_failed");
            }
        }

        // Read the interface that answered last time first, then the other candidates in
        // discovery order. The first one that yields a real percent becomes the preferred
        // interface for every later poll.
        int preferred = _preferred;
        int best = -1;
        for (int n = 0; n < _candidates.Count; n++)
        {
            // n == 0 is the preferred candidate; after that walk 0..Count-1 skipping it.
            int idx = n == 0 ? preferred : (n - 1 >= preferred ? n : n - 1);
            int pct = ReadCandidate(_candidates[idx]);
            if (pct >= 0)
            {
                if (idx != preferred)
                    Logger.Log($"MOUSE_CANDIDATE_SWITCH device={DeviceName} path={_candidates[idx]} (battery answers on this collection)");
                _preferred = idx;
                return pct;
            }
            // No new sentinel may be invented here: AdaptivePoller.ReadingRank, the alert
            // policy and DeviceCapability all key on -1 / -2 / -3. Of the two a read can
            // produce, -2 ("answered but sent no usable battery report") is the more
            // informative, so it is the one that survives the walk.
            if (SentinelRank(pct) > SentinelRank(best)) best = pct;
        }
        return best;
    }

    // Same order AdaptivePoller.ReadingRank uses when collapsing a device's collections.
    static int SentinelRank(int pct) => pct == -2 ? 1 : 0;

    // The v3 has a MEASURED fixed channel and takes no part in probing or learning: Input 0x90
    // on col02, Feature measured failing (err 87, 2026-09-01). Nothing may be learned for it
    // and no stored answer may be applied to it, or a hand-edited config.ini could change a
    // read path this project has hardware evidence for.
    bool HasFixedV3Channel =>
        Kind == DeviceKind.MagicMouseV3 || Pid.Equals("0323", StringComparison.OrdinalIgnoreCase);

    BatteryChannelAnswer? LearnedAnswer => HasFixedV3Channel ? null : BatteryChannelMemory.Get(Pid);

    // Which candidate carries the remembered collection. -1 when none does.
    int IndexOfCollection(string token)
    {
        for (int i = 0; i < _candidates.Count; i++)
            if (string.Equals(CollectionToken(_candidates[i]), token, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // The &col0N fragment of an interface path, lowercased, or "none" when the path carries no
    // &col fragment at all (the 030D shape). Half of a learned answer's key: unlike the full
    // path it survives re-pairing, which re-mints the instance ids, and it never contains the
    // Bluetooth MAC.
    internal static string CollectionToken(string? path)
    {
        if (string.IsNullOrEmpty(path)) return BatteryChannelAnswer.NoCollection;
        int at = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return BatteryChannelAnswer.NoCollection;
        int start = at + 4;
        int end = start;
        while (end < path.Length && char.IsAsciiDigit(path[end])) end++;
        if (end == start) return BatteryChannelAnswer.NoCollection;
        return string.Concat("col", path.AsSpan(start, end - start));
    }

    // Test hook - stands in for one candidate read, no live HID call. Null in production.
    // The success branch of the learned read (ClearFailedReads above) is otherwise unreachable
    // off Windows: a real percent needs ReadCandidate to open a HID node through kernel32, and
    // the only kernel-free way into GetBatteryPercent is the hands-free rejection, which always
    // answers -1, so nothing but a failing read could ever be driven through it.
    //
    // [ThreadStatic] because xunit runs test classes in parallel and this suite declares no
    // collection fixture: a sibling class on another thread must never be served the canned read
    // one class installed for itself, however careful that class's try/finally is. Production
    // never assigns this field, so a per-thread slot costs nothing there.
    //
    // The cost of that isolation: a hook does NOT survive a thread hop. It is installed on the
    // thread that assigns it and is null everywhere else, so a test that drives a read through
    // AdaptivePoller.ReadBatteryGuarded (AdaptivePoller.cs:58 runs it as
    // Task.Run(device.GetBatteryPercent)) finds no hook on the pool thread, silently takes the
    // real HID path, and fails for a reason unrelated to what it set out to test. No test does
    // this today. One that wants to either installs the hook on whatever runs on the far side of
    // the hop, or calls GetBatteryPercent directly and leaves the poller out of it.
    [ThreadStatic]
    internal static Func<string, BatteryChannelAnswer?, int>? ReadCandidateHook;

    int ReadCandidate(string path, BatteryChannelAnswer? learned = null)
    {
        if (ReadCandidateHook is { } hook) return hook(path, learned);

        if (DeviceRegistry.IsHandsFreeOrIphonePath(path))
        {
            Logger.Log($"MOUSE_REJECT_HANDSFREE device={DeviceName} path={path}");
            return -1;
        }

        using var handle = HidNative.CreateFile(
            path,
            0,  // zero access - avoids err=5 on mouhid-owned interfaces
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero,
            HidNative.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            Logger.Log($"MOUSE_OPEN_FAILED path={path} err={Marshal.GetLastWin32Error()}");
            return -1;
        }

        if (HasFixedV3Channel)
        {
            LogV3ChannelSkipOnce(path);
            return ReadV3Rid90(handle, path);
        }

        return learned is { } answer
            ? ReadLearnedChannel(handle, path, answer)
            : ReadV1V2Feature(handle, path);
    }

    // PIDs whose deliberate one-channel v3 behaviour has already been logged this run.
    static readonly HashSet<string> V3SkipLogged = new(StringComparer.OrdinalIgnoreCase);

    // Every other mouse in this file now tries both channels; the v3 deliberately does not,
    // because Feature was MEASURED failing on it. A silent exception to the file's own rule is
    // exactly what makes a remote debug.log unreadable, so say it - once per run, not once per
    // 30-second poll.
    void LogV3ChannelSkipOnce(string path)
    {
        lock (V3SkipLogged)
        {
            if (!V3SkipLogged.Add(Pid)) return;
        }
        Logger.Log($"MOUSE_CHANNEL_SKIP pid={Pid} col={CollectionToken(path)} channel={BatteryChannelAnswer.Feature47} reason=measured_err87 (v3 reads Input 0x90 on col02 only)");
    }

    // Live 0323: mm-hid-probe got 47% from Input 0x90 buf[2] on COL02.
    // Feature 0x47 is err 87/1 — do not use it, do not fall back to WMI.
    int ReadV3Rid90(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
    {
        // A silent -1 is undiagnosable in a remote reporter's debug.log - it cannot be told
        // apart from "this device exposes no battery". Every -1 below names its own cause.
        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed))
        {
            Logger.Log($"MOUSE_NO_PREPARSED device={DeviceName} err={Marshal.GetLastWin32Error()} path={path}");
            return -1;
        }
        int inLen = 64;
        try
        {
            var caps = new HidNative.HIDP_CAPS();
            if (HidNative.HidP_GetCaps(preparsed, ref caps) != HidNative.HIDP_STATUS_SUCCESS)
            {
                Logger.Log($"MOUSE_NO_CAPS device={DeviceName} err={Marshal.GetLastWin32Error()} path={path}");
                return -1;
            }
            inLen = Math.Max((int)caps.InputReportByteLength, 64);
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }

        var buf = new byte[inLen];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Array.Clear(buf, 0, buf.Length);
            buf[0] = BatteryReportId;
            if (HidNative.HidD_GetInputReport(handle, buf, buf.Length))
            {
                var pct = ParseRid90Percent(buf);
                if (pct is >= 0)
                {
                    Logger.Log($"MOUSE_BATTERY_OK device={DeviceName} pct={pct}% (Input 0x90 COL02)");
                    return pct.Value;
                }
                if (IsBogusZeroReport(buf))
                {
                    Logger.Log($"MOUSE_BATTERY_ZERO device={DeviceName} rid=0x{buf[0]:X2} bytes=[{FormatReportHead(buf, 3)}] (no battery report sent yet - idle mouse or a charge-cable phantom; not a real level)");
                    return -2;
                }
                Logger.Log($"MOUSE_RID90_BAD device={DeviceName} rid=0x{buf[0]:X2}");
                return -2;
            }
            if (attempt < 2) Thread.Sleep(50);
        }

        Logger.Log($"MOUSE_RID90_FAILED device={DeviceName} err={Marshal.GetLastWin32Error()} (not Feature 0x47)");
        return -2;
    }

    internal static int? ParseRid90Percent(byte[] buf)
    {
        if (buf is null || buf.Length < 3) return null;
        if (buf[0] != BatteryReportId) return null;
        int pct = buf[2];
        // Apple reports 1..100 on these devices. A read that SUCCEEDS and yields exactly 0
        // comes from a dead charge-cable/phantom interface, never from a connected mouse
        // (live: 30 of 63 reads pct=0 on a just-charged Magic Mouse 2024). A failed read
        // must stay a failed read so last-known-good and the poller handle it.
        if (pct is < MinValidPercent or > 100) return null;
        return pct;
    }

    // A well-formed 0x90 report whose percent byte is 0 - rejected, logged distinctly.
    internal static bool IsBogusZeroReport(byte[] buf) =>
        buf is not null && buf.Length >= 3 && buf[0] == BatteryReportId && buf[2] == 0;

    static string FormatReportHead(byte[] buf, int count)
    {
        int n = Math.Min(buf.Length, count);
        var sb = new System.Text.StringBuilder(n * 3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(buf[i].ToString("X2"));
        }
        return sb.ToString();
    }

    // What the descriptor of ONE opened node says. Copied out of the preparsed data before it
    // is freed: when every channel fails, these numbers ARE the diagnosis of the node that was
    // opened, and nothing else in the log identifies it.
    readonly record struct NodeCaps(
        byte? FeatureBatteryRid,
        bool VendorBatteryInput,
        int FeatureLen,
        ushort UsagePage,
        ushort Usage,
        ushort FeatureValueCaps,
        ushort InputLen);

    // One channel to try on an opened node. Declared records whether this node's own
    // descriptor announced the channel - worth logging on every line, because the point of the
    // probe is that an UNDECLARED channel is attempted anyway and sometimes answers.
    internal readonly record struct ChannelAttempt(string Channel, byte ReportId, bool Declared);

    // Attempt order for a v1/v2 node. BOTH channels are always attempted; the descriptor only
    // decides which goes first. A declared Generic Device battery-strength Feature cap leads
    // because it is the cheapest likely hit (030D), otherwise the vendor Input 0x90 leads
    // (0269, 0323 shape). A descriptor that declares neither is a reason to probe, never a
    // reason to report the device as having no battery - the bug in issues #124 and #164.
    internal static ChannelAttempt[] AttemptOrder(byte? declaredFeatureRid, bool declaredVendorInput)
    {
        var feature = new ChannelAttempt(
            BatteryChannelAnswer.Feature47,
            declaredFeatureRid ?? FallbackFeatureReportId,
            declaredFeatureRid.HasValue);
        var input = new ChannelAttempt(
            BatteryChannelAnswer.Input90, BatteryReportId, declaredVendorInput);
        return declaredFeatureRid.HasValue ? [feature, input] : [input, feature];
    }

    int ReadV1V2Feature(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
    {
        if (!TryInspect(handle, path, out var node)) return -1;

        string col = CollectionToken(path);
        int best = -1;
        foreach (var attempt in AttemptOrder(node.FeatureBatteryRid, node.VendorBatteryInput))
        {
            int pct = RunAttempt(handle, col, attempt, node.FeatureLen);
            if (pct >= MinValidPercent)
            {
                // Figured out: this PID answers on this collection over this channel. From the
                // next poll on it is read with one call and the other channel is not touched
                // again - until it fails, which is what Forget / MOUSE_RELEARN is for.
                BatteryChannelMemory.Learn(Pid, new BatteryChannelAnswer(col, attempt.Channel, attempt.ReportId));
                return pct;
            }
            if (SentinelRank(pct) > SentinelRank(best)) best = pct;
        }

        // Both channels were tried on this node and neither produced a percent. Name the node
        // completely AND name what was attempted: a remote reporter's log has to separate "the
        // wrong collection was opened" from "this device really has no battery report", and
        // since this build no longer trusts the descriptor, "no cap declared" alone would be a
        // claim the app did not observe (issues #124, #164).
        Logger.Log($"MOUSE_NO_BATTERY_CAP device={DeviceName} pid={Pid} col={col} tried={BatteryChannelAnswer.Input90},{BatteryChannelAnswer.Feature47} usage=0x{node.UsagePage:X4}/0x{node.Usage:X4} featcaps={node.FeatureValueCaps} featlen={node.FeatureLen} inlen={node.InputLen} path={path}");
        return best;
    }

    // The remembered channel, and only it. The descriptor is still read here, but it costs no
    // battery call: it sizes the Feature buffer and keeps the declared= field of the log line a
    // fact instead of a guess. Exactly one HidD_GetInputReport / HidD_GetFeature leaves this
    // method, which is the whole point of remembering.
    //
    // One call, three log lines, every poll forever - see LOG VOLUME in this file's header. The
    // attempt lines are buffered and written only when this PID's read says something it did not
    // say last time.
    int ReadLearnedChannel(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path, BatteryChannelAnswer answer)
    {
        if (!TryInspect(handle, path, out var node))
        {
            // TryInspect named its own cause straight to the log, unbuffered: a node that cannot
            // be inspected is not the steady state this dedup exists for. Drop the remembered
            // set with it, so the read that recovers prints instead of being taken for a repeat
            // of the last good one.
            BatteryChannelMemory.ForgetLearnedLog(Pid);
            return -1;
        }

        var attempt = answer.Channel == BatteryChannelAnswer.Input90
            ? new ChannelAttempt(BatteryChannelAnswer.Input90, answer.ReportId, node.VendorBatteryInput)
            : new ChannelAttempt(BatteryChannelAnswer.Feature47, answer.ReportId, node.FeatureBatteryRid == answer.ReportId);

        // Capacity 3: TRY plus either OK and BATTERY_OK, or one FAIL.
        var buffered = new List<string>(3);
        int pct = RunAttempt(handle, CollectionToken(path), attempt, node.FeatureLen, buffered);

        Flush(LearnedLogLines(Pid, buffered, pct >= MinValidPercent));
        return pct;
    }

    // Which of a learned read's buffered lines actually get written. Pure and internal so a
    // test can reach it: ReadCandidateHook intercepts one frame above ReadLearnedChannel, so
    // nothing routed through that hook can ever reach these branches in place.
    //
    // A failure prints EVERY time - it is the line a reporter and MOUSE_RELEARN are both read
    // off - and it takes the remembered set with it, so the success that follows is not
    // suppressed as a repeat of the last success three strikes ago.
    //
    // A success prints only when this PID's read says something it did not say last time.
    internal static List<string> LearnedLogLines(string pid, List<string> buffered, bool succeeded)
    {
        if (!succeeded)
        {
            BatteryChannelMemory.ForgetLearnedLog(pid);
            return buffered;
        }
        return BatteryChannelMemory.RepeatsLastLearnedLog(pid, buffered) ? new List<string>() : buffered;
    }

    static void Flush(List<string> lines)
    {
        foreach (var line in lines) Logger.Log(line);
    }

    // Reads the opened node's descriptor. False means the node could not be inspected at all,
    // and each such -1 names its own cause: a silent one is undiagnosable in a remote log.
    bool TryInspect(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path, out NodeCaps node)
    {
        node = default;
        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed))
        {
            Logger.Log($"MOUSE_NO_PREPARSED device={DeviceName} err={Marshal.GetLastWin32Error()} path={path}");
            return false;
        }

        try
        {
            var caps = new HidNative.HIDP_CAPS();
            if (HidNative.HidP_GetCaps(preparsed, ref caps) != HidNative.HIDP_STATUS_SUCCESS)
            {
                Logger.Log($"MOUSE_NO_CAPS device={DeviceName} err={Marshal.GetLastWin32Error()} path={path}");
                return false;
            }

            byte? featureRid = null;
            if (caps.NumberFeatureValueCaps > 0)
            {
                var fcaps = new HidNative.HIDP_VALUE_CAPS[caps.NumberFeatureValueCaps];
                ushort len = caps.NumberFeatureValueCaps;
                if (HidNative.HidP_GetValueCaps(2, fcaps, ref len, preparsed) == HidNative.HIDP_STATUS_SUCCESS)
                {
                    for (int i = 0; i < len; i++)
                    {
                        if (fcaps[i].UsagePage == UP_GENDEV_BATTERY && fcaps[i].Usage == USG_GENDEV_BATTSTRENG)
                        {
                            featureRid = fcaps[i].ReportID;
                            break;
                        }
                    }
                }
            }

            // The vendor collection: usage page 0xFF00, usage 0x0014, Input RID 0x90. The >= 3
            // guard fits that collection exactly rather than excluding it: InputReportByteLength
            // counts the report-ID byte, so report ID + flags + percent = 3, and buf[2] is the
            // percent. Declaring it only moves Input 0x90 to the front of the attempt order;
            // not declaring it no longer blocks the try.
            bool vendorInput = caps.UsagePage == UP_VENDOR_BATTERY && caps.Usage == USG_VENDOR_BATTERY
                && caps.InputReportByteLength >= 3;

            node = new NodeCaps(
                featureRid, vendorInput, caps.FeatureReportByteLength,
                caps.UsagePage, caps.Usage, caps.NumberFeatureValueCaps, caps.InputReportByteLength);
            return true;
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }
    }

    // One channel attempt on an opened node: one TRY line in, one OK or one FAIL line out, so a
    // reporter's debug.log is a complete record of what the device answered.
    //
    // sink is the learned path's dedup buffer: non-null means "collect these lines, the caller
    // decides whether they say anything new". Null - the probe walk - writes straight through,
    // which is why nothing about that walk's logging changed.
    int RunAttempt(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string col, ChannelAttempt attempt, int featureLen, List<string>? sink = null)
    {
        Emit(sink, $"MOUSE_CHANNEL_TRY pid={Pid} col={col} channel={attempt.Channel} rid=0x{attempt.ReportId:X2} declared={(attempt.Declared ? "true" : "false")}");
        return attempt.Channel == BatteryChannelAnswer.Input90
            ? TryInput90(handle, col, attempt, sink)
            : TryFeature(handle, col, attempt, featureLen, sink);
    }

    // The one place that decides between "write it" and "hold it": no caller below composes a
    // line without going through here, so no later line can forget the buffer exists.
    static void Emit(List<string>? sink, string line)
    {
        if (sink is null) Logger.Log(line);
        else sink.Add(line);
    }

    int TryInput90(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string col, ChannelAttempt attempt, List<string>? sink)
    {
        var buf = new byte[64];
        // Three IOCTLs 50ms apart are ONE attempt of this channel: a Bluetooth mouse that has
        // just woken refuses the first call. One outcome line is logged either way.
        for (int n = 0; n < 3; n++)
        {
            Array.Clear(buf, 0, buf.Length);
            buf[0] = attempt.ReportId;
            if (HidNative.HidD_GetInputReport(handle, buf, buf.Length))
            {
                var pct = ParseRid90Percent(buf);
                if (pct is not null)
                {
                    LogChannelOk(col, attempt, pct.Value, buf, 3, sink);
                    return pct.Value;
                }
                LogChannelFail(col, attempt, 0, IsBogusZeroReport(buf) ? "zero_byte" : "out_of_range", buf, 3, sink);
                // The device answered and what it sent was not a usable level.
                return -2;
            }
            if (n < 2) Thread.Sleep(50);
        }
        LogChannelFail(col, attempt, Marshal.GetLastWin32Error(), "call_failed", null, 0, sink);
        return DeclaredFailureSentinel(attempt);
    }

    int TryFeature(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string col, ChannelAttempt attempt, int featureLen, List<string>? sink)
    {
        // Sized from the descriptor when the node declares feature reports at all; 64 is the
        // blind-try buffer for a node that declares none, matching the input path.
        var buf = new byte[featureLen > 0 ? Math.Max(featureLen, 2) : 64];
        buf[0] = attempt.ReportId;
        if (!HidNative.HidD_GetFeature(handle, buf, buf.Length))
        {
            LogChannelFail(col, attempt, Marshal.GetLastWin32Error(), "call_failed", null, 0, sink);
            return DeclaredFailureSentinel(attempt);
        }

        int pct = buf[1];
        if (pct is >= MinValidPercent and <= 100)
        {
            LogChannelOk(col, attempt, pct, buf, 2, sink);
            return pct;
        }
        LogChannelFail(col, attempt, 0, pct == 0 ? "zero_byte" : "out_of_range", buf, 2, sink);
        return -2;
    }

    // A call that FAILS on a channel this node DECLARES is the old MOUSE_UNIFIED_BLOCKED case:
    // the device says it has a battery report and Windows will not hand it over (a bound filter
    // driver eats the IOCTL), which is -2, present but unreadable. A blind try that fails
    // observed nothing at all and may not claim anything, so it stays -1.
    static int DeclaredFailureSentinel(ChannelAttempt attempt) => attempt.Declared ? -2 : -1;

    void LogChannelOk(string col, ChannelAttempt attempt, int pct, byte[] buf, int bytes, List<string>? sink)
    {
        Emit(sink, $"MOUSE_CHANNEL_OK pid={Pid} col={col} channel={attempt.Channel} rid=0x{attempt.ReportId:X2} pct={pct} bytes=[{FormatReportHead(buf, bytes)}]");
        // The success line every existing consumer greps for, in the exact wording
        // DRIVER-MATRIX.md and DriverAdvisor quote: "(split)" for the vendor Input channel,
        // "(unified Feature 0xNN)" for the Generic Device Feature channel.
        string how = attempt.Channel == BatteryChannelAnswer.Input90
            ? "(split)"
            : $"(unified Feature 0x{attempt.ReportId:X2})";
        Emit(sink, $"MOUSE_BATTERY_OK device={DeviceName} pct={pct}% {how}");
    }

    void LogChannelFail(string col, ChannelAttempt attempt, int err, string reason, byte[]? buf, int bytes, List<string>? sink)
    {
        // Bytes only when there ARE bytes: a failed call read nothing, and printing an empty
        // buffer as if it were the device's answer would be a claim it never made.
        string tail = buf is null ? string.Empty : $" bytes=[{FormatReportHead(buf, bytes)}]";
        // Buffered like the rest, but ReadLearnedChannel flushes a failed read unconditionally:
        // this line never goes quiet.
        Emit(sink, $"MOUSE_CHANNEL_FAIL pid={Pid} col={col} channel={attempt.Channel} rid=0x{attempt.ReportId:X2} err={err} reason={reason}{tail}");
    }
}

// Where a learned answer lives at run time.
//
// It cannot live in a MouseBatteryDevice: DeviceRegistry.Discover rebuilds every device object
// on every poll cycle, so an instance field (the sticky _preferred index) only shortcuts the
// walk WITHIN one read and is gone by the next tick. The answer therefore has to outlive the
// instances, and the process already owns exactly one Config - App.xaml.cs hands it to TrayApp,
// which hands it to AdaptivePoller. Threading that handle down through
// DeviceRegistry.Discover, which builds devices out of paths and has no business knowing about
// user settings, would be the uglier change; instead AdaptivePoller - the owner of both the
// Config and the poll loop the reads happen on - attaches that same instance here as this
// store's persistence sink. Config stays the single writer of config.ini, so no second
// instance can clobber a threshold with a stale snapshot.
//
// Unattached (unit tests, any code path that reads a battery without a poller) every call is a
// no-op and the reader simply probes, which is the correct degraded behaviour.
internal static class BatteryChannelMemory
{
    static Config? _sink;

    internal static void Attach(Config? config) => Volatile.Write(ref _sink, config);

    internal static BatteryChannelAnswer? Get(string pid) => Volatile.Read(ref _sink)?.GetBatteryChannel(pid);

    // Config only reports true when the stored answer actually changed, so a device that keeps
    // answering the same way writes config.ini once and logs once - not every 30 seconds.
    internal static void Learn(string pid, BatteryChannelAnswer answer)
    {
        if (Volatile.Read(ref _sink) is not { } config) return;
        if (config.SetBatteryChannel(pid, answer))
            Logger.Log($"MOUSE_CHANNEL_LEARNED pid={pid} col={answer.Collection} channel={answer.Channel} rid=0x{answer.ReportId:X2}");
    }

    // Three consecutive failures before a learned answer is dropped. AdaptivePoller.cs:156-165
    // happens to use three as well, but it is NOT the same test - it keys on DeviceName, counts
    // only -1 (a -2 resets it) and lives on the poller - so the two must not be changed as a
    // pair or reasoned about as one.
    internal const int ForgetAfterFailures = 3;

    // The streak lives beside the learned answer and not on a MouseBatteryDevice for the same
    // reason the answer does: DeviceRegistry.Discover rebuilds every device object each poll,
    // so an instance counter would read 1 forever and nothing would ever be forgotten. Keyed
    // the way Config._batteryChannel is (Config.cs:117) - DeviceRegistry.ExtractPid takes the
    // PID off whichever path discovery ordered first, and the USB and Bluetooth path forms
    // disagree on case. The read-modify-write needs the lock Config uses for _batteryChannel; a
    // Volatile read would not make it atomic.
    //
    // "One strike per poll" is NOT guaranteed, and the paragraph above may be read as if it
    // were. AdaptivePoller.ReadBatteryGuarded (AdaptivePoller.cs:54-70) runs each read as
    // Task.Run(device.GetBatteryPercent) and, on timeout, logs POLL_DEVICE_TIMEOUT and returns
    // without cancelling anything - the abandoned task keeps running and still counts its strike
    // here, because this streak is process-wide and keyed on product id while the device objects
    // are rebuilt every cycle. So an abandoned read and the next cycle's read can spend two
    // strikes between them and three strikes can land in two polls. The lock keeps the count
    // itself correct; what is approximate is the mapping from strikes to polls. The error only
    // ever runs towards forgetting SOONER - that is, back towards the one-strike behaviour this
    // replaced - never towards holding a dead answer longer, so it cannot defeat self-healing.
    static readonly object _failGate = new();
    static readonly Dictionary<string, int> _failedReads = new(StringComparer.OrdinalIgnoreCase);

    // Records ONE failed read of this PID's remembered channel; true means the caller must now
    // forget it. The count resets as it reports the limit, so the answer that replaces this one
    // starts with its own three strikes instead of inheriting a full count.
    internal static bool CountFailedRead(string pid)
    {
        lock (_failGate)
        {
            _failedReads.TryGetValue(pid, out int fails);
            if (++fails < ForgetAfterFailures)
            {
                _failedReads[pid] = fails;
                return false;
            }
            // Remove rather than zero: an entry is only worth keeping while a streak is running.
            //
            // LIMITATION, known and accepted, and now a narrow one: an entry is cleared on the
            // third strike (here), on a successful learned read (ClearFailedReads), in Forget,
            // and by the poller (AdaptivePoller.cs:202) for every product id discovery returned
            // last cycle and not this one. That last clear is what covers the unpaired mouse and
            // the pulled cable, which used to carry a partial streak into their own reconnect.
            // What survives is "Show in Magic Tray" unticked: the poller records a skipped
            // device into seenThisCycle BEFORE it skips it (AdaptivePoller.cs:145-147), so the
            // cycle counts it as seen and it never reaches the omitted-names set that clears.
            // A PID that fails once or twice and is then unticked keeps its partial streak for
            // the life of the process, and on being reticked its learned answer can be dropped
            // on the first or second failure instead of the third - the old one-strike
            // behaviour. This store cannot close that itself: it is called from reads and cannot
            // tell "did not fail" from "was not asked". The mis-firing direction is forgetting
            // too early, never too late, so the worst case is one extra probe walk - the same
            // cost the pre-change code paid on every first failure.
            _failedReads.Remove(pid);
            return true;
        }
    }

    // Consecutive, not cumulative: one good read drops the streak. A mouse that misses a read
    // every few hours must never accumulate its way to a relearn while the channel still works.
    internal static void ClearFailedReads(string pid)
    {
        lock (_failGate) _failedReads.Remove(pid);
    }

    // What the learned-path read of this PID last WROTE to the log, in order. Keyed by PID for
    // the same reason the answer and the strike count are: DeviceRegistry.Discover rebuilds
    // every MouseBatteryDevice each poll, so an instance field would have nothing to compare
    // against by the next tick. Guarded the way MouseBatteryDevice.V3SkipLogged is, and for the
    // same reason: the poll thread is the writer.
    //
    // Being process-wide and keyed on product id, an entry OUTLIVES the device disappearing, the
    // same way a failure streak does. A mouse unplugged and replugged inside one session whose
    // learned read then produces a byte-identical line set gets no MOUSE_CHANNEL_* line for the
    // reconnect, so the log does not mark the gap. Accepted, not overlooked: the percent itself
    // still reaches debug.log on every cycle through TrayApp's TRAY_UPDATE line, so nothing is
    // lost, only the restatement of a channel that did not change. Forget and ForgetLearnedLog
    // clear the entry whenever the ANSWER changes, which is the case this comparand exists for.
    static readonly object _learnedLogGate = new();
    static readonly Dictionary<string, List<string>> _lastLearnedLog = new(StringComparer.OrdinalIgnoreCase);

    // True when these lines repeat, word for word and in order, what this PID's learned read
    // last wrote - the caller then writes nothing at all. False takes ownership of the list as
    // the new comparand, so the caller must not reuse it afterwards.
    //
    // Word-for-word and not "same percent", because MOUSE_CHANNEL_OK carries bytes=[...]: a
    // report head that moves while the percent holds still prints, and only a genuinely
    // identical outcome goes quiet. A changed declared= or col= prints for the same reason.
    internal static bool RepeatsLastLearnedLog(string pid, List<string> lines)
    {
        lock (_learnedLogGate)
        {
            if (_lastLearnedLog.TryGetValue(pid, out var last) && last.Count == lines.Count)
            {
                bool same = true;
                for (int i = 0; i < last.Count; i++)
                {
                    if (!string.Equals(last[i], lines[i], StringComparison.Ordinal))
                    {
                        same = false;
                        break;
                    }
                }
                if (same) return true;
            }
            _lastLearnedLog[pid] = lines;
            return false;
        }
    }

    // Drops the comparand, so this PID's next learned read prints whatever it finds. Called on
    // every failed learned read, on a node that could not be inspected, and from Forget: the
    // first read of a REPLACEMENT answer has to appear under its MOUSE_RELEARN even when the
    // relearned channel and percent happen to match the answer that was dropped.
    internal static void ForgetLearnedLog(string pid)
    {
        lock (_learnedLogGate) _lastLearnedLog.Remove(pid);
    }

    // The remembered answer stopped working. Drop it and log what it was and why: the caller
    // re-probes on the same read and the next success stores the new answer, so a driver switch
    // shows up in the log as relearn then learn.
    internal static void Forget(string pid, BatteryChannelAnswer was, string reason)
    {
        // The expected answer is passed so a read abandoned by ReadBatteryGuarded cannot, on
        // finally arriving, delete an answer that was learned after it started. That refusal is
        // why the clear runs FIRST and the line is written only when it took: a MOUSE_RELEARN
        // over an answer still sitting in config.ini, with no val=cleared and no probe walk under
        // it, reads as a driver switch that never happened. The refusal gets no token of its own
        // because every MOUSE_* token is a documented contract (README.md, docs/DRIVER-MATRIX.md,
        // docs/ENABLE-DISABLE.md) and a late read losing a race is not an event a reporter needs
        // named. Unattached - no sink, so nothing was ever stored - is the same non-event.
        if (Volatile.Read(ref _sink)?.ClearBatteryChannel(pid, was) == true)
            Logger.Log($"MOUSE_RELEARN pid={pid} was={was.Collection}:{was.Channel} reason={reason}");
        // The strikes were counted against the answer being dropped, so they go with it: a
        // structural forget must not hand inherited strikes to the answer that replaces it.
        ClearFailedReads(pid);
        // And the log memory goes with it too, or the first read of the relearned answer is
        // mistaken for a repeat of the one that just failed.
        ForgetLearnedLog(pid);
    }
}
