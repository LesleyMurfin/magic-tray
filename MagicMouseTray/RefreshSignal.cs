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
    // already being read. The signal that finds the slot full - whether Signal's own check sees
    // it or the Release that raced it does - is the redundant one.
    //
    // Deliberately never disposed. Nothing here touches AvailableWaitHandle, so there is no
    // unmanaged resource to release, and a disposed semaphore would turn RefreshNow arriving
    // after tray shutdown - a queued menu click, a repair thread finishing - into an
    // ObjectDisposedException on whichever thread got there.
    readonly SemaphoreSlim _slot = new(0, 1);

    internal bool IsSignalled => _slot.CurrentCount > 0;

    // Arms the next wait, or collapses into the signal already waiting to be consumed. The
    // CurrentCount check is the coalescing on the common path - a full slot is a refresh nobody
    // has taken yet, so the second click is already represented and there is nothing to add -
    // and it keeps a user drumming on Refresh Now out of the exception path entirely. It cannot
    // lose a wake: it skips the Release only while a permit is sitting in the slot, and a Signal
    // arriving after a waiter consumed its permit finds the slot empty and arms the next wait.
    //
    // The catch covers the one case the check cannot, two threads both seeing an empty slot and
    // both releasing - a menu click and a repair thread finishing at the same moment. That is
    // the same redundancy, found a few nanoseconds later, so it is logged and dropped rather
    // than thrown: an escaping SemaphoreFullException on a UI or repair thread would take down
    // whatever called RefreshNow, over a refresh that was already queued.
    internal void Signal()
    {
        if (_slot.CurrentCount > 0)
        {
            Logger.Log("REFRESH_SIGNAL_COALESCED reason=already_signalled");
            return;
        }

        try { _slot.Release(); }
        catch (SemaphoreFullException)
        {
            Logger.Log("REFRESH_SIGNAL_COALESCED reason=raced_a_concurrent_signal");
        }
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
