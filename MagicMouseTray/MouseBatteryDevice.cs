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
//       This project has never read a 0269 itself - one tester reported that Magic Tray
//       finds no battery on the Boot Camp route (issue #124). The descriptor is also why
//       discovery must keep EVERY collection of a mouse as a read candidate: collapse a
//       0269 onto its pointer collection and neither channel has anywhere to answer from.
//     0310 (Apple Wireless Mouse): never read here, no descriptor ever seen, unknown.
//   BLE v2 is VID 0001004C PID&0269 (#73).
//   Of this family only the 030D has ever been measured here (97%, unified Feature 0x47),
//   so DriverAdvisor keys its battery CLAIM on the PID and not on Kind (#137), and the
//   trackpad prose describes this probe rather than the v3's fixed 0x90 (#134).
//
// PROBE, LEARN, RELEARN (issue #124 follow-up). The descriptor is no longer allowed to VETO a
// channel, only to order the attempts: ReadV1V2Feature tries the declared channel first
// because it is the cheapest likely hit, then tries the other one anyway. An Apple device
// answering a report it never declared is ordinary behaviour, and this project cannot predict
// per-model channels - the three shapes it knows disagree (030D unified Feature on one
// interface and 0323 Input 0x90 on col02, both measured here; 0269 Input 0x90 on col02, from
// its public descriptor, never read by this project). Apple's driver choice changes the shape
// again (measured on the 0323: filter bound = one unified node with no readable battery,
// filter removed = &col02 appears and reads). So the app stops guessing: it probes, logs
// EVERY attempt with its outcome, remembers the pair that worked in config.ini
// (BatteryChannelMemory, one battery call per poll from then on), and
// throws that memory away the moment it stops working - which is what makes a Boot Camp to
// Stock driver switch self-healing without the user doing anything.
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
    // the battery report, only opening the node and reading its descriptor can (issue #124,
    // where a tester's Magic Mouse v2 reported no battery at all on the Boot Camp route).
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
                    return pct;
                }
                // Do not keep failing silently on a stale answer: forget it and fall through
                // to the full probe walk on THIS read, so one poll both notices the change
                // and finds the new channel. The next success writes the new answer.
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

    int ReadCandidate(string path, BatteryChannelAnswer? learned = null)
    {
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
    // reason to report the device as having no battery - which is the bug in issue #124.
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
        // claim the app did not observe (issue #124).
        Logger.Log($"MOUSE_NO_BATTERY_CAP device={DeviceName} pid={Pid} col={col} tried={BatteryChannelAnswer.Input90},{BatteryChannelAnswer.Feature47} usage=0x{node.UsagePage:X4}/0x{node.Usage:X4} featcaps={node.FeatureValueCaps} featlen={node.FeatureLen} inlen={node.InputLen} path={path}");
        return best;
    }

    // The remembered channel, and only it. The descriptor is still read here, but it costs no
    // battery call: it sizes the Feature buffer and keeps the declared= field of the log line a
    // fact instead of a guess. Exactly one HidD_GetInputReport / HidD_GetFeature leaves this
    // method, which is the whole point of remembering.
    int ReadLearnedChannel(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path, BatteryChannelAnswer answer)
    {
        if (!TryInspect(handle, path, out var node)) return -1;

        var attempt = answer.Channel == BatteryChannelAnswer.Input90
            ? new ChannelAttempt(BatteryChannelAnswer.Input90, answer.ReportId, node.VendorBatteryInput)
            : new ChannelAttempt(BatteryChannelAnswer.Feature47, answer.ReportId, node.FeatureBatteryRid == answer.ReportId);
        return RunAttempt(handle, CollectionToken(path), attempt, node.FeatureLen);
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
    int RunAttempt(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string col, ChannelAttempt attempt, int featureLen)
    {
        Logger.Log($"MOUSE_CHANNEL_TRY pid={Pid} col={col} channel={attempt.Channel} rid=0x{attempt.ReportId:X2} declared={(attempt.Declared ? "true" : "false")}");
        return attempt.Channel == BatteryChannelAnswer.Input90
            ? TryInput90(handle, col, attempt)
            : TryFeature(handle, col, attempt, featureLen);
    }

    int TryInput90(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string col, ChannelAttempt attempt)
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
                    LogChannelOk(col, attempt, pct.Value, buf, 3);
                    return pct.Value;
                }
                LogChannelFail(col, attempt, 0, IsBogusZeroReport(buf) ? "zero_byte" : "out_of_range", buf, 3);
                // The device answered and what it sent was not a usable level.
                return -2;
            }
            if (n < 2) Thread.Sleep(50);
        }
        LogChannelFail(col, attempt, Marshal.GetLastWin32Error(), "call_failed", null, 0);
        return DeclaredFailureSentinel(attempt);
    }

    int TryFeature(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string col, ChannelAttempt attempt, int featureLen)
    {
        // Sized from the descriptor when the node declares feature reports at all; 64 is the
        // blind-try buffer for a node that declares none, matching the input path.
        var buf = new byte[featureLen > 0 ? Math.Max(featureLen, 2) : 64];
        buf[0] = attempt.ReportId;
        if (!HidNative.HidD_GetFeature(handle, buf, buf.Length))
        {
            LogChannelFail(col, attempt, Marshal.GetLastWin32Error(), "call_failed", null, 0);
            return DeclaredFailureSentinel(attempt);
        }

        int pct = buf[1];
        if (pct is >= MinValidPercent and <= 100)
        {
            LogChannelOk(col, attempt, pct, buf, 2);
            return pct;
        }
        LogChannelFail(col, attempt, 0, pct == 0 ? "zero_byte" : "out_of_range", buf, 2);
        return -2;
    }

    // A call that FAILS on a channel this node DECLARES is the old MOUSE_UNIFIED_BLOCKED case:
    // the device says it has a battery report and Windows will not hand it over (a bound filter
    // driver eats the IOCTL), which is -2, present but unreadable. A blind try that fails
    // observed nothing at all and may not claim anything, so it stays -1.
    static int DeclaredFailureSentinel(ChannelAttempt attempt) => attempt.Declared ? -2 : -1;

    void LogChannelOk(string col, ChannelAttempt attempt, int pct, byte[] buf, int bytes)
    {
        Logger.Log($"MOUSE_CHANNEL_OK pid={Pid} col={col} channel={attempt.Channel} rid=0x{attempt.ReportId:X2} pct={pct} bytes=[{FormatReportHead(buf, bytes)}]");
        // The success line every existing consumer greps for, in the exact wording
        // DRIVER-MATRIX.md and DriverAdvisor quote: "(split)" for the vendor Input channel,
        // "(unified Feature 0xNN)" for the Generic Device Feature channel.
        string how = attempt.Channel == BatteryChannelAnswer.Input90
            ? "(split)"
            : $"(unified Feature 0x{attempt.ReportId:X2})";
        Logger.Log($"MOUSE_BATTERY_OK device={DeviceName} pct={pct}% {how}");
    }

    void LogChannelFail(string col, ChannelAttempt attempt, int err, string reason, byte[]? buf, int bytes)
    {
        // Bytes only when there ARE bytes: a failed call read nothing, and printing an empty
        // buffer as if it were the device's answer would be a claim it never made.
        string tail = buf is null ? string.Empty : $" bytes=[{FormatReportHead(buf, bytes)}]";
        Logger.Log($"MOUSE_CHANNEL_FAIL pid={Pid} col={col} channel={attempt.Channel} rid=0x{attempt.ReportId:X2} err={err} reason={reason}{tail}");
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

    // The remembered answer stopped working. Log what it was and why it is being dropped, then
    // drop it: the caller re-probes on the same read and the next success stores the new
    // answer, so a driver switch shows up in the log as relearn then learn.
    internal static void Forget(string pid, BatteryChannelAnswer was, string reason)
    {
        Logger.Log($"MOUSE_RELEARN pid={pid} was={was.Collection}:{was.Channel} reason={reason}");
        Volatile.Read(ref _sink)?.ClearBatteryChannel(pid);
    }
}
