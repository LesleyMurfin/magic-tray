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
// A v3 Magic Mouse that cannot expose Input 0x90 on COL02 returns pct=-2.
// Never Feature 0x47, never WMI Hands-Free. The tray never flips LowerFilters.
internal sealed class AdaptivePoller : IDisposable
{
    // Fired once per discovered device per poll cycle.
    // percent sentinel values: -1=not found/disconnected/read did not answer, -2=present but the
    // battery report is not exposed, -3=battery unavailable (three consecutive -1s, minted below).
    internal event Action<int, string, DeviceKind, string>? BatteryChanged;

    // Last computed interval — readable by TrayApp for tooltip.
    internal TimeSpan LastInterval { get; private set; } = TimeSpan.FromMinutes(5);

    readonly Config _config;
    // One source for the poller's lifetime, so Dispose cannot race a reader of the field. The
    // only thing that used to replace it was RefreshNow, which now signals instead.
    readonly CancellationTokenSource _cts = new();
    Task? _pollTask;
    readonly RefreshSignal _refresh = new();
    volatile bool _disposed;
    int _loopStarts;

    // Cycle state. Every one of these is read-modify-written by the poll loop and by nothing
    // else: there is exactly ONE PollLoop for a poller's lifetime (Start creates it; RefreshNow
    // only wakes it), so this state is single-writer by construction. That invariant is what the
    // whole arrangement rests on - two loops racing these dictionaries corrupt the bucket chain,
    // which is a non-terminating Insert spin or an IndexOutOfRangeException, swallowed by the
    // cycle guard as POLL_CYCLE_ERROR. Anything added here must stay inside the loop.
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


    internal AdaptivePoller(Config config) => _config = config;

    // Reads one device's battery with a hard timeout and exception guard so a single slow or
    // throwing device can't stall the poll loop. On timeout/throw the device is logged (so the
    // culprit is identifiable) and the reading is -1, matching the disconnected sentinel: -2
    // means the battery report is not exposed, which a read that never answered has not
    // established, and -2 is what buys the elevated keyboard SDP patch offer.
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
        // line: -2 (present, report not exposed) outranks -1 (not found), and a later -1
        // must not erase a -2 already seen in the group.
        if (read.Pct == -2) best = -2;
        return read.TimedOut;
    }

    // One device's interfaces, read in a rotating order. BestReading ends a scan at the first
    // timeout, which bounds the tick but makes the scan order load-bearing: Discover re-derives
    // the same paths in the same index order every tick, so a wedged interface sitting first
    // would hide every interface behind it on every tick, forever - the same "only an interface
    // that never reports survives" outcome DeviceRegistry refuses to cause one layer up. So the
    // start rotates: a device that did not answer moves on by one, which gives every interface
    // its turn within interfaces.Count ticks at a cost, per device per tick, of the group budget
    // plus at most the one per-read budget a launch started just under the deadline can overrun
    // it by. A device that answered keeps the start it answered from, because that start is
    // exactly the one that put a live interface ahead of the wedged one - so the wedge is then
    // never read again, and the leaked worker is paid once rather than on every tick. If that
    // interface later stops answering the start advances again and the others, wedge included,
    // get their turn back.
    //
    // Only -1 rotates. -2 is not "learned nothing": it says the interface answered and the
    // battery report is not exposed, and it is the only input to RepairPlanner rule 2d and to the
    // elevated keyboard SDP patch offer. Rotating on it would hand the next tick's first slot to
    // a wedged interface whose timeout ends that scan at -1 before the -2 interface is reached,
    // so the sentinel would survive only every other tick and the repair offer would flicker.
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

        for (int i = 0; i < interfaces.Count; i++)
        {
            var device = interfaces[(start + i) % interfaces.Count];
            if (wedges is not null && wedges.ShouldSkip(device.DevicePath)) continue;
            if (bounded && spent >= groupBudget) break;

            var read = launcher(device, perRead);
            if (bounded) spent += read.Took;
            if (read.TimedOut) wedges?.RecordTimeout(device.DevicePath);
            else wedges?.RecordAnswer(device.DevicePath);

            if (ScanEndsHere(read, ref best)) break;
        }

        if (best == -1)
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
            // ticks, recovers for a week and fails once would escalate on that single tick. A -2
            // keeps the count it had: it is an answer, but it is not a reading, so it says nothing
            // about whether rotation has yet reached an interface that could produce one.
            scanStart[name] = new ScanState(start, best >= 0 ? 0 : state.Rotations);
        }
        return best;
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
    internal static bool ShouldSkipPid(Config config, string pid) =>
        !config.IsDeviceEnabled(pid);



    internal void Start() => _pollTask = PollLoop(_cts.Token);

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
        try { _pollTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
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

                var devices = DeviceRegistry.Discover(_config.EnableThirdParty);

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
                        if (ShouldSkipPid(_config, pid))
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
                    if (ShouldSkipPid(_config, meta.Pid)) continue;
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

                Logger.Log($"POLL_SCHEDULED interfaces={devices.Count} devices={seenThisCycle.Count} lowest_pct={lowestPct} next_in={interval}");
            }
            catch (Exception ex)
            {
                var root = ex.GetBaseException();
                Logger.Log($"POLL_CYCLE_ERROR type={root.GetType().Name} err={root.Message}");
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
                var probe = DeviceRegistry.Discover(_config.EnableThirdParty);
                var names = new string[probe.Count];
                for (int i = 0; i < probe.Count; i++)
                    names[i] = probe[i].DeviceName;
                if (DeviceSetChanged(_lastSeen.Keys, names))
                {
                    Logger.Log($"POLL_DEVICE_SET_CHANGED last_names={_lastSeen.Count} now_paths={names.Length} breaking wait");
                    return;
                }
            }
            catch (Exception ex)
            {
                var root = ex.GetBaseException();
                Logger.Log($"POLL_DEVICE_SET_PROBE_ERROR type={root.GetType().Name} err={root.Message}");
            }
        }
    }
}
