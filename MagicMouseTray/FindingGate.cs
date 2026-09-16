// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// Persistence gate between RepairPlanner.Plan and everything the user can see:
// the menu row, the per-device sub-row, the toasts and the guided repair
// dialogs. A fault that exists for 900 ms is not a fault.
//
// Why this exists, measured on the reference PC: a driver rebuild/reinstall
// walks the BTHENUM stack through states that are byte-identical to four real
// faults the planner already reports -
//   the filter service is briefly Stopped        -> FilterStoppedButBound
//   the filter is briefly out of Device_Stack    -> FilterNotInStack
//   an old and a new filter name are both bound  -> ConflictingFilters
//   the HID children are briefly torn down       -> PointerChildMissing
// Every one of those would toast Lesley mid-install and offer an elevated
// pnputil /restart-device that fights the install still in flight. None of them
// is wrong about what the registry said; they are wrong about how long it said
// it. That is what this file adds, and the only thing it adds: it does NOT
// filter by problem type, because persistence is not severity - a transient
// ConflictingFilters is as false as a transient PointerChildMissing, and a
// standing one of either is as true.
//
// Pure and clock-injected in the shape DeviceDiagReader already established for
// its own debounce (BaselineMinAge / AliveMemory, decision lifted out of the
// registry into an nowUtc-taking method so tests can drive it): no registry, no
// Windows types, no DateTime.UtcNow inside. The clock comes from the caller.
//
// Deliberately an instance rather than DeviceDiagReader's static state: TrayApp
// owns exactly one, and per-instance state is what lets the tests isolate by
// constructing their own gate instead of leaning on unique keys. Nothing here
// must survive a process restart - a fault that is still there will re-confirm
// within one hold window of the first refresh.
internal sealed class FindingGate
{
    // How long a (Pid, Problem) pair must keep showing up before it is allowed
    // to reach the user.
    //
    // Floor - it must outlast the churn of a legitimate driver change. The
    // repo's own repair path already states that ceiling twice: the embedded
    // post-restart script polls for the filter to come back for up to 10 s
    // (DeviceRepair.cs:195, :472), and diagnose-and-recover.ps1:700 sleeps 3 s
    // after pnputil /restart-device before it re-reads the stack. So ~13 s is
    // the longest window in which this PC is known to look broken while being
    // repaired.
    //
    // Ceiling - it must not delay a real fault past the user's patience. A
    // standing fault is re-observed on every menu open and every poll tick, so
    // 15 s costs the user one extra glance at the menu and nothing else.
    //
    // 15 s clears the 13 s of measured churn with margin and stays inside one
    // interaction. Shorter and a slow install leaks a toast; much longer and a
    // genuinely dead wheel sits unreported while the user is looking at it.
    internal static readonly TimeSpan HoldWindow = TimeSpan.FromSeconds(15);

    // Elapsed time alone is not enough. Refresh cadence here is bursty AND
    // irregular: menu Opening, a poll tick and AfterFindingHandled can all land
    // inside the same second, while the poll interval itself has been observed
    // live at a full day (POLL_SCHEDULED next_in=1.00:00:00). So one very old
    // observation can satisfy any elapsed test on its own, and it must not - a
    // single sample cannot distinguish a standing fault from the instant a
    // device stack was being torn down. Confirmation therefore needs BOTH at
    // least this many separate observations AND HoldWindow of elapsed time
    // between the first and the latest one.
    internal const int MinObservations = 2;

    // What the gate hands back. Pending is not a debug extra: it is what keeps
    // an explicit user-initiated check from answering "No problems found" while
    // a fault is mid-confirmation, and it is what the REPAIR_FINDINGS log line
    // reports so a suppressed transient is auditable instead of swallowed.
    internal readonly record struct GateResult(
        IReadOnlyList<RepairFinding> Confirmed,
        IReadOnlyList<RepairFinding> Pending);

    // Identity of a tracked fault. PID casing is not stable across the readers
    // (the registry hands back both 0323 and 0323 upper/lower on the same PC),
    // so it is compared case-insensitively, exactly like TrayApp's toast keys.
    readonly record struct Pair(string Pid, RepairProblem Problem);

    // Struct key + explicit comparer rather than a "{pid}:{problem}" string:
    // Confirm runs on the UI thread on every menu open, and this way a refresh
    // allocates nothing at all in the common "nothing changed" case.
    sealed class PairComparer : IEqualityComparer<Pair>
    {
        internal static readonly PairComparer Instance = new();

        public bool Equals(Pair a, Pair b) =>
            a.Problem == b.Problem
            && string.Equals(a.Pid, b.Pid, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(Pair p) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(p.Pid), (int)p.Problem);
    }

    // Sweep is the id of the Confirm call that last saw this pair. It does two
    // jobs: it forgets a pair that stopped appearing (below), and it stops a
    // duplicated pair inside one raw list from counting as two observations.
    readonly record struct Track(DateTime FirstSeenUtc, int Observations, bool Confirmed, long Sweep);

    readonly Dictionary<Pair, Track> _tracks = new(PairComparer.Instance);
    // Reused across calls so the forget pass does not allocate a list per
    // refresh. Never read outside Confirm.
    readonly List<Pair> _forget = new();
    long _sweep;

    /// <summary>
    /// Folds one raw planner result into the gate and returns the findings that
    /// have earned the right to be shown, in the order the planner produced
    /// them, plus the ones still being confirmed.
    /// </summary>
    internal GateResult Confirm(IReadOnlyList<RepairFinding> raw, DateTime nowUtc)
    {
        _sweep++;
        var confirmedCount = 0;

        for (var i = 0; i < raw.Count; i++)
        {
            var key = new Pair(raw[i].Pid, raw[i].Problem);
            if (_tracks.TryGetValue(key, out var track))
            {
                // Already folded in this same call (a duplicated pair): one
                // refresh is one observation, however many rows carry it.
                if (track.Sweep != _sweep)
                {
                    track = track with
                    {
                        Observations = track.Observations + 1,
                        Sweep = _sweep
                    };
                    // Once confirmed it stays confirmed for as long as it keeps
                    // appearing, so the menu row does not flicker between the
                    // fault and "No problems found" from one open to the next.
                    if (!track.Confirmed
                        && track.Observations >= MinObservations
                        && nowUtc - track.FirstSeenUtc >= HoldWindow)
                    {
                        track = track with { Confirmed = true };
                    }
                    _tracks[key] = track;
                }
            }
            else
            {
                track = new Track(nowUtc, 1, false, _sweep);
                _tracks[key] = track;
            }

            if (track.Confirmed)
                confirmedCount++;
        }

        // A pair absent from this raw list is forgotten outright - no decay, no
        // grace. That is the whole transient defence: during a driver install
        // each of those false faults appears, vanishes when the next PnP step
        // lands, and so never accumulates credit across the interruption. The
        // price is that a fault which genuinely flaps in and out never
        // confirms, which is correct - an intermittent stack is not something
        // an elevated restart-device fixes.
        foreach (var pair in _tracks)
        {
            if (pair.Value.Sweep != _sweep)
                _forget.Add(pair.Key);
        }
        if (_forget.Count > 0)
        {
            for (var i = 0; i < _forget.Count; i++)
                _tracks.Remove(_forget[i]);
            _forget.Clear();
        }

        // The three cheap cases cover every real refresh: nothing found,
        // nothing confirmed yet, or a steady state where everything found is
        // already confirmed. Only a mixed result builds lists.
        if (raw.Count == 0)
            return new GateResult(Array.Empty<RepairFinding>(), Array.Empty<RepairFinding>());
        if (confirmedCount == 0)
            return new GateResult(Array.Empty<RepairFinding>(), raw);
        if (confirmedCount == raw.Count)
            return new GateResult(raw, Array.Empty<RepairFinding>());

        var confirmed = new List<RepairFinding>(confirmedCount);
        var pending = new List<RepairFinding>(raw.Count - confirmedCount);
        for (var i = 0; i < raw.Count; i++)
        {
            var key = new Pair(raw[i].Pid, raw[i].Problem);
            if (_tracks.TryGetValue(key, out var track) && track.Confirmed)
                confirmed.Add(raw[i]);
            else
                pending.Add(raw[i]);
        }
        return new GateResult(confirmed, pending);
    }
}
