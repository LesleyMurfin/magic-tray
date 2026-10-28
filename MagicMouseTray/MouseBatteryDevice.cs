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

    // The IBatteryDevice level contract in one place. Every implementation's read path
    // asks this before returning a percentage, so the floor cannot drift per device:
    // a real 0 would end AdaptivePoller.BestReading's scan of a group and outrank a live
    // interface's failure sentinel.
    internal static bool IsRealLevel(int pct) => pct is >= MinValidPercent and <= 100;

    readonly string _path;

    public string DeviceName { get; }
    public string Pid { get; }
    public DeviceKind Kind { get; }

    // The HID interface this instance reads. Part of the IBatteryDevice contract: discovery
    // returns one device per interface path, and AdaptivePoller keys per-interface state by it.
    public string DevicePath => _path;

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
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Array.Clear(buf, 0, buf.Length);
            buf[0] = BatteryReportId;
            if (HidNative.HidD_GetInputReport(handle, buf, buf.Length))
            {
                var (pct, marker) = ClassifyRid90Report(buf);
                // Same dialect as KB_BATTERY_OK / LOGI_BATTERY_OK: docs/TEST-PLAN.md rows are read
                // off debug.log by eye and `pct=` is the token for a healthy read. Each rejection
                // keeps its own sentence because the three mean different things to whoever reads
                // the log - and because the retired MOUSE_RID90_BAD line printed `rid=0x90` as if
                // the report id were at fault, when a 0x90 carrying a 101..255 percent byte has a
                // perfectly correct id and only unusable content.
                string detail = marker switch
                {
                    "MOUSE_BATTERY_OK" => $"pct={pct}% (Input 0x90 COL02)",
                    "MOUSE_BATTERY_ZERO" => $"rid=0x{buf[0]:X2} bytes=[{FormatReportHead(buf, 3)}] (no battery report sent yet - idle mouse or a charge-cable phantom; not a real level)",
                    "MOUSE_RID90_BAD_CONTENT" => $"rid=0x{buf[0]:X2} value={buf[2]} bytes=[{FormatReportHead(buf, 3)}] (report id is correct, the percent byte is outside 1-100)",
                    _ => $"rid=0x{buf[0]:X2} bytes=[{FormatReportHead(buf, 3)}] (not the 0x90 battery report)",
                };
                Logger.Log($"{marker} device={DeviceName} {detail}");
                return pct;
            }
            if (attempt < 2) Thread.Sleep(50);
        }

        var (failPct, failMarker) = ClassifyReadFailure(v3Rid90Path: true);
        Logger.Log($"{failMarker} device={DeviceName} err={Marshal.GetLastWin32Error()} (not Feature 0x47)");
        return failPct;
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
        if (!IsRealLevel(pct)) return null;
        return pct;
    }

    // A well-formed 0x90 report whose percent byte is 0 - rejected, logged distinctly.
    internal static bool IsBogusZeroReport(byte[] buf) =>
        buf is not null && buf.Length >= 3 && buf[0] == BatteryReportId && buf[2] == 0;

    // The sentinel decision for a v3 Input 0x90 report that DID arrive, split out of the read so
    // it is provable without the hardware (the shape is KeyboardBatteryDevice.ClassifyFeatureByte
    // and LogitechBatteryDevice.ClassifyLevelByte, marker included). Pure and allocation-free: the
    // markers are literals and the tuple never leaves the stack. It layers on ParseRid90Percent
    // and IsBogusZeroReport instead of re-deriving the level rule; the single byte test it repeats
    // is the report id, because both of those predicates answer "no" for a wrong id AND for a 0x90
    // carrying junk, while only this seam has to tell those two apart in the log.
    //
    // Only the [90 xx 00] arm is -2. IBatteryDevice's rule is that -2 is reserved for what a read
    // path can PROVE never arrived, and a v3 zero is exactly that: an idle mouse or a charge-cable
    // phantom leaves the report shell with no level in it. A 0x90 whose percent byte is 101..255
    // has the report in hand and only its content is unusable, so it is -1 - the same answer the
    // three analogous arms give (KeyboardBatteryDevice.ClassifyFeatureByte,
    // LogitechBatteryDevice.ClassifyLevelByte, and the unified-Feature arm in ReadV1V2Feature
    // below). That arm used to answer -2 under the now-retired MOUSE_RID90_BAD marker, which cost
    // more than tidiness: -2 outranks -1 in AdaptivePoller.ScanEndsHere (the one ranking shared by
    // the bounded and unbounded group scans), so a single junk-answering interface won the whole
    // group over a live interface's -1, and DeviceCapability.BatteryRow then rendered
    // "Battery unavailable (Windows sends no report)"
    // (DeviceCapability.cs:253) for a device that was answering.
    //
    // A report with some OTHER id is -1 as well, including when its byte 2 happens to be 0: a
    // non-0x90 report arriving proves its content is not a level, it does not prove the battery
    // report never arrived. The [90 xx 00] zero inference is specific to the v3 report and must
    // not generalise to any report that merely contains a zero. A buffer shorter than 3 bytes (or
    // null) carries no percent byte at all, so it is the same "not a level" answer.
    internal static (int Pct, string Marker) ClassifyRid90Report(byte[] buf)
    {
        var pct = ParseRid90Percent(buf);
        if (pct is not null) return (pct.Value, "MOUSE_BATTERY_OK");
        if (IsBogusZeroReport(buf)) return (-2, "MOUSE_BATTERY_ZERO");
        return buf is { Length: >= 3 } && buf[0] == BatteryReportId
            ? (-1, "MOUSE_RID90_BAD_CONTENT")
            : (-1, "MOUSE_RID90_WRONG_ID");
    }

    // The sentinel for a read that never completed - three failed HidD_GetInputReport attempts -
    // on either mouse path. Both are -2: IBatteryDevice files "the HID read itself failed" under
    // -2, and the two paths are looking at the same hardware symptom, so the split path's old -1
    // made one wedged interface rank differently in AdaptivePoller.BestReading depending only on
    // which path happened to read it. The markers stay distinct because the log still has to name
    // the path that gave up: the v3 Input 0x90 read (which must never fall back to Feature 0x47)
    // or the split vendor-collection read.
    internal static (int Pct, string Marker) ClassifyReadFailure(bool v3Rid90Path) =>
        (-2, v3Rid90Path ? "MOUSE_RID90_FAILED" : "MOUSE_READ_FAILED");

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
            // -2, not -1: a read that never completed is the same answer on both mouse paths
            // (ClassifyReadFailure). Nothing mouse-side gates on this sentinel - RepairPlanner
            // rule 2d is gated on DriverHealthChecker.IsKeyboardPid and TrayMenu.ShowFixKeyboard
            // on DeviceKind.MagicKeyboard, and DeviceCapability's SDP row is inside its
            // MagicKeyboard branch - so the only visible change is the mouse row wording, which
            // now says "Windows sends no report" for an interface that is in fact not reporting.
            var (failPct, failMarker) = ClassifyReadFailure(v3Rid90Path: false);
            Logger.Log($"{failMarker} device={DeviceName} err={Marshal.GetLastWin32Error()}");
            return failPct;
        }

        if (unifiedApple && featureLen > 0)
        {
            var fbuf = new byte[Math.Max(featureLen, 2)];
            fbuf[0] = unifiedRid;
            if (HidNative.HidD_GetFeature(handle, fbuf, fbuf.Length))
            {
                int pct = fbuf[1];
                if (IsRealLevel(pct))
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
