// SPDX-License-Identifier: MIT
using System.Diagnostics;

namespace MagicMouseTray;

// One interface's guarded read: the value (a percentage or a failure sentinel), whether the read
// was abandoned at its budget, and what it actually cost. Took is what a group's wall-clock
// budget is spent against, and it is reported by the read rather than measured by the scan so a
// test can script a 4 s read without spending 4 s.
internal readonly record struct GuardedRead(int Pct, bool TimedOut, TimeSpan Took);

// How a scan performs one interface's read. Production passes AdaptivePoller.ReadBatteryGuarded;
// tests substitute a script, so an interface that never answers costs no wall clock and parks no
// thread-pool worker that the rest of the suite then runs short of.
internal delegate GuardedRead ReadLauncher(IBatteryDevice device, TimeSpan budget);

// A device's rotating scan position: the interface index its next scan starts at, and how many
// complete rotations it has made since its last real reading. Rotations is what keeps the -3
// escalation from out-running the rotation - see AdaptivePoller.ShouldEscalate.
internal readonly record struct ScanState(int Start, int Rotations);

// Polls all discovered Apple HID battery devices at adaptive intervals driven by
// DrainRateTracker. Records each reading into DrainRateTracker so subsequent
// intervals reflect the observed drain rate.
//
// BatteryChanged is raised from a thread-pool thread — callers must marshal
// to the UI thread before touching WPF/NotifyIcon objects (done in TrayApp).
//
// A v3 Magic Mouse whose Input 0x90 read never completes returns pct=-2, and so does a
// [90 xx 00] report, because a zeroed 0x90 is the battery report failing to arrive at all. A
// 0x90 that arrives carrying a junk percent byte is -1 (MOUSE_RID90_BAD_CONTENT).
// Never Feature 0x47, never WMI Hands-Free. The tray never flips LowerFilters.
internal sealed class AdaptivePoller : IDisposable
{
    // Fired once per discovered device per poll cycle.
    // percent sentinel values, as IBatteryDevice defines them: -1 = not found/disconnected, or a
    // report that arrived and is not a level, or a read this poller abandoned at its budget;
    // -2 = the battery report never arrived (the capability is absent, or the HID read itself
    // came back failed - MouseBatteryDevice.ClassifyReadFailure - or a v3 [90 xx 00]);
    // -3 = battery unavailable, minted below when a device has failed EscalationFailures ticks
    // AND rotation has completed a turn (see ShouldEscalate).
    internal event Action<int, string, DeviceKind, string>? BatteryChanged;

    // Last computed interval — readable by TrayApp for tooltip.
    internal TimeSpan LastInterval { get; private set; } = TimeSpan.FromMinutes(5);

    readonly Config _config;
    // How this poller reaches the HID world and the log. See the constructor.
    readonly Func<bool, IReadOnlyList<IBatteryDevice>> _discover;
    readonly Action<string> _log;
    // One source for the poller's lifetime, so Dispose cannot race a reader of the field. The
    // only thing that used to replace it was RefreshNow, which now signals instead.
    readonly CancellationTokenSource _cts = new();
    Task? _pollTask;
    readonly RefreshSignal _refresh;
    volatile bool _disposed;
    int _loopStarts;

    // Cycle state. Every one of these is read-modify-written by the poll loop and by nothing
    // else: there is exactly ONE PollLoop for a poller's lifetime (Start creates it and refuses
    // to create a second; RefreshNow only wakes it), so this state is single-writer. That is the
    // invariant the whole arrangement rests on - two loops racing these dictionaries corrupt the
    // bucket chain, which is a non-terminating Insert spin or an IndexOutOfRangeException,
    // swallowed by the cycle guard as POLL_CYCLE_ERROR. Anything added here must stay inside the
    // loop.
    readonly Dictionary<string, int> _consecutiveFailures = new();
    readonly Dictionary<string, (DeviceKind Kind, string Pid)> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    // Next interface index and rotation count for a device's scan, per DeviceName. See ReadDevice.
    readonly Dictionary<string, ScanState> _scanStart = new(StringComparer.OrdinalIgnoreCase);
    readonly WedgeTracker _wedges = new();
    // Refilled per group / per cycle rather than reallocated: ReadDevice only indexes its list
    // and Prune only enumerates. Holding them for the poller's lifetime is safe for the same
    // single-writer reason as the dictionaries above.
    readonly List<IBatteryDevice> _groupBuffer = new();
    readonly List<string> _livePaths = new();

    // How many times PollLoop has been entered. One, for the poller's lifetime — the property
    // exists so that invariant is checkable rather than merely intended.
    internal int LoopStarts => Volatile.Read(ref _loopStarts);


    // Per-device read budget. A synchronous HID IOCTL (HidD_GetFeature / HidD_GetInputReport)
    // can block indefinitely on a wedged device, and before this guard one stuck device froze
    // the whole poll loop so POLL_SCHEDULED never fired and no battery ever reached the tray.
    static readonly TimeSpan DeviceReadTimeout = TimeSpan.FromSeconds(5);
    // Whole-group wall-clock budget, checked before each interface is launched. The per-read
    // budget bounds ONE read, which left a group of N interfaces costing N x DeviceReadTimeout
    // with nothing timing out: the 4-collection v3 mouse could hold a cycle for 20 s, 20 s in
    // which POLL_SCHEDULED does not fire and the tray still shows the previous reading. One
    // per-read budget for the group means a single slow interface may spend the whole of it and
    // the rest of the group waits for the next tick - which rotation has already made safe,
    // because the next tick starts at a different interface.
    internal static readonly TimeSpan DeviceGroupBudget = TimeSpan.FromSeconds(5);
    // Cheap HID name-set probe while waiting a long battery interval (24h when
    // pct is above the attention floor). Pairing 030D during that wait must not
    // wait until the next battery cycle to appear.
    internal static readonly TimeSpan DeviceSetProbeInterval = TimeSpan.FromSeconds(15);

    // Consecutive -1 ticks before a device is reported as -3, "battery unavailable".
    const int EscalationFailures = 3;


    // Discovery and logging are seams, defaulted to the production article so no call site
    // changes meaning. They exist because Start() runs the real PollLoop, which is the thing
    // worth pinning and also the thing that calls DeviceRegistry.Discover ->
    // HidNative.EnumerateHidPaths: setupapi enumeration followed by HID reads against whatever
    // Apple or Logitech device the machine happens to have attached, and a debug.log line per
    // cycle. A test that drives the loop needs the loop, not the host's hardware.
    //
    // One discover delegate covers the loop: the cycle and WaitForNextCycle's 15 s device-set
    // probe are the only callers and both pass _config.EnableThirdParty, so the bool travels
    // with it rather than being captured.
    //
    // _refresh takes the same sink because RefreshNow -> Signal logs REFRESH_SIGNAL_COALESCED on
    // the CALLER's thread, before the loop is involved, so faking discovery cannot keep a
    // repeated refresh out of the real log.
    internal AdaptivePoller(
        Config config,
        Func<bool, IReadOnlyList<IBatteryDevice>>? discover = null,
        Action<string>? log = null)
    {
        _config = config;
        _discover = discover ?? DeviceRegistry.Discover;
        _log = log ?? Logger.Log;
        _refresh = new RefreshSignal(_log);
    }

    // Reads one device's battery with a hard timeout and exception guard so a single slow or
    // throwing device can't stall the poll loop. On timeout/throw the device is logged (so the
    // culprit is identifiable) and the reading is -1, matching the disconnected sentinel.
    //
    // That is deliberately NOT the sentinel the device layer mints for a read that failed:
    // MouseBatteryDevice.ClassifyReadFailure answers -2 there, because the HID call came back
    // failed and a call that fails is evidence the battery report is not arriving. A read this
    // guard abandoned came back with nothing at all - it is still running, inside a driver that
    // may yet answer - so it has established neither that nor anything else. -2 is the sole
    // input to RepairPlanner's keyboard-gated rule 2d and to the elevated keyboard SDP patch
    // offer, and claiming it on the evidence of a driver that hung would tell the user their
    // pairing record is broken when all that is known is that a read is slow.
    //
    // TimedOut is reported separately from the value because the two failures have opposite
    // costs. A throw came back immediately and left nothing behind. A timeout spent the whole
    // budget AND leaked the worker: Task.Run cannot cancel a blocking HID IOCTL
    // (HidD_GetFeature / HidD_GetInputReport), so the abandoned read keeps a thread-pool thread
    // until the driver releases it, which on a wedged interface is never. BestReading needs to
    // tell the two apart to bound the tick, and WedgeTracker needs it to stop paying for the same
    // wedged interface on every later tick.
    //
    // Took is measured rather than assumed because the group budget is spent against it: a read
    // that threw at once cost nothing and must not be billed a whole budget.
    static GuardedRead ReadBatteryGuarded(IBatteryDevice device, TimeSpan timeout)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            var read = Task.Run(device.GetBatteryPercent);
            if (read.Wait(timeout))
                return new GuardedRead(read.Result, false, Stopwatch.GetElapsedTime(started));

            Logger.Log($"POLL_DEVICE_TIMEOUT device={device.DeviceName} after={timeout} (read abandoned, treated as -1)");
            return new GuardedRead(-1, true, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex)
        {
            Logger.Log($"POLL_DEVICE_ERROR device={device.DeviceName} err={ex.GetBaseException().Message}");
            return new GuardedRead(-1, false, Stopwatch.GetElapsedTime(started));
        }
    }

    // Reads a group's HID collections in the order given and returns the first real percentage,
    // or, when none answers, the better of the two failure sentinels. Discovery returns one
    // device per interface path and no longer collapses a PID's collections; DeviceRegistry
    // .TryClassify gates on a collection only for MagicMouseV3 and for keyboards, so 030D, 0269,
    // 0310 and the trackpads contribute every collection they expose and the group size is
    // unbounded, which is why the scan needs exits and why all three of them are cost bounds.
    //
    // First real answer wins: any percentage in 1-100 is the battery level, so a second live
    // collection could only substitute a different equally-real number at the cost of another
    // read. When two live collections of one device disagree the winner is therefore the first
    // one that answers, in discovery order.
    //
    // First timeout also wins, and this is the exit the motivating failure needs. An interface
    // answering [90 00 00] forever yields -2 or -1, never >= 0, so before this exit the whole
    // group was read on every tick: measured at 3 x budget and 3 permanently parked workers per
    // tick for one wedged 3-interface device. One timeout proves only that THIS interface is not
    // answering, not that the device is not answering - but it has already cost a worker we can
    // never get back, so continuing multiplies both costs by the interface count, forever.
    // Stopping here therefore hides every interface behind a wedged one for the whole of this
    // scan, which is only acceptable because the scan order is not the same twice: ReadDevice
    // rotates it. This method reads exactly the order it is given.
    //
    // The group budget is the third exit and the only one that bounds a group in which nothing
    // goes wrong: N interfaces that each answer slowly but inside their per-read budget used to
    // cost N x that budget. It is checked BEFORE each launch, never after - an interface launched
    // past the deadline costs its whole per-read budget on top of a budget already spent, which
    // is exactly the cost the bound exists to refuse. TimeSpan.MaxValue means unbounded, and the
    // elapsed time is accumulated rather than compared against "now + budget" because that sum
    // overflows on MaxValue and would throw inside the cycle guard.
    //
    // Returning best rather than -1 at either early exit keeps a -2 the group already established.
    internal static int BestReading(IEnumerable<IBatteryDevice> group, TimeSpan perRead) =>
        BestReading(group, perRead, TimeSpan.MaxValue);

    internal static int BestReading(
        IEnumerable<IBatteryDevice> group, TimeSpan perRead, TimeSpan groupBudget,
        ReadLauncher? launcher = null)
    {
        launcher ??= ReadBatteryGuarded;
        int best = -1;
        bool bounded = groupBudget != TimeSpan.MaxValue;
        var spent = TimeSpan.Zero;

        foreach (var device in group)
        {
            if (bounded && spent >= groupBudget) break;

            var read = launcher(device, perRead);
            if (bounded) spent += read.Took;
            if (ScanEndsHere(read, ref best)) return best;
        }
        return best;
    }

    // Ranks one guarded read against the group's running best and says whether the scan ends
    // here. Both scan shapes - BestReading's plain sequence and ReadDevice's rotating index loop -
    // go through this, so the ranking and the exits cannot drift apart between them.
    static bool ScanEndsHere(GuardedRead read, ref int best)
    {
        if (read.Pct >= 0)
        {
            best = read.Pct;
            return true;
        }
        // Only the failure sentinels reach here, so the whole of the ranking is this one
        // line: -2 (the battery report never arrived) outranks -1 (not found, or a report that
        // arrived and is not a level), and a later -1 must not erase a -2 already seen.
        if (read.Pct == -2) best = -2;
        return read.TimedOut;
    }

    // One device's interfaces, read in a rotating order. The scan ends at the first timeout
    // (ScanEndsHere), which bounds the tick but makes the scan order load-bearing: Discover
    // re-derives the same paths in the same index order every tick, so a wedged interface
    // sitting first would hide every interface behind it on every tick, forever - the same "only
    // an interface that never reports survives" outcome DeviceRegistry refuses to cause one
    // layer up. So the start rotates: a scan that got no reading and did not get to see
    // everything moves on by one, which gives every interface its turn within interfaces.Count
    // ticks at a cost, per device per tick, of the group budget plus at most the one per-read
    // budget a launch started just under the deadline can overrun it by. A device that answered
    // keeps the start it answered from, because that start is exactly the one that put a live
    // interface ahead of the wedged one - so the wedge is then never read again, and the leaked
    // worker is paid once rather than on every tick. If that interface later stops answering the
    // start advances again and the others, wedge included, get their turn back.
    //
    // The rotation rule is "no real reading AND the scan was cut short": the loop stopped - on a
    // timeout or on the group budget - with an interface left that it never examined. Those
    // interfaces are reachable by nothing else. The wedge backoff only relieves a path that
    // timed out, and a scan cut by wall clock records no strike at all, so without rotation the
    // interfaces behind a budget cut are never read again. A -1 from a scan that examined
    // everything rotates as well: it learned nothing anywhere, and a different order is the only
    // thing that could change that.
    //
    // A group that examined every interface and ended at -2 keeps its start instead. It has
    // nowhere to rotate TO - any other start reads the same set for the same answer - and moving
    // it costs the one thing -2 is for: it is the sole input to RepairPlanner's keyboard-gated
    // rule 2d and to the elevated keyboard SDP patch offer, and once a wedged interface held the
    // first slot the sentinel would survive only every other tick and the repair offer would
    // flicker with it. A real reading keeps its start for the older reason - that start is
    // exactly the one that produced the reading - even though stopping at the first live
    // interface also leaves the rest of the group unexamined.
    //
    // A skipped path does not make a scan cut short. The skip is per path and holds whatever
    // order the scan uses, so rotating cannot reach it a tick sooner; only the backoff elapsing
    // can. See AnyUnexamined.
    //
    // Rotations counts the times the start wrapped past the end since the last real reading,
    // which is exactly the number of times every interface has had the first slot. ShouldEscalate
    // needs that, not a tick count: a tick count escalates early on a 4-collection device and
    // late on a 2-collection one.
    //
    // A wedged path is skipped BEFORE it is launched, so the interface behind it is reached in
    // the same tick instead of the scan ending on the wedge's timeout. A group whose paths are
    // all skipped therefore launches nothing and reads -1, which is the right answer: the device
    // is present and nothing answered. The tracker is filled from here, because a tracker only
    // the tests fill never skips anything in production, but it is never ticked from here - the
    // backoff is measured in poll cycles, and the cycle owns Tick().
    //
    // scanStart holds each device's next start index, stored already reduced so it cannot grow
    // without bound; the read reduces again because a device's interface count can change
    // between ticks. GroupBy never yields an empty group, so interfaces is never empty.
    internal static int ReadDevice(
        string name, IReadOnlyList<IBatteryDevice> interfaces, TimeSpan perRead,
        TimeSpan groupBudget, Dictionary<string, ScanState> scanStart,
        WedgeTracker? wedges = null, ReadLauncher? launcher = null)
    {
        launcher ??= ReadBatteryGuarded;
        scanStart.TryGetValue(name, out var state);
        int start = state.Start % interfaces.Count;

        int best = -1;
        bool bounded = groupBudget != TimeSpan.MaxValue;
        var spent = TimeSpan.Zero;
        // Whether the scan stopped with an interface left that a later start could read. A local
        // observable at the loop's own exits, because the rotation decision below is the only
        // thing that consumes it - no caller of this method has any use for it.
        bool cutShort = false;

        for (int i = 0; i < interfaces.Count; i++)
        {
            var device = interfaces[(start + i) % interfaces.Count];
            if (wedges is not null && wedges.ShouldSkip(device.DevicePath)) continue;
            if (bounded && spent >= groupBudget)
            {
                // This interface passed the skip check, so it is the budget and not the wedge
                // backoff that left it - and everything behind it - unread.
                cutShort = true;
                break;
            }

            var read = launcher(device, perRead);
            if (bounded) spent += read.Took;
            if (read.TimedOut) wedges?.RecordTimeout(device.DevicePath);
            else wedges?.RecordAnswer(device.DevicePath);

            if (ScanEndsHere(read, ref best))
            {
                cutShort = AnyUnexamined(interfaces, start, i, wedges);
                break;
            }
        }

        if (best < 0 && (best == -1 || cutShort))
        {
            int next = start + 1;
            scanStart[name] = new ScanState(
                next % interfaces.Count,
                next >= interfaces.Count ? state.Rotations + 1 : state.Rotations);
        }
        else
        {
            // A real reading proves the current start works, so the rotation debt is paid and the
            // next outage starts its own count; without that reset a device that fails three
            // ticks, recovers for a week and fails once would escalate on that single tick. The
            // settled -2 that lands here keeps the count it had: it is an answer, but it is not a
            // reading, so it says nothing about whether rotation has yet reached an interface
            // that could produce one.
            scanStart[name] = new ScanState(start, best >= 0 ? 0 : state.Rotations);
        }
        return best;
    }

    // Whether anything the scan stopped short of is an interface a later start could actually
    // read. Skipped paths are not: the skip is keyed by path and applies in any order, so a scan
    // whose only unexamined interfaces are skipped ones has already seen everything rotation
    // could show it, and rotating would cost a settled -2 its stability for nothing. Walked from
    // the stop point rather than counted during the loop because only the exits need the answer,
    // and a group is at most a handful of HID collections.
    static bool AnyUnexamined(
        IReadOnlyList<IBatteryDevice> interfaces, int start, int stoppedAt, WedgeTracker? wedges)
    {
        for (int i = stoppedAt + 1; i < interfaces.Count; i++)
        {
            var device = interfaces[(start + i) % interfaces.Count];
            if (wedges is null || !wedges.ShouldSkip(device.DevicePath)) return true;
        }
        return false;
    }

    // Whether a device's run of failures has earned the -3 "battery unavailable" sentinel.
    //
    // The failure count alone is not enough. Rotation needs up to interfaces.Count ticks to put a
    // live interface first, so a 4-collection device whose live interface sorts last showed the
    // user "battery unavailable" on tick 3 and a real percentage on tick 4. Escalation waits
    // until the rotation has had its full turn, which is what a completed rotation means: every
    // interface has held the first slot at least once and none of them answered. A single-
    // interface device completes a rotation on every failing tick, so nothing is owed to rotation
    // there and the failure count governs alone - the pre-rotation behaviour, unchanged where it
    // was already right.
    internal static bool ShouldEscalate(int consecutiveFailures, int rotationsSinceLastReading) =>
        consecutiveFailures >= EscalationFailures && rotationsSinceLastReading >= 1;

    // True when the Discover DeviceName set differs (order and duplicates ignored).
    internal static bool DeviceSetChanged(IEnumerable<string> oldNames, IEnumerable<string> newNames)
    {
        var oldSet = new HashSet<string>(oldNames, StringComparer.OrdinalIgnoreCase);
        var newSet = new HashSet<string>(newNames, StringComparer.OrdinalIgnoreCase);
        return !oldSet.SetEquals(newSet);
    }

    // Disabled PIDs stay discovered (row + checkbox) but are not HID-read,
    // drain-tracked, or BatteryChanged — including Discover-omit pct=-1.
    // kind is optional because not every caller has one: it is read only to let a legacy
    // vendor-wide enabled_logi=false keep suppressing a Logitech product whose PID is now its
    // own - see Config.IsDeviceEnabled. The poll loop has the kind in hand at both of its call
    // sites, which is the gate that decides whether the device is read at all.
    internal static bool ShouldSkipPid(Config config, string pid, DeviceKind? kind = null) =>
        !config.IsDeviceEnabled(pid, kind);



    // Starts the one poll loop. Idempotent, because the single-writer invariant the cycle state
    // rests on is only "by construction" if a second call cannot construct a second loop: two
    // PollLoops write the failure counts, the last-seen map, the scan state and the wedge
    // tracker at once, and concurrent writes to a Dictionary corrupt the bucket chain - a
    // non-terminating Insert spin or an IndexOutOfRangeException, swallowed by the cycle guard
    // as POLL_CYCLE_ERROR. A second call also overwrote _pollTask, leaving Dispose waiting on
    // one loop while the other ran past tray shutdown. Starting after Dispose is refused for the
    // same reason RefreshNow is: the token is already cancelled, so the loop would exit at once
    // and the only lasting effect would be a _pollTask nobody waits on.
    internal void Start()
    {
        if (_pollTask is not null || _disposed) return;
        _pollTask = PollLoop(_cts.Token);
    }

    // Asks the running loop to poll now instead of waiting out its interval. Safe to call from
    // any thread, and from all six call sites in TrayApp.
    //
    // It signals; it does not start a loop. Starting a second PollLoop while the first was still
    // inside a cycle that can last the whole device budget put two threads into the poller's
    // failure counts, last-seen map and scan state at once, and concurrent writes to a Dictionary
    // corrupt the bucket chain. After tray shutdown there is nothing left to wake, and the signal
    // is deliberately a no-op then rather than an ObjectDisposedException on a UI or repair
    // thread: RefreshSignal holds no disposable state, so losing the race with Dispose costs
    // nothing more than a signal nobody consumes.
    internal void RefreshNow()
    {
        if (_disposed) return;
        _refresh.Signal();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        // Best effort by design: the loop's own cancellation check is what stops it, and the
        // tray is going away either way, so nothing here changes what Dispose does next. The
        // wait can still end badly - a cycle cancelled mid-await surfaces as an
        // AggregateException wrapping OperationCanceledException, and anything the cycle guard
        // could not catch (an OutOfMemoryException, a Dispose racing a faulted loop) arrives the
        // same way - and a shutdown that discarded that silently is how a poll loop stops
        // polling with nothing in the log to say so.
        try { _pollTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex)
        {
            var root = ex.GetBaseException();
            _log($"POLL_DISPOSE_WAIT_FAILED type={root.GetType().Name} err={root.Message}");
        }
        _cts.Dispose();
    }

    async Task PollLoop(CancellationToken ct)
    {
        // Counted before the first await, so Start() has published it by the time it returns and
        // a second loop would be visible immediately rather than one cycle later.
        Interlocked.Increment(ref _loopStarts);

        // Detach from the caller (TrayApp ctor via Start()) so the first cycle runs on a
        // thread-pool thread rather than synchronously up to the first await. This honors
        // the "BatteryChanged is raised from a thread-pool thread" contract and keeps tray startup
        // from blocking on device I/O or re-entering a half-constructed TrayApp.
        await Task.Yield();

        while (!ct.IsCancellationRequested)
        {
            // Default to the last good interval so a faulted cycle still re-polls instead of
            // spinning. The whole cycle body is guarded: PollLoop is an unobserved Task, so any
            // throw here (Discover, a device read, or the BatteryChanged dispatch) would otherwise
            // fault it silently and stop polling forever — the keyboard-never-surfaces stall.
            var interval = LastInterval;
            try
            {
                // One tick per cycle: the wedge backoff is counted in poll cycles, which is why
                // ReadDevice never calls this.
                _wedges.Tick();

                var devices = _discover(_config.EnableThirdParty);

                _livePaths.Clear();
                for (int i = 0; i < devices.Count; i++)
                    _livePaths.Add(devices[i].DevicePath);
                _wedges.Prune(_livePaths);

                int lowestPct = -1;
                string lowestDevice = string.Empty;
                string lowestPid = string.Empty;
                var seenThisCycle = new Dictionary<string, (DeviceKind Kind, string Pid)>(
                    StringComparer.OrdinalIgnoreCase);

                if (devices.Count > 0)
                {
                    // One physical device can expose several HID collections (the v3 Magic Mouse
                    // surfaces a unified path, Col01 pointer, and Col02 vendor battery — all the same
                    // DisplayName). Discover returns one device per path, so raising BatteryChanged
                    // per path lets a non-battery collection's -1/-2 clobber the good Col02 reading
                    // (last write wins in TrayApp's per-name dictionary). ReadDevice collapses each
                    // name to one reading: the first collection that answers with a real percentage,
                    // or, when none does, the better failure sentinel of the collections it got to
                    // read before a timeout or the group budget ended the scan.
                    foreach (var group in devices.GroupBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase))
                    {
                        _groupBuffer.Clear();
                        foreach (var device in group)
                            _groupBuffer.Add(device);

                        var kind = _groupBuffer[0].Kind;
                        var pid = _groupBuffer[0].Pid;
                        seenThisCycle[group.Key] = (kind, pid);
                        if (ShouldSkipPid(_config, pid, kind))
                            continue;

                        int best = ReadDevice(
                            group.Key, _groupBuffer, DeviceReadTimeout, DeviceGroupBudget,
                            _scanStart, _wedges);

                        if (best == -1)
                        {
                            _consecutiveFailures.TryGetValue(group.Key, out int fails);
                            _consecutiveFailures[group.Key] = ++fails;
                            // ReadDevice has already updated this device's scan state for this
                            // tick, so the rotation count read here includes the tick that just
                            // failed — which is what makes "three failures and a completed
                            // rotation" the condition it reads as.
                            _scanStart.TryGetValue(group.Key, out var scan);
                            if (ShouldEscalate(fails, scan.Rotations)) best = -3;
                        }
                        else
                        {
                            _consecutiveFailures[group.Key] = 0;
                        }

                        BatteryChanged?.Invoke(best, group.Key, kind, pid);

                        if (best >= 0)
                        {
                            // Record into drain tracker (skip -2 and -1 failures)
                            DrainRateTracker.Record(group.Key, best);

                            if (lowestPct < 0 || best < lowestPct)
                            {
                                lowestPct = best;
                                lowestDevice = group.Key;
                                lowestPid = pid;
                            }
                        }
                    }
                }

                // Discover only returns currently connected HID. A name seen last poll and
                // missing now is a disconnect: fire pct=-1 so TrayApp Evaluate can open AA
                // death or CloseModal the v3 USB-C alert while other devices remain.
                foreach (var name in BatteryAlertPolicy.NamesOmittedFromDiscover(
                             _lastSeen.Keys, seenThisCycle.Keys))
                {
                    if (!_lastSeen.TryGetValue(name, out var meta)) continue;
                    _consecutiveFailures.Remove(name);
                    _scanStart.Remove(name);
                    if (ShouldSkipPid(_config, meta.Pid, meta.Kind)) continue;
                    BatteryChanged?.Invoke(-1, name, meta.Kind, meta.Pid);
                }
                _lastSeen.Clear();
                foreach (var kv in seenThisCycle)
                    _lastSeen[kv.Key] = kv.Value;

                if (devices.Count == 0)
                    BatteryChanged?.Invoke(-1, string.Empty, DeviceKind.MagicMouseV1, string.Empty);

                // Interval driven by the lowest readable device.
                var lowestIsV3 = devices.FirstOrDefault(d => d.DeviceName == lowestDevice)
                                         ?.Kind == DeviceKind.MagicMouseV3;
                interval = DrainRateTracker.GetNextInterval(
                    lowestDevice, lowestPct, _config.GetThreshold(lowestPid), lowestIsV3);
                LastInterval = interval;

                _log($"POLL_SCHEDULED interfaces={devices.Count} devices={seenThisCycle.Count} lowest_pct={lowestPct} next_in={interval}");
            }
            catch (Exception ex)
            {
                var root = ex.GetBaseException();
                _log($"POLL_CYCLE_ERROR type={root.GetType().Name} err={root.Message}");
            }

            await WaitForNextCycle(interval, ct);
        }
    }

    // Delay the battery interval in ≤15s slices, ending early when RefreshNow signals. If
    // Discover names differ from the last full poll, return immediately so PollLoop runs a full
    // cycle. Cancellation ends the wait quietly — PollLoop's own loop condition stops the loop.
    async Task WaitForNextCycle(TimeSpan interval, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + interval;
        while (!ct.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return;

            var slice = remaining < DeviceSetProbeInterval ? remaining : DeviceSetProbeInterval;
            // The slice IS the refresh wait: a battery interval runs up to 24 h and Refresh Now
            // must not wait for it, so the only way a single loop can stay prompt is to sleep on
            // the signal rather than on a bare delay.
            if (await _refresh.WaitAsync(slice, ct))
                return;

            if (DateTime.UtcNow >= deadline)
                return;

            try
            {
                var probe = _discover(_config.EnableThirdParty);
                var names = new string[probe.Count];
                for (int i = 0; i < probe.Count; i++)
                    names[i] = probe[i].DeviceName;
                if (DeviceSetChanged(_lastSeen.Keys, names))
                {
                    _log($"POLL_DEVICE_SET_CHANGED last_names={_lastSeen.Count} now_paths={names.Length} breaking wait");
                    return;
                }
            }
            catch (Exception ex)
            {
                var root = ex.GetBaseException();
                _log($"POLL_DEVICE_SET_PROBE_ERROR type={root.GetType().Name} err={root.Message}");
            }
        }
    }
}
