// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// Per-interface memory of reads that never answered, so the poll loop stops paying for a wedged
// interface on every tick.
//
// A timeout is not a free failure. Task.Run cannot cancel a blocking HID IOCTL
// (HidD_GetFeature / HidD_GetInputReport), so the abandoned read keeps a thread-pool worker until
// the driver releases it, which on a wedged interface is never. Every scan comes back to a wedged
// interface: AdaptivePoller.ReadDevice rotates a scan that learned nothing and was cut short, so
// a never-answering group reaches each of its wedged interfaces in turn, forever - and a group
// that settled on -2 with nothing left unexamined keeps a start that reads its wedge on every
// single tick. Either way, without this memory the tray leaks one worker per wedged interface
// per tick for the life of the process.
//
// Keyed by DevicePath, because that is what a wedge belongs to: DeviceName covers a whole group,
// and the other collections of the same mouse are exactly what the rotation is trying to reach.
//
// Owned by the single poll loop (see AdaptivePoller's cycle state), so no locking.
internal sealed class WedgeTracker
{
    // Consecutive timeouts on one path before it is skipped. Below this the path is still read:
    // one or two timeouts can be a device that is asleep or mid-reconnect, and refusing to read it
    // then would be the "only an interface that never reports survives" outcome in reverse.
    internal static readonly int WedgeTimeoutStrikes = 3;

    // How many poll cycles a skipped path stays skipped. The skip is a backoff, not a blacklist:
    // a wedged interface is routinely one that was asleep, and a permanent skip would make a
    // reboot the only way to read it again. It must be longer than one cycle, or the backoff buys
    // nothing - the next tick's rotation would hand the wedge the first slot straight back.
    internal static readonly int WedgeBackoffTicks = 4;

    sealed class PathState
    {
        internal int Strikes;
        internal long SkippedAtTick;
    }

    readonly Dictionary<string, PathState> _paths = new(StringComparer.OrdinalIgnoreCase);
    // Scratch for Prune, refilled rather than reallocated; the tracker has one writer.
    readonly List<string> _dropped = new();
    long _tick;

    // One call per poll cycle. This advances the backoff clock and nothing else: strikes count
    // READS, not cycles, so a cycle neither adds nor clears one.
    internal void Tick() => _tick++;

    // A read that was abandoned at its budget. Recorded by the read path itself, because a
    // tracker that only the tests fill never skips anything in production.
    internal void RecordTimeout(string devicePath)
    {
        if (!_paths.TryGetValue(devicePath, out var state))
            _paths[devicePath] = state = new PathState();
        else if (state.Strikes >= WedgeTimeoutStrikes && !IsSkipped(state))
            // The backoff already elapsed, so this path has had its retry and this timeout opens
            // a new outage rather than extending the old one. Without the reset the first retry
            // after any backoff would re-skip the path on a single timeout, forever.
            state.Strikes = 0;

        state.Strikes++;
        if (state.Strikes == WedgeTimeoutStrikes)
            state.SkippedAtTick = _tick;
    }

    // An interface that answered is not wedged, whatever it did before: the strikes count
    // CONSECUTIVE timeouts, so one answer clears them and the path owes the full count again.
    // Otherwise a flaky interface accumulates strikes across hours of successful reads and is
    // eventually skipped while it is working.
    internal void RecordAnswer(string devicePath) => _paths.Remove(devicePath);

    // True while the path is inside its backoff window. No Tick() is needed first: the skip is
    // armed by the strike that reached the count, so the very next scan already avoids the path.
    internal bool ShouldSkip(string devicePath) =>
        _paths.TryGetValue(devicePath, out var state) && IsSkipped(state);

    // Drops every path that is not in the live set. Discovery re-derives paths each cycle and a
    // USB-C reconnect changes them, so the tracker is keyed by something that disappears: pruning
    // is what keeps it from growing for the life of the process, and a path that comes back is a
    // different physical state that has not earned a skip.
    internal void Prune(IEnumerable<string> livePaths)
    {
        if (_paths.Count == 0) return;

        var live = new HashSet<string>(livePaths, StringComparer.OrdinalIgnoreCase);
        _dropped.Clear();
        foreach (var path in _paths.Keys)
            if (!live.Contains(path))
                _dropped.Add(path);

        foreach (var path in _dropped)
            _paths.Remove(path);
    }

    bool IsSkipped(PathState state) =>
        state.Strikes >= WedgeTimeoutStrikes && _tick - state.SkippedAtTick < WedgeBackoffTicks;
}
