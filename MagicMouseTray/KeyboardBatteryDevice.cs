// IBatteryDevice implementation for Apple Wireless Keyboard (2011, PID=0x0239) and variants.
//
// Device: Apple Wireless Keyboard A1314, 2011 ANSI/ISO/JIS revision (VID=0x05AC).
// PID source: Linux kernel hid-apple.c (USB_DEVICE_ID_APPLE_ALU_WIRELESS_2011_*).
//
// Battery path (empirical, 2026-06-25):
//   col02: RID=0x47 UP=0x0006/U=0x0020 ("Battery Strength", BitSize=8).
//   Natively the descriptor declares 0x47 as Input-only (FeatureReportByteLength=0), so it is
//   unreadable: HidD_GetFeature/GetInputReport return nothing and ReadFile never fires (the
//   device only pushes on BT connect). Verified exhaustively: 6,859 consecutive read timeouts,
//   255-RID sweep with zero hits.
//
//   After the BTHPORT CachedServices SDP cache is patched to expose 0x47 as a *Feature* report
//   (scripts/kbd-patch-cachedservices.ps1 inserts 09 20 B1 02 at the COL02 close), col02 reports
//   FeatureReportByteLength=2 and a Feature ValueCap for Battery Strength. HidD_GetFeature(0x47)
//   then returns [0x47, pct] — confirmed live: GetFeature 0x47 -> [47 0E].
//
//   This is an active synchronous read on col02; DeviceRegistry.Discover() hands us the col02
//   interface path and creates a fresh instance per poll, so no caching/monitor thread is needed.
//   When the patch is absent (e.g. erased by a re-pair) the Feature cap is missing and we return
//   -2 ("present but blocked"). The tray shows that as status only — it does not run the
//   admin SDP-cache script.

using System.Runtime.InteropServices;

namespace MagicMouseTray;

internal sealed class KeyboardBatteryDevice : IBatteryDevice
{
    internal record struct VidPidEntry(string VidPattern, string PidPattern, string DisplayName);

    internal static readonly VidPidEntry[] KnownKeyboards =
    [
        // DESIGN: USB HID is VID_05AC&PID_xxxx for every PID already listed (BT/BLE rows
        // stay 000205AC PID&xxxx / 0001004C PID&xxxx). Do not invent BLE 0001004C PIDs.
        // 2011 revision (A1314) — PID source: Linux kernel hid-apple.c
        new("000205AC", "PID&0239", "Apple Wireless Keyboard (2011)"),
        new("VID_05AC",  "PID_0239", "Apple Wireless Keyboard (2011)"), // USB
        new("000205AC", "PID&023A", "Apple Wireless Keyboard (2011) ISO"),
        new("VID_05AC",  "PID_023A", "Apple Wireless Keyboard (2011) ISO"), // USB
        new("000205AC", "PID&023B", "Apple Wireless Keyboard (2011) JIS"),
        new("VID_05AC",  "PID_023B", "Apple Wireless Keyboard (2011) JIS"), // USB
        // Magic Keyboard (A1644, 2015+)
        new("000205AC", "PID&024F", "Magic Keyboard"),
        new("VID_05AC",  "PID_024F", "Magic Keyboard"), // USB
        new("000205AC", "PID&0250", "Magic Keyboard ISO"),
        new("VID_05AC",  "PID_0250", "Magic Keyboard ISO"), // USB
        // Magic Keyboard with Touch ID (A2449, 2021+)
        new("000205AC", "PID&0267", "Magic Keyboard with Touch ID"),
        new("VID_05AC",  "PID_0267", "Magic Keyboard with Touch ID"), // USB
        new("000205AC", "PID&026C", "Magic Keyboard with Touch ID ISO"),
        new("VID_05AC",  "PID_026C", "Magic Keyboard with Touch ID ISO"), // USB
        // PIDs below: numeric facts only from hid-ids.h (GPL) — no kernel code/comments copied.
        // Magic Keyboard 2021/2024 (BLE company-id 0x004C or BT-classic 0x05AC)
        new("0001004C", "PID&029C", "Magic Keyboard (2021)"),
        new("000205AC", "PID&029C", "Magic Keyboard (2021)"),
        new("VID_05AC",  "PID_029C", "Magic Keyboard (2021)"), // USB
        new("0001004C", "PID&029A", "Magic Keyboard with Touch ID (2021)"),
        new("000205AC", "PID&029A", "Magic Keyboard with Touch ID (2021)"),
        new("VID_05AC",  "PID_029A", "Magic Keyboard with Touch ID (2021)"), // USB
        new("0001004C", "PID&029F", "Magic Keyboard with Numeric Keypad (2021)"),
        new("000205AC", "PID&029F", "Magic Keyboard with Numeric Keypad (2021)"),
        new("VID_05AC",  "PID_029F", "Magic Keyboard with Numeric Keypad (2021)"), // USB
        new("0001004C", "PID&0320", "Magic Keyboard (2024)"),
        new("000205AC", "PID&0320", "Magic Keyboard (2024)"),
        new("VID_05AC",  "PID_0320", "Magic Keyboard (2024)"), // USB
        new("0001004C", "PID&0321", "Magic Keyboard with Touch ID (2024)"),
        new("000205AC", "PID&0321", "Magic Keyboard with Touch ID (2024)"),
        new("VID_05AC",  "PID_0321", "Magic Keyboard with Touch ID (2024)"), // USB
        new("0001004C", "PID&0322", "Magic Keyboard with Numeric Keypad (2024)"),
        new("000205AC", "PID&0322", "Magic Keyboard with Numeric Keypad (2024)"),
        new("VID_05AC",  "PID_0322", "Magic Keyboard with Numeric Keypad (2024)"), // USB
        new("000205AC", "PID&0255", "Apple Wireless Keyboard (2011 ANSI)"),
        new("VID_05AC",  "PID_0255", "Apple Wireless Keyboard (2011 ANSI)"), // USB
        new("000205AC", "PID&0256", "Apple Wireless Keyboard (2011 ISO)"),
        new("VID_05AC",  "PID_0256", "Apple Wireless Keyboard (2011 ISO)"), // USB
        new("000205AC", "PID&0257", "Apple Wireless Keyboard (2011 JIS)"),
        new("VID_05AC",  "PID_0257", "Apple Wireless Keyboard (2011 JIS)"), // USB
    ];

    // Battery Strength on Generic Device Controls page — readable as a Feature after the patch.
    const ushort UP_GENDEV_BATTERY    = 0x0006;
    const ushort USG_GENDEV_BATTSTRENG = 0x0020;

    readonly string _path;

    public string DeviceName { get; }
    public string Pid { get; }
    public DeviceKind Kind => DeviceKind.MagicKeyboard;

    internal KeyboardBatteryDevice(string path, string displayName)
    {
        _path = path;
        DeviceName = displayName;
        Pid = DeviceRegistry.ExtractPid(path);
    }

    // Active read of the col02 Battery Strength Feature report (RID 0x47).
    // Returns 1-100; -2 only when the battery report is not exposed (no Feature cap, or
    // HidD_GetFeature itself failed - the patch is needed); -1 on open/caps failure or on a
    // read that succeeded but answered with something that is not a level.
    public int GetBatteryPercent()
    {
        using var handle = HidNative.CreateFile(
            _path,
            0,                              // zero access — read Feature without needing GENERIC_READ
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero,
            HidNative.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            Logger.Log($"KB_OPEN_FAILED device={DeviceName} err={Marshal.GetLastWin32Error()}");
            return -1;
        }

        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed))
        {
            Logger.Log($"KB_PREPARSED_FAILED device={DeviceName}");
            return -1;
        }

        byte batteryReportId = 0;
        bool hasBatteryFeature = false;
        int featureLen = 0;
        try
        {
            var caps = new HidNative.HIDP_CAPS();
            if (HidNative.HidP_GetCaps(preparsed, ref caps) != HidNative.HIDP_STATUS_SUCCESS)
            {
                Logger.Log($"KB_CAPS_FAILED device={DeviceName}");
                return -1;
            }
            featureLen = caps.FeatureReportByteLength;
            Logger.Log($"KB_HIDP_CAPS device={DeviceName} FeatLen={featureLen} FeatValueCaps={caps.NumberFeatureValueCaps}");

            if (caps.NumberFeatureValueCaps > 0)
            {
                var fcaps = new HidNative.HIDP_VALUE_CAPS[caps.NumberFeatureValueCaps];
                ushort len = caps.NumberFeatureValueCaps;
                if (HidNative.HidP_GetValueCaps(2 /* Feature */, fcaps, ref len, preparsed) == HidNative.HIDP_STATUS_SUCCESS)
                {
                    for (int i = 0; i < len; i++)
                    {
                        if (fcaps[i].UsagePage == UP_GENDEV_BATTERY && fcaps[i].Usage == USG_GENDEV_BATTSTRENG)
                        {
                            hasBatteryFeature = true;
                            batteryReportId = fcaps[i].ReportID;
                            break;
                        }
                    }
                }
            }
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }

        // No Feature cap = unpatched descriptor (or patch erased by re-pair). Present but blocked.
        if (!hasBatteryFeature || featureLen <= 0)
        {
            Logger.Log($"KB_BATTERY_BLOCKED device={DeviceName} (no Feature 0x47 cap — patch needed)");
            return -2;
        }

        var fbuf = new byte[Math.Max(featureLen, 2)];
        fbuf[0] = batteryReportId;
        if (!HidNative.HidD_GetFeature(handle, fbuf, fbuf.Length))
        {
            Logger.Log($"KB_FEATURE_BLOCKED device={DeviceName} err={Marshal.GetLastWin32Error()} (Feature 0x{batteryReportId:X2} unreadable — patch needed)");
            return -2;
        }

        // Same level contract as every other IBatteryDevice (MouseBatteryDevice.IsRealLevel):
        // the floor is 1, because Apple firmware reports 1..100, so a read that succeeds with
        // exactly 0 came from a dead/phantom interface. The keyboard needs the floor because
        // DeviceRegistry.Discover keeps a paired keyboard's leftover USB col02 next to the live
        // interface, and a real 0 would end AdaptivePoller.BestReading's scan of the group ahead
        // of the live interface and fire the low-battery alert on a healthy keyboard. fbuf[1] is
        // a byte, so the previous >= 0 bound rejected nothing.
        var (pct, marker) = ClassifyFeatureByte(fbuf[1]);
        Logger.Log($"{marker} device={DeviceName} value={fbuf[1]} (Feature 0x{batteryReportId:X2})");
        return pct;
    }

    // The level decision for the Feature byte, split out of the read so it is provable without
    // the hardware (the model is MouseBatteryDevice.ParseRid90Percent). Pure and allocation-free:
    // the markers are literals and the tuple never leaves the stack.
    //
    // A read that SUCCEEDED but did not answer with a level is -1, never -2. -2 means "the
    // battery report is not exposed", and it is the only value that opens the elevated SDP-cache
    // patch offer (TrayMenu.ShowFixKeyboard, TrayApp.cs:271-272) and RepairPlanner.PlanOne rule 2d.
    // On this line the Feature cap is present and HidD_GetFeature
    // returned true, so the pairing record is demonstrably intact: a phantom col02 answering 0
    // must not buy a repair row that tells the user to re-patch a working keyboard. That is the
    // same answer the mouse's unified-Feature path gives (MouseBatteryDevice.cs:280-294).
    //
    // The rejection markers stay distinct from KB_FEATURE_BLOCKED on purpose: "patch needed"
    // would be false here, and GetLastWin32Error would be stale from an earlier call because
    // this read succeeded.
    internal static (int Pct, string Marker) ClassifyFeatureByte(byte raw) =>
        MouseBatteryDevice.IsRealLevel(raw)
            ? (raw, "KB_BATTERY_OK")
            : (-1, raw == 0 ? "KB_BATTERY_ZERO" : "KB_BATTERY_BAD");
}
