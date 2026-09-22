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
//   point, ReadV1V2Feature, but the channel it takes is whatever the opened node's
//   descriptor declares, and that is NOT the same channel on every model:
//     030D: the unified Generic Device (0x0006) battery-strength (0x0020) FEATURE
//       report, RID 0x47. Measured by this project on its own hardware (97%; again
//       2026-09-21, 91%, bytes=[47 5B]). That mouse enumerates as ONE interface with
//       no &col0x suffix and carries the Feature on the mouse collection itself, which
//       is why nothing about collections can take its percent away - a 030D fact only.
//     0269: no Generic Device battery-strength Feature exists on this model at all, so
//       the unifiedApple branch below can never fire for it. Its only channel is the
//       second top-level collection - usage page 0xFF00, usage 0x0014, Input RID 0x90,
//       percent byte at buf[2] - which is the same shape the 0323 uses and which Windows
//       exposes as the &Col02 interface. Source: public HID descriptor dump of a Magic
//       Mouse 2 at
//       https://github.com/svartalf/hid-magicmouse2/blob/master/dumps/debugfs-rdesc.log
//       Numeric facts re-parsed from that byte string only: the repo is kernel-derived,
//       so no prose, code or comments are copied from it (same rule as hid-ids.h).
//       This project has never read a 0269 itself - one tester reported that Magic Tray
//       finds no battery on the Boot Camp route (issue #124). The descriptor is also why
//       discovery must keep EVERY collection of a mouse as a read candidate: collapse a
//       0269 onto its pointer collection and ReadV1V2Feature has no channel left to read.
//     0310 (Apple Wireless Mouse): never read here, no descriptor ever seen, unknown.
//   BLE v2 is VID 0001004C PID&0269 (#73).
//   Of this family only the 030D has ever been measured here (97%, unified Feature 0x47),
//   so DriverAdvisor keys its battery CLAIM on the PID and not on Kind (#137), and the
//   trackpad prose describes this descriptor-driven path rather than the v3's fixed
//   0x90 (#134).
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

    int ReadCandidate(string path)
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

        if (Kind == DeviceKind.MagicMouseV3 || Pid.Equals("0323", StringComparison.OrdinalIgnoreCase))
            return ReadV3Rid90(handle, path);

        return ReadV1V2Feature(handle, path);
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

    int ReadV1V2Feature(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
    {
        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed))
        {
            Logger.Log($"MOUSE_NO_PREPARSED device={DeviceName} err={Marshal.GetLastWin32Error()} path={path}");
            return -1;
        }

        bool splitVendor = false;
        bool unifiedApple = false;
        byte unifiedRid = 0;
        int featureLen = 0;
        // Copied out of caps and kept past the finally that frees the preparsed data: when
        // neither battery channel is declared, these four numbers ARE the diagnosis of the
        // node that was opened, and nothing else in the log identifies it.
        ushort usagePage = 0, usage = 0, featureValueCaps = 0, inputLen = 0;

        try
        {
            var caps = new HidNative.HIDP_CAPS();
            if (HidNative.HidP_GetCaps(preparsed, ref caps) != HidNative.HIDP_STATUS_SUCCESS)
            {
                Logger.Log($"MOUSE_NO_CAPS device={DeviceName} err={Marshal.GetLastWin32Error()} path={path}");
                return -1;
            }

            usagePage = caps.UsagePage;
            usage = caps.Usage;
            featureValueCaps = caps.NumberFeatureValueCaps;
            inputLen = caps.InputReportByteLength;
            featureLen = caps.FeatureReportByteLength;

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
                            unifiedApple = true;
                            unifiedRid = fcaps[i].ReportID;
                            break;
                        }
                    }
                }
            }

            // The vendor collection: usage page 0xFF00, usage 0x0014, Input RID 0x90.
            // For a 0269 this is the ONLY battery channel there is (see the file header);
            // reaching it therefore depends entirely on the node opened BEING that
            // collection, which is what the candidate walk in GetBatteryPercent exists to
            // guarantee - the pointer collection of the same mouse declares neither channel
            // and can only produce MOUSE_NO_BATTERY_CAP.
            // The >= 3 guard fits that collection exactly rather than excluding it:
            // HIDP_CAPS.InputReportByteLength counts the report-ID byte, so report ID +
            // flags byte + percent byte = 3, and buf[2] is the percent.
            if (caps.UsagePage == UP_VENDOR_BATTERY && caps.Usage == USG_VENDOR_BATTERY
                && caps.InputReportByteLength >= 3)
                splitVendor = true;
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }

        if (splitVendor)
        {
            var buf = new byte[Math.Max(3, 64)];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Array.Clear(buf, 0, buf.Length);
                buf[0] = BatteryReportId;
                if (HidNative.HidD_GetInputReport(handle, buf, buf.Length))
                {
                    var pct = ParseRid90Percent(buf);
                    if (pct is >= 0)
                    {
                        Logger.Log($"MOUSE_BATTERY_OK device={DeviceName} pct={pct}% (split)");
                        return pct.Value;
                    }
                    if (IsBogusZeroReport(buf))
                        Logger.Log($"MOUSE_BATTERY_ZERO device={DeviceName} rid=0x{buf[0]:X2} bytes=[{FormatReportHead(buf, 3)}] (split, not a real level)");
                    else
                        Logger.Log($"MOUSE_SPLIT_BAD device={DeviceName} rid=0x{buf[0]:X2} bytes=[{FormatReportHead(buf, 3)}] (split, percent byte outside 1..100)");
                    return -1;
                }
                if (attempt < 2) Thread.Sleep(50);
            }
            Logger.Log($"MOUSE_READ_FAILED device={DeviceName} err={Marshal.GetLastWin32Error()}");
            return -1;
        }

        if (unifiedApple && featureLen > 0)
        {
            var fbuf = new byte[Math.Max(featureLen, 2)];
            fbuf[0] = unifiedRid;
            if (HidNative.HidD_GetFeature(handle, fbuf, fbuf.Length))
            {
                int pct = fbuf[1];
                if (pct is >= MinValidPercent and <= 100)
                {
                    Logger.Log($"MOUSE_BATTERY_OK device={DeviceName} pct={pct}% (unified Feature 0x{unifiedRid:X2})");
                    return pct;
                }
                if (pct == 0)
                    Logger.Log($"MOUSE_BATTERY_ZERO device={DeviceName} rid=0x{unifiedRid:X2} bytes=[{FormatReportHead(fbuf, 2)}] (unified Feature, not a real level)");
                else
                    Logger.Log($"MOUSE_UNIFIED_BAD device={DeviceName} rid=0x{unifiedRid:X2} bytes=[{FormatReportHead(fbuf, 2)}] (unified Feature, percent byte outside 1..100)");
                return -1;
            }
            int err = Marshal.GetLastWin32Error();
            Logger.Log($"MOUSE_UNIFIED_BLOCKED device={DeviceName} err={err} (battery report not exposed)");
            return -2;
        }

        // The one fully silent failure this file used to have. The node that was opened
        // declares neither a Generic Device battery-strength FEATURE cap nor the
        // 0xFF00/0x0014 vendor top level, so there is no channel here to read. Name the node
        // completely: a remote reporter's log has to separate "the wrong collection was
        // opened" from "this device really has no battery report" (issue #124).
        Logger.Log($"MOUSE_NO_BATTERY_CAP device={DeviceName} usage=0x{usagePage:X4}/0x{usage:X4} featcaps={featureValueCaps} featlen={featureLen} inlen={inputLen} path={path}");
        return -1;
    }
}
