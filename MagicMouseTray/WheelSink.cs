// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MagicMouseTray;

// WheelSink.cs - does any wheel input from the Magic Mouse 2024 (PID 0323)
// actually reach Windows' raw input stack while the user is dragging on its
// surface?
//
// Why userland has to ask. The scroll filter's gesture engine emits wheel
// notches only for the lowest-id contact in TOUCH_STATE_DRAG, so a resting
// finger that holds a low slot id starves the notch and every other finger's
// travel is re-anchored and discarded. Nothing fails: the multitouch reports
// still arrive, the pointer still moves, the filter's own Diag counters still
// climb. Measured on the reference 0323 before the driver fix: 694 multitouch
// reports in, 0 wheel events out, with 457 raw pointer records from the SAME
// HID collection in the same window. The only layer where the two binaries
// differed at all was this one - raw input - which is why the tray has to
// count wheel events itself rather than trust any counter.
//
// What this file is NOT. It makes no verdict, and it never claims scroll is
// healthy. A single observation with zero wheel events is "no evidence", never
// "scroll is dead": on the WORKING binary 83 of 84 active touch seconds carried
// zero wheel and zero hwheel, because all 32 events fell inside one 1.34 s
// burst, and zero-wheel time on a healthy device is unbounded because it
// depends on whether the person chooses to scroll. So the deciding evidence
// comes from a USER-PROMPTED probe of two legs measured separately -
// ObservePromptedAsync once for a normal two-finger scroll (the positive
// control: it proves genuine two-finger contact and that the wheel path can
// carry a byte) and once for the asymmetric gesture that starves the reference
// finger (the discriminator). This file measures the legs; RepairPlanner
// compares them and is the only place a verdict is made. The sink never merges
// the legs.
//
// Three properties keep an observation honest, all learned the hard way:
//
//   Void      - no touch activity happened at all, so there is nothing to
//               conclude. Consumers MUST treat Void as "no verdict". A 60 s
//               window whose 305 mouse records all landed in the first 5 s,
//               with the filter's report counter flat across the whole window,
//               is void, not a negative result.
//   Decoder   - the wheel bit is read out of usButtonFlags at a hand-computed
//     Validated offset. DecoderValidated goes true only when a nonzero NON-wheel
//               flag (a real click: 0x0001/0x0002) is seen at that same offset,
//               which is the only honest proof the offset is right and so the
//               only thing that makes a zero wheel count meaningful. Injected
//               input cannot stand in for it: SendInput wheel is not delivered
//               to Raw Input at all (measured: 3x +120 and 2x -120 injected,
//               zero records seen), so there is no self-test anywhere here.
//   Target    - counts are attributed per hDevice, resolved to a device path,
//     scoping  and only the 0323 pointer collection (&Col01) is reported. A
//               second mouse on the same PC contributed stray records in the
//               reference runs, and an un-attributed aggregate would let its
//               wheel events mask the Magic Mouse's zero. hDevice CHANGES
//               across a device restart (0x5251099 -> 0xD3511F8 was observed
//               across a reinstall), so handles are resolved per observation
//               and never cached beyond it; the device PATH is the stable key.

// One measured window. Every field is scoped to TargetDevicePath except the
// two durations.
//
// AbsMotionSum is the sum of abs(lLastX)+abs(lLastY) over the window. Raw input
// mouse deltas are relative, so this sum IS the travel that happened inside the
// window - a consumer comparing consecutive observations needs no start/end
// odometer, only "> 0". It is reported as a measurement and as structural
// corroboration that the lLastX/lLastY offsets decode correctly; it is never
// this file's activity gate.
//
// [INFERENCE] pointer travel is a proxy for "the user is dragging", not proof
// of it: a two-finger scroll that moves no cursor is invisible to it, which
// biases any consumer toward silence rather than toward accusing a healthy
// driver. That is the correct direction to be wrong in.
internal sealed record WheelObservation(
    TimeSpan WallDuration,
    TimeSpan ActiveDuration,
    int MouseRecords,
    int WheelEvents,
    int HWheelEvents,
    int ButtonEvents,
    long AbsMotionSum,
    string? TargetDevicePath,
    bool DecoderValidated,
    bool Void);

internal interface IWheelSink
{
    // Passive path. Observes for at most `window`, measuring only once the touch
    // stream moves. Returns a partial observation when ct is cancelled - it never
    // throws OperationCanceledException, because a half-measured window is still
    // evidence and the caller is a poll tick, not a user request.
    Task<WheelObservation> ObserveAsync(TimeSpan window, CancellationToken ct);

    // Prompted path. The user has just been asked to scroll, so the whole window
    // is measured with no arming wait. Void still means "the surface was never
    // touched" - a user who ignored the prompt must not be scored as broken.
    Task<WheelObservation> ObservePromptedAsync(TimeSpan window, CancellationToken ct);
}

// One WM_INPUT mouse record, decoded to the fields the wheel question needs.
// ButtonData is the signed wheel delta (120 per notch) when a wheel bit is set
// in ButtonFlags, and is meaningless otherwise.
internal readonly record struct RawMouseRecord(
    IntPtr Device, ushort ButtonFlags, short ButtonData, int LastX, int LastY);

// The raw-input pump, behind a seam so the counting rules can be exercised with
// synthetic records, no window and no hardware.
internal interface IRawMouseSource
{
    // Delivers every mouse record to onRecord until ct is cancelled. onRecord is
    // invoked on the pump's own thread, never the caller's.
    Task PumpAsync(Action<RawMouseRecord> onRecord, CancellationToken ct);

    // hDevice -> device interface path, or null when it cannot be resolved.
    string? ResolveDevicePath(IntPtr device);
}

// Arms on activity, counts wheel events on the 0323 pointer collection, and
// reports what it saw. No registry access of its own: the activity question
// arrives as a Func<bool> so the Diag counter semantics (including the reinstall
// reset, which is only decidable from a before/after pair) stay in one place.
internal sealed class RawInputWheelSink : IWheelSink
{
    // RI_MOUSE_* bits in RAWMOUSE.usButtonFlags.
    internal const ushort RiMouseWheel = 0x0400;
    internal const ushort RiMouseHWheel = 0x0800;
    // Button down/up bits 0x0001..0x0200 (left, right, middle, 4, 5). Deliberately
    // excludes the two wheel bits: a wheel event must not validate the decoder it
    // is being read by.
    internal const ushort ButtonFlagMask = 0x03FF;

    // The activity probe is sampled at this cadence, clamped to what is left of
    // the window. 250 ms is the cadence the reference measurement ran at.
    internal static readonly TimeSpan ArmPollInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan ActiveTickInterval = TimeSpan.FromMilliseconds(250);
    // How long the sink waits for the pump thread to unwind after cancelling it,
    // so a wedged source cannot hang a tray poll tick.
    static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(2);

    readonly IRawMouseSource _source;
    readonly Func<bool?> _touchAdvanced;
    // Sticky decoder proof, keyed on the resolved device PATH (Project explains
    // why it is sticky). Its own lock: two observations on one sink instance have
    // separate per-observation state but share this set.
    readonly HashSet<string> _validatedPaths = new(StringComparer.OrdinalIgnoreCase);
    readonly object _stickyGate = new();

    // touchAdvanced: true when the device's touch report stream advanced since the
    // previous call, false when it provably did not, and NULL when there is no
    // evidence either way - Diag key missing, access denied, no usable baseline
    // yet, or a counter that went backwards because the device is mid-restart.
    // Null and false both mean "not armed" here, which is reported as Void: no
    // evidence is never rendered as inactivity, and nothing downstream gates a
    // fault on this seam. The fault path is the prompted probe, where the user's
    // intent is known. Sampled every ArmPollInterval, so it must be cheap.
    internal RawInputWheelSink(IRawMouseSource source, Func<bool?> touchAdvanced)
    {
        _source = source;
        _touchAdvanced = touchAdvanced;
    }

    // The 0323's pointer collection, e.g.
    //   \\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0323&Col01#a&31e5d054&2a&0000
    // Col01 is the pointer collection by construction on this device
    // (DeviceDiagReader.cs:556-567); Col02 is the vendor/battery collection and
    // never carries a wheel.
    internal static bool IsTargetDevicePath(string? path) =>
        !string.IsNullOrEmpty(path)
        && V3RecycleManager.IsV3Path(path)
        && path.Contains("col01", StringComparison.OrdinalIgnoreCase);

    // Passive path: nothing is measured until the touch stream moves, so a window
    // nobody touched comes back Void instead of as a zero.
    public Task<WheelObservation> ObserveAsync(TimeSpan window, CancellationToken ct) =>
        MeasureAsync(window, armOnActivity: true, ct);

    // Prompted path: intent is known, so there is nothing to wait for.
    public Task<WheelObservation> ObservePromptedAsync(TimeSpan window, CancellationToken ct) =>
        MeasureAsync(window, armOnActivity: false, ct);

    async Task<WheelObservation> MeasureAsync(TimeSpan window, bool armOnActivity, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sawActivity = false;

        if (armOnActivity)
        {
            // --- arm phase ---
            while (true)
            {
                if (TouchAdvanced() == true)
                {
                    sawActivity = true;
                    break;
                }

                var left = window - sw.Elapsed;
                if (left <= TimeSpan.Zero) break;
                if (!await QuietDelay(Shorter(ArmPollInterval, left), ct)) break;
            }

            if (!sawActivity)
                return new WheelObservation(sw.Elapsed, TimeSpan.Zero, 0, 0, 0, 0, 0, null, false, Void: true);
        }

        // --- measured window ---
        var gate = new object();
        var byDevice = new Dictionary<IntPtr, DeviceCounters>();
        var active = TimeSpan.Zero;
        var lastTick = sw.Elapsed;

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = StartPump(byDevice, gate, pumpCts.Token);
        try
        {
            while (true)
            {
                var left = window - sw.Elapsed;
                if (left <= TimeSpan.Zero) break;
                if (!await QuietDelay(Shorter(ActiveTickInterval, left), ct)) break;

                var now = sw.Elapsed;
                // Only time in which the touch stream actually moved counts as
                // active: a hand lifted mid-window must not be reported as touch
                // time in which scroll failed to happen.
                if (TouchAdvanced() == true)
                {
                    sawActivity = true;
                    active += now - lastTick;
                }
                lastTick = now;
            }
        }
        finally
        {
            pumpCts.Cancel();
            await Task.WhenAny(pump, Task.Delay(PumpDrainTimeout, CancellationToken.None));
        }

        lock (gate)
            return Project(byDevice, sw.Elapsed, active, sawActivity);
    }

    Task StartPump(Dictionary<IntPtr, DeviceCounters> byDevice, object gate, CancellationToken ct)
    {
        try
        {
            return _source.PumpAsync(rec => Accumulate(byDevice, gate, rec), ct);
        }
        catch (Exception ex)
        {
            // A source that cannot start at all still leaves a reportable window:
            // zero counts with no target path, which no consumer can read as a
            // verdict.
            Logger.Log($"WHEEL_SINK_PUMP_START_FAILED err={ex.GetBaseException().Message}");
            return Task.CompletedTask;
        }
    }

    // Runs on the pump thread.
    void Accumulate(Dictionary<IntPtr, DeviceCounters> byDevice, object gate, RawMouseRecord rec)
    {
        lock (gate)
        {
            if (!byDevice.TryGetValue(rec.Device, out var dev))
            {
                // Resolved once per hDevice per observation - never cached across
                // observations, because the handle changes when the device restarts.
                dev = new DeviceCounters(ResolveDevicePath(rec.Device));
                byDevice[rec.Device] = dev;
            }

            dev.MouseRecords++;
            dev.AbsMotionSum += Math.Abs((long)rec.LastX) + Math.Abs((long)rec.LastY);
            if ((rec.ButtonFlags & ButtonFlagMask) != 0) dev.ButtonEvents++;
            if ((rec.ButtonFlags & RiMouseWheel) != 0) dev.WheelEvents++;
            if ((rec.ButtonFlags & RiMouseHWheel) != 0) dev.HWheelEvents++;
        }
    }

    // Collapses the per-hDevice tallies onto the target device. Every non-target
    // device is dropped rather than summed: the whole point of the attribution is
    // that another mouse's wheel cannot mask the 0323's zero. Two handles can
    // carry the SAME target path when the device restarts mid-window, so the
    // target is chosen by path and its handles are summed.
    WheelObservation Project(
        Dictionary<IntPtr, DeviceCounters> byDevice, TimeSpan wall, TimeSpan active, bool sawActivity)
    {
        string? target = null;
        var best = -1L;
        foreach (var dev in byDevice.Values)
        {
            if (!IsTargetDevicePath(dev.Path)) continue;
            var records = 0L;
            foreach (var other in byDevice.Values)
                if (string.Equals(other.Path, dev.Path, StringComparison.OrdinalIgnoreCase))
                    records += other.MouseRecords;
            if (records > best)
            {
                best = records;
                target = dev.Path;
            }
        }

        if (target is null)
            return new WheelObservation(wall, active, 0, 0, 0, 0, 0, null, false, Void: !sawActivity);

        int records2 = 0, wheel = 0, hwheel = 0, buttons = 0;
        long motion = 0;
        foreach (var dev in byDevice.Values)
        {
            if (!string.Equals(dev.Path, target, StringComparison.OrdinalIgnoreCase)) continue;
            records2 += dev.MouseRecords;
            wheel += dev.WheelEvents;
            hwheel += dev.HWheelEvents;
            buttons += dev.ButtonEvents;
            motion += dev.AbsMotionSum;
        }

        // DecoderValidated is STICKY per device path for this sink's lifetime.
        // A prompted "scroll for ten seconds" window gives the user no reason to
        // click, so recomputing it per window would leave every probe
        // inconclusive; the validating clicks arrive opportunistically instead
        // (measured: 0x0001 x7 and 0x0002 x7 across one 100 s window, alongside
        // 0x0400 x24). Keyed on the PATH, not on hDevice, because the handle
        // changes when the device re-enumerates while the path does not.
        // ButtonEvents stays the per-window fact for the same evidence.
        return new WheelObservation(
            wall, active, records2, wheel, hwheel, buttons, motion, target,
            DecoderValidated: RememberDecoderProof(target, buttons > 0), Void: !sawActivity);
    }

    bool RememberDecoderProof(string path, bool provenThisWindow)
    {
        lock (_stickyGate)
        {
            if (provenThisWindow) _validatedPaths.Add(path);
            return _validatedPaths.Contains(path);
        }
    }

    string? ResolveDevicePath(IntPtr device)
    {
        try
        {
            return _source.ResolveDevicePath(device);
        }
        catch (Exception ex)
        {
            Logger.Log($"WHEEL_SINK_PATH_FAILED err={ex.GetBaseException().Message}");
            return null;
        }
    }

    // A probe that throws is no evidence, not a negative: null keeps the window
    // unarmed and the observation Void.
    bool? TouchAdvanced()
    {
        try
        {
            return _touchAdvanced();
        }
        catch (Exception ex)
        {
            Logger.Log($"WHEEL_SINK_PROBE_FAILED err={ex.GetBaseException().Message}");
            return null;
        }
    }

    // False when the wait was cut short by cancellation.
    static async Task<bool> QuietDelay(TimeSpan span, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        if (span <= TimeSpan.Zero) return true;
        try
        {
            await Task.Delay(span, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? a : b;

    sealed class DeviceCounters(string? path)
    {
        internal string? Path { get; } = path;
        internal int MouseRecords;
        internal int WheelEvents;
        internal int HWheelEvents;
        internal int ButtonEvents;
        internal long AbsMotionSum;
    }
}

// The real pump: a message-only window on its own thread, RIDEV_INPUTSINK, and
// a hand-decoded RAWMOUSE.
//
// This app has no message-only window and no WndProc of its own to borrow - the
// host is WPF (App.xaml.cs:47) hosting a WinForms NotifyIcon (TrayApp.cs:624,
// :763), and the only other user32 P/Invoke in the app is DestroyIcon
// (TrayApp.cs:3433). So the window is created here, and deliberately NOT on the
// WPF UI thread: this loop owns its thread's message queue for the length of an
// observation, which would stall the tray menu.
//
// Verified on the reference PC, non-elevated and with no focus: HWND_MESSAGE +
// RIDEV_INPUTSINK delivers WM_INPUT. Two details that are load-bearing:
//   - the window uses the stock STATIC class, so there is no class to register,
//     no WndProc delegate to keep alive and no class to unregister;
//   - WM_INPUT is handled in the peek loop and STILL dispatched, because
//     DefWindowProc performs the WM_INPUT cleanup.
internal sealed class RawInputMouseSource : IRawMouseSource
{
    const uint RidevRemove = 0x00000001;
    const uint RidevInputSink = 0x00000100;
    const uint RidInput = 0x10000003;
    const uint RimTypeMouse = 0;
    const uint WmInput = 0x00FF;
    const uint PmRemove = 0x0001;
    const uint RidiDeviceName = 0x20000007;
    const ushort UsagePageGeneric = 0x01;
    const ushort UsageMouse = 0x02;

    static readonly IntPtr HwndMessage = new(-3);

    // The queue holds messages while this loop sleeps, so polling costs latency,
    // not records. The device streams ~65 reports/s, i.e. under one message per
    // sleep, and this keeps a background thread off a spin loop.
    static readonly TimeSpan PollSleep = TimeSpan.FromMilliseconds(10);

    // dwType, dwSize, hDevice, wParam - 24 bytes on x64, 16 on x86. RAWMOUSE
    // follows immediately, and within it usButtonFlags is at +4, the signed
    // usButtonData at +6, lLastX at +12 and lLastY at +16. These offsets were
    // validated on hardware: lLastX/lLastY tracked real pointer travel
    // (abs sum 32264 over one 84 s window) while the wheel bits at +4 matched
    // physical notches (24 events summing to +2640 = 22 notches at 120/notch).
    static readonly int HeaderSize = 8 + 2 * IntPtr.Size;

    public Task PumpAsync(Action<RawMouseRecord> onRecord, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Pump(onRecord, ct);
            }
            catch (Exception ex)
            {
                // A failed pump must not fault the observation: the sink reports
                // the (empty) window instead, which is not a verdict.
                Logger.Log($"WHEEL_SINK_PUMP_FAILED err={ex.GetBaseException().Message}");
            }
            finally
            {
                done.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "mm-wheel-sink",
        };
        thread.Start();
        return done.Task;
    }

    static void Pump(Action<RawMouseRecord> onRecord, CancellationToken ct)
    {
        var hwnd = CreateWindowExW(0, "STATIC", "mm-wheel-sink", 0, 0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Logger.Log($"WHEEL_SINK_WINDOW_FAILED gle={Marshal.GetLastWin32Error()}");
            return;
        }

        var registered = false;
        var buffer = IntPtr.Zero;
        var bufferSize = 0;
        try
        {
            var cb = (uint)Marshal.SizeOf<RAWINPUTDEVICE>();
            var rid = new RAWINPUTDEVICE[1];
            rid[0].usUsagePage = UsagePageGeneric;
            rid[0].usUsage = UsageMouse;
            rid[0].dwFlags = RidevInputSink;   // delivered with no focus, no elevation
            rid[0].hwndTarget = hwnd;
            if (!RegisterRawInputDevices(rid, 1, cb))
            {
                Logger.Log($"WHEEL_SINK_REGISTER_FAILED gle={Marshal.GetLastWin32Error()}");
                return;
            }
            registered = true;

            bufferSize = 256;
            buffer = Marshal.AllocHGlobal(bufferSize);

            while (!ct.IsCancellationRequested)
            {
                if (!Drain(onRecord, ref buffer, ref bufferSize))
                    ct.WaitHandle.WaitOne(PollSleep);
            }
        }
        finally
        {
            if (registered)
            {
                // RIDEV_REMOVE requires a null hwndTarget. The tray outlives the
                // observation, so leaving the registration behind would keep
                // feeding a destroyed window.
                var off = new RAWINPUTDEVICE[1];
                off[0].usUsagePage = UsagePageGeneric;
                off[0].usUsage = UsageMouse;
                off[0].dwFlags = RidevRemove;
                off[0].hwndTarget = IntPtr.Zero;
                if (!RegisterRawInputDevices(off, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                    Logger.Log($"WHEEL_SINK_DEREGISTER_FAILED gle={Marshal.GetLastWin32Error()}");
            }
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            DestroyWindow(hwnd);
        }
    }

    // Drains this thread's queue. True when at least one message was handled.
    static bool Drain(Action<RawMouseRecord> onRecord, ref IntPtr buffer, ref int bufferSize)
    {
        var any = false;
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PmRemove))
        {
            any = true;
            if (msg.message == WmInput)
            {
                var rec = Decode(msg.lParam, ref buffer, ref bufferSize);
                if (rec.HasValue) onRecord(rec.Value);
            }
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);   // DefWindowProc performs the WM_INPUT cleanup
        }
        return any;
    }

    static RawMouseRecord? Decode(IntPtr hRawInput, ref IntPtr buffer, ref int bufferSize)
    {
        uint size = 0;
        if (GetRawInputData(hRawInput, RidInput, IntPtr.Zero, ref size, (uint)HeaderSize) != 0 || size == 0)
            return null;

        if (size > bufferSize)
        {
            Marshal.FreeHGlobal(buffer);
            bufferSize = (int)size;
            buffer = Marshal.AllocHGlobal(bufferSize);
        }

        var got = size;
        var read = GetRawInputData(hRawInput, RidInput, buffer, ref got, (uint)HeaderSize);
        if (read == unchecked((uint)-1) || read < (uint)HeaderSize)
            return null;

        if ((uint)Marshal.ReadInt32(buffer, 0) != RimTypeMouse)
            return null;

        return new RawMouseRecord(
            Marshal.ReadIntPtr(buffer, 8),
            (ushort)Marshal.ReadInt16(buffer, HeaderSize + 4),
            Marshal.ReadInt16(buffer, HeaderSize + 6),
            Marshal.ReadInt32(buffer, HeaderSize + 12),
            Marshal.ReadInt32(buffer, HeaderSize + 16));
    }

    public string? ResolveDevicePath(IntPtr device)
    {
        uint chars = 0;
        GetRawInputDeviceInfoW(device, RidiDeviceName, IntPtr.Zero, ref chars);
        if (chars == 0 || chars > 8192) return null;

        var p = Marshal.AllocHGlobal(((int)chars + 1) * 2);
        try
        {
            var got = chars;
            if (GetRawInputDeviceInfoW(device, RidiDeviceName, p, ref got) == unchecked((uint)-1))
            {
                Logger.Log($"WHEEL_SINK_DEVNAME_FAILED gle={Marshal.GetLastWin32Error()}");
                return null;
            }
            return Marshal.PtrToStringUni(p);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData,
        ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData,
        ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,
        uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DispatchMessageW(ref MSG lpMsg);
}
