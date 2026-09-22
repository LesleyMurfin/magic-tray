// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

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
    // percent sentinel values: -1=not found/disconnected, -2=present but unreadable, -3=battery unavailable.
    internal event Action<int, string, DeviceKind, string>? BatteryChanged;

    // Last computed interval — readable by TrayApp for tooltip.
    internal TimeSpan LastInterval { get; private set; } = TimeSpan.FromMinutes(5);

    readonly Config _config;
    CancellationTokenSource _cts = new();
    Task? _pollTask;
    readonly Dictionary<string, int> _consecutiveFailures = new();
    readonly Dictionary<string, (DeviceKind Kind, string Pid)> _lastSeen = new(StringComparer.OrdinalIgnoreCase);


    // Per-device read budget. A synchronous HID IOCTL (HidD_GetFeature / HidD_GetInputReport)
    // can block indefinitely on a wedged device, and before this guard one stuck device froze
    // the whole poll loop so POLL_SCHEDULED never fired and no battery ever reached the tray.
    static readonly TimeSpan DeviceReadTimeout = TimeSpan.FromSeconds(5);
    // Cheap HID name-set probe while waiting a long battery interval (24h when
    // pct is above the attention floor). Pairing 030D during that wait must not
    // wait until the next battery cycle to appear.
    internal static readonly TimeSpan DeviceSetProbeInterval = TimeSpan.FromSeconds(15);


    internal AdaptivePoller(Config config) => _config = config;

    // Reads one device's battery with a hard timeout and exception guard so a single slow or
    // throwing device can't stall the poll loop. On timeout/throw the device is logged (so the
    // culprit is identifiable) and treated as unreadable (-1), matching the disconnected sentinel.
    static int ReadBatteryGuarded(IBatteryDevice device, TimeSpan timeout)
    {
        try
        {
            var read = Task.Run(device.GetBatteryPercent);
            if (read.Wait(timeout))
                return read.Result;

            Logger.Log($"POLL_DEVICE_TIMEOUT device={device.DeviceName} after={timeout} (read abandoned, treated as -1)");
            return -1;
        }
        catch (Exception ex)
        {
            Logger.Log($"POLL_DEVICE_ERROR device={device.DeviceName} err={ex.GetBaseException().Message}");
            return -1;
        }
    }

    // Ranks a battery reading when collapsing a device's multiple HID collections to one:
    // a real percentage (0-100) beats -2 (present but unreadable) beats -1 (not found).
    static int ReadingRank(int pct) => pct >= 0 ? pct + 2 : (pct == -2 ? 1 : 0);

    // The collapse comparison, with one tie-break added and nothing else
    // changed: among readings of EQUAL rank, one carrying the zero-report fact
    // wins.
    //
    // Why the tie needs breaking at all. All four -2 producers rank identically
    // at 1 - the v3 truncation (MouseBatteryDevice.cs:183-195), a wrong report
    // id (:201-203), a failed IOCTL (:214-216) and the v1/v2
    // MOUSE_UNIFIED_BLOCKED (:353) - the old comparison was strictly `>`, and
    // DeviceRegistry.Discover does not enumerate collections in any defined
    // order. So on the exact device shape the battery rule targets (COL01
    // failing while COL02 truncates) which -2 survived, and therefore which
    // probe record travelled with it, was decided by enumeration order.
    //
    // Every other ordering is untouched, so the rationale at :155-160 stands: a
    // real percentage still beats -2, which still beats -1, and this can only
    // choose between readings the old rule considered equal.
    internal static bool PrefersReading(
        int candidatePct, bool? candidateZeroReport, int bestPct, bool? bestZeroReport)
    {
        int candidate = ReadingRank(candidatePct);
        int best = ReadingRank(bestPct);
        if (candidate != best)
            return candidate > best;

        // Only true breaks the tie. null (nothing judgeable came back) must
        // never outrank a reading that actually answered.
        return candidateZeroReport == true && bestZeroReport != true;
    }

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

    // Cancels the current wait and polls immediately. Safe to call from any thread.
    internal void RefreshNow()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
        _pollTask = PollLoop(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _pollTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _cts.Dispose();
    }

    async Task PollLoop(CancellationToken ct)
    {
        // Detach from the caller (TrayApp ctor via Start(), or RefreshNow()) so the first cycle
        // runs on a thread-pool thread rather than synchronously up to the first await. This honors
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
                var devices = DeviceRegistry.Discover(_config.EnableThirdParty);

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
                    // (last write wins in TrayApp's per-name dictionary). Collapse to the best read
                    // per device name: a real percentage beats -2 (present, unreadable) beats -1.
                    foreach (var group in devices.GroupBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase))
                    {
                        var kind = group.First().Kind;
                        var pid = group.First().Pid;
                        seenThisCycle[group.Key] = (kind, pid);
                        if (ShouldSkipPid(_config, pid))
                            continue;

                        int best = -1;

                        // The probe travels WITH the winning reading rather
                        // than beside it: the fact and the percent have to come
                        // from the same HID collection, or COL01's failed open
                        // supplies the fact while COL02 supplies the number.
                        BatteryProbe? bestProbe = null;
                        foreach (var device in group)
                        {
                            int pct = ReadBatteryGuarded(device, DeviceReadTimeout);
                            var probe = (device as MouseBatteryDevice)?.LastProbe;
                            if (!PrefersReading(pct, probe?.ZeroReport, best, bestProbe?.ZeroReport))
                                continue;

                            best = pct;
                            bestProbe = probe;
                        }

                        // Publish only the survivor. A group with no v3 probe at
                        // all publishes nothing, which leaves the snapshot's
                        // field null - no evidence, and no evidence raises
                        // nothing.
                        if (bestProbe is not null)
                            DeviceDiagReader.PublishBatteryProbe(pid, bestProbe);

                        if (best == -1)
                        {
                            _consecutiveFailures.TryGetValue(group.Key, out int fails);
                            _consecutiveFailures[group.Key] = ++fails;
                            if (fails >= 3) best = -3;
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

                Logger.Log($"POLL_SCHEDULED devices={devices.Count} lowest_pct={lowestPct} next_in={interval}");
            }
            catch (Exception ex)
            {
                var root = ex.GetBaseException();
                Logger.Log($"POLL_CYCLE_ERROR type={root.GetType().Name} err={root.Message}");
            }

            try { await WaitForNextCycle(interval, ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    // Delay the battery interval in ≤15s slices. If Discover names differ from
    // the last full poll, return immediately so PollLoop runs a full cycle.
    async Task WaitForNextCycle(TimeSpan interval, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + interval;
        while (!ct.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return;

            var slice = remaining < DeviceSetProbeInterval ? remaining : DeviceSetProbeInterval;
            await Task.Delay(slice, ct);

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
                    Logger.Log($"POLL_DEVICE_SET_CHANGED last={_lastSeen.Count} now={names.Length} breaking wait");
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
