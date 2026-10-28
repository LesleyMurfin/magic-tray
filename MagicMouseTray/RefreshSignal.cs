// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// The wake-up AdaptivePoller's single poll loop waits on, so Refresh Now does not have to start a
// second loop to be prompt.
//
// RefreshNow used to swap the poller's CancellationTokenSource and start a whole new PollLoop
// while the previous one was still inside a cycle that can last the full device budget. The two
// loops then raced the poller's failure counts, last-seen map and scan state, and concurrent
// writes to a Dictionary corrupt its bucket chain - a non-terminating Insert spin or an
// IndexOutOfRangeException, swallowed by the cycle guard as POLL_CYCLE_ERROR. One loop woken by a
// signal is the only arrangement in which that state has a single writer.
//
// The signal is remembered until the loop next waits, because a refresh arriving mid-cycle must
// not be lost, and it auto-resets on the wake, because one refresh must buy exactly one extra
// cycle rather than a permanent 0 s poll.
internal sealed class RefreshSignal
{
    // Capacity one, which is what makes repeated signals coalesce: a user clicking Refresh Now
    // three times wants one refresh, not three queued cycles of HID reads on a device that is
    // already being read. The release that finds the slot full is the redundant one.
    //
    // Deliberately never disposed. Nothing here touches AvailableWaitHandle, so there is no
    // unmanaged resource to release, and a disposed semaphore would turn RefreshNow arriving
    // after tray shutdown - a queued menu click, a repair thread finishing - into an
    // ObjectDisposedException on whichever thread got there.
    readonly SemaphoreSlim _slot = new(0, 1);

    internal bool IsSignalled => _slot.CurrentCount > 0;

    internal void Signal()
    {
        try { _slot.Release(); }
        catch (SemaphoreFullException) { }
    }

    // True when a refresh was asked for (and consumes it), false when the timeout expired or the
    // token was cancelled. Cancellation returns quietly rather than throwing: the poll loop's own
    // cancellation check is what stops it, and an OperationCanceledException thrown out of the
    // wait would escape as an unobserved task fault instead.
    internal async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { return await _slot.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
