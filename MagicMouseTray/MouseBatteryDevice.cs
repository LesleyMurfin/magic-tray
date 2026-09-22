// IBatteryDevice implementation for Apple Magic Mouse (all generations).
//
// Battery read strategy (read-only HID — the tray never binds or flips filters):
//   0323 / v3: HID Input RID 0x90 on COL02, buf[2]=pct. Never Feature 0x47
//   (live 2026-09-01: Input 0x90 COL02 43%/46%, bytes=[90 04 2E]; Feature 0x90 FAIL).
//   Never WMI / Hands-Free / iPhone as mouse percent.
//   v1/v2 (030D / 0269 / 0310) and every trackpad (030E / 0265 / 0324): one
//   descriptor-driven path, ReadV1V2Feature - HID Input 0x90 when the node opened IS the
//   0xFF00/0x0014 vendor collection, otherwise the Generic Device battery-strength
//   FEATURE report the descriptor declares (0x47 on the 030D). Never WMI.
//   BLE v2 is VID 0001004C PID&0269 (#73).
//   Only the 030D has ever been measured here (97%, unified Feature 0x47), so
//   DriverAdvisor keys its battery CLAIM on the PID and not on Kind (#137), and the
//   trackpad prose describes this path rather than the v3's fixed 0x90 (#134).
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

    readonly string _path;

    public string DeviceName { get; }
    public string Pid { get; }
    public DeviceKind Kind { get; }

    // The HID interface this instance reads. Lets discovery tests assert which transport
    // survived de-duplication.
    internal string DevicePath => _path;

    internal MouseBatteryDevice(string path, string displayName, DeviceKind kind)
    {
        _path = path;
        DeviceName = displayName;
        Kind = kind;
        Pid = DeviceRegistry.ExtractPid(path);
    }

    public int GetBatteryPercent()
    {
        if (DeviceRegistry.IsHandsFreeOrIphonePath(_path))
        {
            Logger.Log($"MOUSE_REJECT_HANDSFREE device={DeviceName} path={_path}");
            return -1;
        }

        using var handle = HidNative.CreateFile(
            _path,
            0,  // zero access — avoids err=5 on mouhid-owned interfaces
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero,
            HidNative.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            Logger.Log($"MOUSE_OPEN_FAILED path={_path} err={Marshal.GetLastWin32Error()}");
            return -1;
        }

        if (Kind == DeviceKind.MagicMouseV3 || Pid.Equals("0323", StringComparison.OrdinalIgnoreCase))
            return ReadV3Rid90(handle);

        return ReadV1V2Feature(handle);
    }

    // Live 0323: mm-hid-probe got 47% from Input 0x90 buf[2] on COL02.
    // Feature 0x47 is err 87/1 — do not use it, do not fall back to WMI.
    int ReadV3Rid90(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed)) return -1;
        int inLen = 64;
        try
        {
            var caps = new HidNative.HIDP_CAPS();
            if (HidNative.HidP_GetCaps(preparsed, ref caps) != HidNative.HIDP_STATUS_SUCCESS)
                return -1;
            inLen = Math.Max((int)caps.InputReportByteLength, 64);
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }

        var buf = new byte[inLen];

        // The probe's touch evidence is a counter delta, and a delta is only
        // evidence about NOW if the interval it spans is short. This sample is
        // taken immediately BEFORE the request goes out, so the pair the
        // planner judges spans THIS read - the three attempts and their sleeps
        // included - instead of the 5 min .. 24 h gap back to the previous poll
        // cycle, which is what let a mouse last touched hours ago read as "in
        // use right now". The bound and what it costs in detection are recorded
        // at DeviceDiagReader.PairMaxSpan.
        //
        // One reader for both samples: it resolves the bound filter service
        // once, and the pair must come from the same service either way.
        var diag = new FilterDiagReader();
        var diagBefore = diag.Read();

        // The loop records ONE thing: whether the IOCTL ever answered. Every
        // terminal outcome below is then derived from that flag plus the
        // buffer, so a read is classified in exactly one place.
        //
        // It used to be four CaptureProbe calls, one per outcome, each with a
        // hand-written fact - and changing any single one of them was a
        // one-keystroke way to resurrect the restart loop with no test able to
        // see it, because this method needs a live 0323 and unmockable
        // hid.dll/kernel32.dll externs (HidNative.cs:24-25, :51-53) to run at
        // all. What survives that constraint is a single call whose argument is
        // a pure function of facts the loop measured: ZeroReportFact (:295-303)
        // is testable and pinned, and ioctlSucceeded cannot be edited to a
        // literal without also changing the control flow that reads it.
        bool ioctlSucceeded = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Array.Clear(buf, 0, buf.Length);
            buf[0] = BatteryReportId;
            if (HidNative.HidD_GetInputReport(handle, buf, buf.Length))
            {
                ioctlSucceeded = true;
                break;
            }
            if (attempt < 2) Thread.Sleep(50);
        }

        // The correlated probe for this read, captured before anything is
        // logged or returned. The -2 sentinel is left exactly as it was -
        // three of the outcomes below still return it and two other classes
        // emit it for unrelated reasons (KeyboardBatteryDevice's blocked SDP
        // cap, and :402 below), so the fact is carried BESIDE the sentinel and
        // never instead of it. Redefining -2 would silently change the keyboard
        // rule at RepairPlanner.cs:416-430 and the repair script behind it.
        CaptureProbe(ZeroReportFact(ioctlSucceeded, buf), diag, diagBefore);

        if (!ioctlSucceeded)
        {
            // The IOCTL failed outright after three attempts. Measured live as
            // err=21 (ERROR_NOT_READY) while the device was re-enumerating
            // after a restart, in the same minute that Rid12Count collapsed
            // 2095323 -> 521. Folding that into the zero-report fact would
            // diagnose the truncation defect - whose repair is a device restart
            // - from the evidence of a restart in progress, i.e. a loop.
            //
            // buf is NOT innocent here: the last attempt left the id byte
            // written and everything else zeroed (:177-178), which is
            // byte-for-byte the fingerprint IsBogusZeroReport looks for. The
            // only thing between a re-enumerating mouse and a restart loop is
            // ioctlSucceeded, which is why ZeroReportFact tests it FIRST.
            Logger.Log($"MOUSE_RID90_FAILED device={DeviceName} err={Marshal.GetLastWin32Error()} (not Feature 0x47)");
            return -2;
        }

        var pct = ParseRid90Percent(buf);
        if (pct is >= 0)
        {
            Logger.Log($"MOUSE_BATTERY_OK device={DeviceName} pct={pct}% (Input 0x90 COL02)");
            return pct.Value;
        }

        if (IsBogusZeroReport(buf))
        {
            // THE truncation fingerprint: a well-formed report whose percent
            // byte is 0. The log text below predates the finding that the
            // filter's inbound diversion swallows the percentage on a 1-byte
            // control-channel read, so "no battery report sent yet" is one of
            // two causes now - the other being this defect. Which one it is is
            // not decidable here; it is decided by the planner from the probe's
            // correlation with a live touch stream.
            Logger.Log($"MOUSE_BATTERY_ZERO device={DeviceName} rid=0x{buf[0]:X2} bytes=[{FormatReportHead(buf, 3)}] (no battery report sent yet - idle mouse or a charge-cable phantom; not a real level)");
            return -2;
        }

        // Wrong report id: nothing judgeable came back, so the fact is unknown
        // and NOT false - claiming "a real percent arrived" would be as wrong
        // as claiming the zero.
        Logger.Log($"MOUSE_RID90_BAD device={DeviceName} rid=0x{buf[0]:X2}");
        return -2;
    }

    // The correlated probe for THIS collection's read (Ruling L): a v3 exposes
    // three HID collections under one DeviceName and the poller collapses their
    // readings to one, so the probe has to travel with the reading that wins
    // rather than being published from whichever path happened to run last.
    // AdaptivePoller carries it alongside `best` and publishes only the
    // winner's (AdaptivePoller.cs:140-174).
    //
    // Instances are created fresh per poll by DeviceRegistry.Discover, so this
    // can never hold a reading from an earlier cycle.
    internal BatteryProbe? LastProbe { get; private set; }

    // The after-sample is taken IMMEDIATELY after HidD_GetInputReport returned,
    // in the same code path, because that is the only moment the filter's
    // shared last-inbound slot can still hold the control-channel frame this
    // read caused (BatteryProbe.DiagAfter). before is the pre-read sample from
    // the same reader, so the pair is bounded by the read itself.
    void CaptureProbe(bool? zeroReport, IFilterDiagReader diag, FilterDiagSnapshot? before) =>
        LastProbe = DeviceDiagReader.TakeBatteryProbe(
            zeroReport, diag, before, DateTimeOffset.UtcNow);

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

    // The fact each terminal outcome of a 0x90 read carries, as a pure
    // function of the two things that decide it: did the IOCTL answer, and what
    // is in the buffer. Every terminal outcome of ReadV3Rid90 is classified
    // through this one call (:194), so the mapping lives in a testable place
    // instead of in four literals at four return paths.
    //   true  - a well-formed report whose percent byte is 0: THE truncation
    //           fingerprint, and the only fact rule 2e may fire on.
    //   false - a real percent arrived.
    //   null  - nothing judgeable came back: a wrong report id, or an IOCTL
    //           that never answered at all.
    //
    // ioctlSucceeded is tested FIRST and short-circuits, because a failed read
    // leaves a buffer that LOOKS like the fingerprint - ReadV3Rid90 zeroes it
    // and writes the report id before every attempt - and because the failure
    // measured here was err=21 on a mouse mid-re-enumeration. Reading that as
    // the truncation defect prescribes a device restart from the evidence of a
    // restart already in progress: the loop FindingGate.cs:8-21 exists to stop.
    internal static bool? ZeroReportFact(bool ioctlSucceeded, byte[] buf)
    {
        if (!ioctlSucceeded)
            return null;                      // the IOCTL never answered
        if (ParseRid90Percent(buf) is >= 0)
            return false;                     // a real percent arrived
        return IsBogusZeroReport(buf) ? true  // well-formed, percent byte 0
                                      : null; // wrong report id: not judgeable
    }

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

    int ReadV1V2Feature(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed)) return -1;

        bool splitVendor = false;
        bool unifiedApple = false;
        byte unifiedRid = 0;
        int featureLen = 0;

        try
        {
            var caps = new HidNative.HIDP_CAPS();
            if (HidNative.HidP_GetCaps(preparsed, ref caps) != HidNative.HIDP_STATUS_SUCCESS) return -1;

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
                return -1;
            }
            int err = Marshal.GetLastWin32Error();
            Logger.Log($"MOUSE_UNIFIED_BLOCKED device={DeviceName} err={err} (battery report not exposed)");
            return -2;
        }

        return -1;
    }
}
