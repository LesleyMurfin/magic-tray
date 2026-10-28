// SPDX-License-Identifier: MIT
// IBatteryDevice implementation for directly-connected (BT/USB) Logitech HID++ 2.0 devices.
//
// EXPERIMENTAL — flag-gated behind Config.EnableThirdParty (default off). No Logitech hardware
// was available to verify this path; it is concrete, buildable scaffolding only.
//
// The HID++ 2.0 negotiation TECHNIQUE (Root feature lookup, then BatteryStatus/UnifiedBattery
// query) is reimplemented from the MIT-licensed project Ithilias/logitray. No source was copied;
// this is an independent C# implementation. See THIRD-PARTY-NOTICES.md.
//
// SCOPE: directly-connected devices only (deviceIndex 0xFF addresses the device itself).
// Unifying/Bolt receivers are out of scope (they require per-paired-device index 1-6) and are
// excluded at discovery time (DeviceRegistry.IsLogitechReceiver).
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MagicMouseTray;

internal sealed class LogitechBatteryDevice : IBatteryDevice
{
    const ushort UP_VENDOR = 0xFF00;   // HID++ vendor-defined collection
    const byte DEV_INDEX   = 0xFF;     // direct-connect: addresses the device itself
    const byte SW_ID       = 0x05;     // arbitrary software id (low nibble of byte3)
    const byte SHORT_ID    = 0x10;     // short report: 7 bytes total
    const byte LONG_ID     = 0x11;     // long report: 20 bytes total
    const int  TIMEOUT_MS  = 600;      // mirrors the per-read budget elsewhere in the app

    readonly string _path;

    // Identity comes from the interface path, not from a constant. Pid and DeviceName are the
    // only keys anything downstream has: AdaptivePoller groups interfaces by DeviceName (one
    // tray row, one winning reading per group), and both AdaptivePoller.ShouldSkipPid and the
    // config enable/disable map are keyed by Pid. A fixed "Logitech Mouse"/"logi" pair made
    // every Logitech interface one identity, so a keyboard, a mouse and a headset dongle
    // enumerated in the same cycle formed a single group - whichever answered first owned the
    // tray row, one checkbox enabled or disabled all three, and every non-vendor interface
    // among them still drew a GENERIC_WRITE open that failed once per tick.
    //
    // The split is not free, and the cost is paid in poll-cycle wall clock, not in reads.
    // AdaptivePoller's budget is per DeviceName GROUP - ReadDevice is called once per
    // GroupBy(d => d.DeviceName) iteration - and DeviceRegistry documents the worst case for one
    // group on one tick as DeviceGroupBudget plus one DeviceReadTimeout, 10 s at today's values.
    // Under the fused "logi" identity, K non-answering vid_046d products were ONE group and
    // therefore one 10 s bound between POLL_SCHEDULED lines. Per-product groups turn that into
    // K x 10 s: a user with three silent Logitech products can hold a cycle for 30 s, during
    // which no tick is scheduled and no row refreshes. Anyone sizing the poll interval has to
    // size it against the number of Logitech products present, not against a constant. What the
    // split buys for that is correctness - one tray row and one checkbox per real device - plus
    // the per-tick failing GENERIC_WRITE opens that ShouldSkipPid can now retire per product
    // instead of all-or-nothing. The exposure is bounded by Config.EnableThirdParty, which
    // ships OFF, so no default install pays any of this.
    //
    // The name is not queried from the device (HID++ 0x0005 DeviceName would cost two more
    // round trips per interface per tick), so the product id stands in for the model name. That
    // is all this name is used for: DeviceCapability.KindForName only scans
    // MouseBatteryDevice.KnownMice and KeyboardBatteryDevice.KnownKeyboards, neither of which
    // has ever held a Logitech row, so no name->kind lookup regresses - Kind is the fixed
    // LogitechMouse either way.
    //
    // Consequence: a config entry keyed "logi" no longer matches and the device returns to its
    // default-enabled state. That is acceptable only because this whole path is behind
    // Config.EnableThirdParty, which ships off, so no shipped-on configuration can hold one.
    public string DeviceName { get; }
    public string Pid { get; }
    public DeviceKind Kind => DeviceKind.LogitechMouse;
    public string DevicePath => _path;

    internal LogitechBatteryDevice(string path)
    {
        _path = path;
        // DeviceRegistry.ExtractPid is the repo's path->pid parser (it also keys the discovery
        // log lines and the 0323 collection gate); its documented fallback for a path with no
        // pid_/pid& token is "0000", which keeps an unparseable interface stable and distinct
        // instead of giving it a blank group key.
        Pid = DeviceRegistry.ExtractPid(path);
        DeviceName = $"Logitech Device {Pid.ToUpperInvariant()}";
    }

    public int GetBatteryPercent()
    {
        using var handle = HidNative.CreateFile(
            _path,
            HidNative.GENERIC_READ | 0x40000000u /* GENERIC_WRITE */,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero,
            HidNative.OPEN_EXISTING,
            HidNative.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            Logger.Log($"LOGI_OPEN_FAILED path={_path} err={Marshal.GetLastWin32Error()}");
            return -1;
        }

        // Confirm vendor collection and pick short vs long report length from OutputReportByteLength.
        int reportLen;
        byte reportId;
        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed)) return -1;
        try
        {
            var caps = new HidNative.HIDP_CAPS();
            if (HidNative.HidP_GetCaps(preparsed, ref caps) != HidNative.HIDP_STATUS_SUCCESS) return -1;
            if (caps.UsagePage != UP_VENDOR) { Logger.Log($"LOGI_NOT_VENDOR up=0x{caps.UsagePage:X4}"); return -1; }
            if (caps.OutputReportByteLength >= 20) { reportId = LONG_ID; reportLen = 20; }
            else                                   { reportId = SHORT_ID; reportLen = 7; }
        }
        finally { HidNative.HidD_FreePreparsedData(preparsed); }

        // Prefer BatteryStatus (0x1000, fn0 = % directly); fall back to UnifiedBattery (0x1004, fn1).
        int pct = ReadFeatureBattery(handle, reportId, reportLen, 0x1000, funcIndex: 0);
        if (pct < 0)
            pct = ReadFeatureBattery(handle, reportId, reportLen, 0x1004, funcIndex: 1);

        if (pct >= 0) Logger.Log($"LOGI_BATTERY_OK device={DeviceName} pct={pct}%");
        return pct;
    }

    // Resolves a HID++ 2.0 feature via Root (0x0000) GetFeature, then queries it for battery %.
    int ReadFeatureBattery(SafeFileHandle handle, byte reportId, int reportLen, ushort featureId, byte funcIndex)
    {
        // Root.GetFeature(featureId): byte3 = 0x00 | swId (function 0 on the Root feature).
        var rootReq = NewReport(reportId, reportLen, featureIndex: 0x00,
            funcByte: (byte)(0x00 | SW_ID), arg0: (byte)(featureId >> 8), arg1: (byte)(featureId & 0xFF));
        // SendReceive issues exactly ONE ReadFile and hands that single frame to
        // IsMatchingResponse; there is no drain loop. So a frame the matcher rejects does not
        // cost one frame, it costs the whole read: this call returns null and ReadFeatureBattery
        // answers -1 for the tick. On a device that pushes notifications on the very feature
        // being queried - which the comment over IsMatchingResponse says a battery feature is
        // exactly the kind to do - the pushed frame is now correctly refused and the real reply
        // behind it in the queue is never read, so such a device reports no battery at all where
        // it used to report an occasional wrong one. That is the intended trade (a stranger's
        // level shown as this device's is worse than an empty row, and the rejection is logged),
        // but it IS a behaviour change from the two-byte matcher, recorded here because the
        // symptom - a Logitech row that is permanently blank rather than wrong - looks like a
        // discovery or permissions fault and is not one. A drain/retry loop is the obvious
        // remedy and is deliberately not here: it changes the per-read timing this path shares
        // with the rest of the app and there is no hardware or seam to validate it against.
        var rootResp = SendReceive(handle, rootReq, reportLen);
        if (rootResp is null) return -1;
        byte featureIndex = rootResp[4];
        if (featureIndex == 0) return -1; // feature absent on this device

        // Query the feature: byte3 = (funcIndex << 4) | swId.
        var req = NewReport(reportId, reportLen, featureIndex,
            funcByte: (byte)((funcIndex << 4) | SW_ID), arg0: 0, arg1: 0);
        var resp = SendReceive(handle, req, reportLen);
        if (resp is null) return -1;

        // % at offset 4 for both 0x1000 GetBatteryLevelStatus and 0x1004 GetStatus.
        var (pct, marker) = ClassifyLevelByte(resp[4]);
        // GetBatteryPercent logs LOGI_BATTERY_OK once for whichever feature answered; the
        // rejection has to be logged here, where the raw byte and the feature that produced it
        // are still in hand. Before this it was silent, which made the IBatteryDevice contract's
        // "each logs a distinct zero marker" false for this implementation.
        if (pct < 0)
            Logger.Log($"{marker} device={DeviceName} value={resp[4]} (HID++ feature 0x{featureId:X4} fn{funcIndex} answered outside 1-100)");
        return pct;
    }

    // The level decision for the HID++ battery byte, split out of the read so it is provable
    // without Logitech hardware (the model is MouseBatteryDevice.ParseRid90Percent). Pure and
    // allocation-free: the markers are literals and the tuple never leaves the stack.
    //
    // Same level contract as the Apple paths (MouseBatteryDevice.IsRealLevel), floor 1: a real
    // percentage ends AdaptivePoller.BestReading's scan of the group, so an unfloored 0 from a
    // dead interface would win over a live one's failure sentinel and alert at 0%.
    //
    // A rejected value is -1, never -2: -2 means "the battery report is not exposed", and a
    // device that answered the Root feature query has demonstrably exposed it. (Nothing routes
    // a Logitech device to a repair offer either way - RepairPlanner rule 2d is gated on
    // DriverHealthChecker.IsKeyboardPid and TrayMenu.ShowFixKeyboard on DeviceKind.MagicKeyboard.)
    internal static (int Pct, string Marker) ClassifyLevelByte(byte raw) =>
        MouseBatteryDevice.IsRealLevel(raw)
            ? (raw, "LOGI_BATTERY_OK")
            : (-1, raw == 0 ? "LOGI_BATTERY_ZERO" : "LOGI_BATTERY_BAD");

    static byte[] NewReport(byte reportId, int len, byte featureIndex, byte funcByte, byte arg0, byte arg1)
    {
        var b = new byte[len];
        b[0] = reportId;
        b[1] = DEV_INDEX;
        b[2] = featureIndex;
        b[3] = funcByte;
        b[4] = arg0;
        b[5] = arg1;
        return b;
    }

    // Decides whether the frame that arrived on this handle is the answer to `req`, before
    // ReadFeatureBattery reads byte 4 as a Root feature index or as a battery percentage.
    // Internal and pure because everything around it in SendReceive needs a live HID handle,
    // and this rule is the part that can be proved without Logitech hardware.
    //
    // byte 3 is (funcIndex << 4) | SW_ID, and it used to go uncompared while the comment over
    // the compare claimed it was being checked. A HID++ device also PUSHES notifications on the
    // feature index it was just queried on, and a battery feature is exactly the kind that
    // does; such a frame carries our report id and our feature index, so a two-byte compare
    // accepted it and ClassifyLevelByte read a stranger's byte 4 as a level. Byte 3 ties the
    // frame to this request's function AND to our software id, which is what makes it our reply
    // rather than another HID++ client's or the device's own event.
    //
    // `transferred` is what GetOverlappedResult reported, and it is checked against 5 rather
    // than only against 0. The buffer comes from GC.AllocateArray, so a 1-4 byte transfer - a
    // complete, correctly matching header with the payload still missing - leaves byte 4 at the
    // allocation's zero, which used to decode as battery 0 and log LOGI_BATTERY_ZERO against
    // the device: a truncated transfer reported as a flat battery. Byte 4 is the first byte
    // either caller reads, so 5 bytes is the minimum decodable frame. A count larger than the
    // buffer is refused too - it cannot describe this read, and trusting it would hand a later
    // indexer a length the array does not have.
    //
    // 0xFF in byte 2 is a HID++ 2.0 error frame: its layout is [rid][devIndex][FF][failed
    // feature index][funcIndex<<4|swId][error code], so byte 4 - the byte both callers decode -
    // is the echoed function byte, 0x05 for our requests, which reads as 5%. It is rejected as a
    // VALUE, not left to the byte-2 compare, because nothing rejects a Root reply that names
    // feature index 0xFF (only index 0 is treated as absent), and such a device could otherwise
    // match its own error frames.
    //
    // byte 1 is the HID++ device index, and leaving it out was the same class of hole byte 3
    // was. Our requests always address the device directly (DEV_INDEX = 0xFF); a receiver or any
    // other multiplexing interface carries frames for every paired device on one report id, and
    // such a frame only has to share our feature index and our function|swId to have been
    // accepted - at which point byte 4 is a DIFFERENT device's battery reported as this row's.
    // Discovery excludes receivers only by a short PID substring list, so any receiver model
    // outside that list arrives here and this compare is the only thing standing in front of it.
    internal static bool IsMatchingResponse(byte[] request, byte[] response, int transferred)
    {
        if (request is null || response is null) return false;
        if (request.Length < 4 || response.Length < 5) return false;
        if (transferred < 5 || transferred > response.Length) return false;
        if (response[2] == 0xFF) return false;
        return response[0] == request[0] && response[1] == request[1]
            && response[2] == request[2] && response[3] == request[3];
    }

    // Writes the HID++ request and waits (overlapped) for the matching response, reusing the
    // same event/wait helpers KeyboardBatteryDevice-era code uses for timed HID reads.
    static byte[]? SendReceive(SafeFileHandle handle, byte[] req, int reportLen)
    {
        if (!HidNative.HidD_SetOutputReport(handle, req, req.Length))
            return null;

        // CreateEvent's failure was unchecked. A NULL hEvent is not a harmless no-op here:
        // ReadFile still starts the read, WaitForSingleObject(NULL, 600) returns WAIT_FAILED
        // instead of WAIT_OBJECT_0, and the cancel path below is taken for a read that is
        // behaving perfectly normally - burning the whole 600 ms budget and then blocking in
        // the deliberately unbounded GetOverlappedResult(bWait: true) wait. There is no
        // injectable seam for the P/Invoke, so this is a distinct log marker rather than a test.
        IntPtr evt = HidNative.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
        if (evt == IntPtr.Zero)
        {
            Logger.Log($"LOGI_EVENT_FAILED err={Marshal.GetLastWin32Error()} (no overlapped event; read not issued)");
            return null;
        }

        try
        {
            // Pinned for the whole pending window, not just for the ReadFile call: the
            // marshaller pins a blittable array only until the call returns, and this read
            // returns ERROR_IO_PENDING immediately, so an ordinary array could be relocated
            // while the driver still holds the address it was handed.
            var buf = GC.AllocateArray<byte>(reportLen, pinned: true);
            var ov = new HidNative.OVERLAPPED { hEvent = evt };
            if (!HidNative.ReadFile(handle, buf, (uint)buf.Length, IntPtr.Zero, ref ov))
            {
                if ((uint)Marshal.GetLastWin32Error() != HidNative.ERROR_IO_PENDING) return null;
                if (HidNative.WaitForSingleObject(evt, TIMEOUT_MS) != HidNative.WAIT_OBJECT_0)
                {
                    // CancelIo only REQUESTS cancellation: the read can still be in flight when
                    // it returns. Without waiting for the cancelled I/O to finish, the finally
                    // below closes the event and this frame returns while the kernel still owns
                    // `ov` (a local on a frame that is going away) - it would later signal a
                    // closed-and-possibly-reused handle and write into stack memory this frame
                    // no longer owns. GetOverlappedResult with bWait=true blocks until the
                    // cancellation has actually completed; the result is uninteresting, only the
                    // completion is. `buf` is a separate problem that keeping this frame alive
                    // does not solve, because a managed array lives on the heap and the GC can
                    // move it regardless; that is what the pinned allocation above is for.
                    //
                    // This wait is deliberately unbounded, unlike every other wait in this file,
                    // which uses TIMEOUT_MS: a driver that ignores CancelIo parks this worker for
                    // good, and that is the lesser evil, because AdaptivePoller.ReadBatteryGuarded
                    // has already abandoned the read by the time we get here.
                    HidNative.CancelIo(handle);
                    HidNative.GetOverlappedResult(handle, ref ov, out _, true);
                    return null;
                }
            }
            if (!HidNative.GetOverlappedResult(handle, ref ov, out uint got, false))
                return null;
            if (!IsMatchingResponse(req, buf, (int)got)) return null;
            return buf;
        }
        finally { HidNative.CloseHandle(evt); }
    }
}
