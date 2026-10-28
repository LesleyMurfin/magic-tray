// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// The one way a blocking tray command leaves the UI thread, and the one way
// its answer comes back.
//
// The tray used to run its elevated work straight out of the click handler:
// DeviceEnable.Apply polls its status sidecar for up to two minutes,
// DriverInstaller.RunElevated waits on an elevated process for five, and the
// 0323 driver offers pull a package and then wait fifteen more. For the whole
// of each of those windows the message pump is dead - no menu, no icon
// repaint, no battery poll, no critical alert - and Windows marks the process
// Not Responding.
//
// Moving the work to a worker thread and leaving the reporting where it fell
// would only move the bug: a MessageBox, a NotifyIcon repaint or a menu
// rebuild from the thread pool is an exception at best and a cross-thread
// corruption at worst. So both halves are this one seam: the work runs off
// the pump, the result is reported on it, and a caller cannot write it the
// other way round by forgetting.
internal static class UiDispatch
{
    // The production marshal - this is what is handed to RunThenReport below,
    // and what every dialog raised from a worker goes through.
    //
    // Dispatcher.Invoke from the dispatcher's own thread runs the callback
    // inline, so a report that is already on the UI thread costs nothing. A
    // null Application is the window between the WPF host shutting down and
    // the process exiting (and any test host): there is no other thread left
    // to hand the work to, so it runs here rather than being dropped.
    internal static void ToUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }
        try
        {
            dispatcher.Invoke(action);
        }
        catch (Exception ex)
        {
            // A dispatcher that is shutting down refuses the hop. Every
            // outcome this carries has already been written to the log by the
            // work that produced it, so the report is lost, not the record.
            Logger.Log($"UI_HOP_FAIL err={ex.Message}");
        }
    }

    // Same hop for the answers a worker has to wait for - a consent dialog is
    // a window, and a window belongs on the thread that runs the pump. Nothing
    // is swallowed here: a caller that cannot ask the user must not carry on
    // as though it had.
    internal static T ToUiThread<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher is null ? func() : dispatcher.Invoke(func);
    }

    /// <summary>
    /// Runs <paramref name="work"/> off the UI thread and reports its result
    /// through exactly one <paramref name="marshal"/> hop - the result if it
    /// returned, the exception if it threw, never both and never neither.
    /// </summary>
    /// <remarks>
    /// The returned task completes when the report has been made, so a caller
    /// may await it; every tray click discards it, which is why a thrown
    /// failure is handed to <paramref name="failed"/> instead of being left on
    /// the task for nobody to observe. marshal is
    /// <see cref="ToUiThread(Action)"/> in production.
    /// </remarks>
    internal static async Task RunThenReport<T>(
        Func<T> work,
        Action<T> report,
        Action<Exception> failed,
        Action<Action> marshal)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(failed);
        // Last, and the one that matters most: with no marshal there is
        // nowhere for the report to run but the worker thread.
        ArgumentNullException.ThrowIfNull(marshal);

        // ConfigureAwait(false): this continuation is the hop's own
        // bookkeeping, and resuming it on the dispatcher would put the one
        // thread this seam exists to keep free back in the queue.
        var outcome = await Task.Run<(T Value, Exception? Failure)>(() =>
        {
            try { return (work(), null); }
            catch (Exception ex) { return (default(T)!, ex); }
        }).ConfigureAwait(false);

        marshal(() =>
        {
            if (outcome.Failure is { } failure)
                failed(failure);
            else
                report(outcome.Value);
        });
    }
}
