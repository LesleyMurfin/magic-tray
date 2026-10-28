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
//
// "Blocking" is the whole of the claim. Two places post work to the pump and
// walk away instead - TrayApp.OfferStaleModeARestore and
// TrayApp.PublishConfigFacts, both dispatcher.InvokeAsync - and they are not
// this seam because this seam blocks: each says so at its own site.
internal static class UiDispatch
{
    // The production marshal - this is what is handed to RunThenReport below,
    // and what every dialog raised from a worker goes through.
    //
    // Dispatcher.Invoke from the dispatcher's own thread runs the callback
    // inline, so a report that is already on the UI thread costs nothing.
    internal static void ToUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // The window between the WPF host shutting down and the process
            // exiting (and any test host). Every caller that reaches here is
            // a worker - RunThenReport hands this its report after Task.Run,
            // and RunDriverActionAsync calls it after awaiting one - and what
            // it carries is a modal, a tray icon repaint or a menu rebuild,
            // none of which may be touched from the thread pool. There is no
            // thread left that may legally run it, so it is dropped the way
            // TrayApp.ReportModeFlip drops its own report in the same state.
            Logger.Log("UI_HOP_SKIPPED reason=no-dispatcher");
            return;
        }
        try
        {
            dispatcher.Invoke(action);
        }
        catch (InvalidOperationException ex)
            when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            // Only the refusal. A dispatcher that is shutting down will not
            // take the hop, and the window the report needed is going away
            // with it. Anything else the callback throws is a bug in the
            // report itself and belongs to App's DispatcherUnhandledException,
            // which logs it and marks it handled (App.xaml.cs:40-44) - the
            // same one line and the same stale menu, but written where report
            // bugs are handled rather than swallowed by this seam.
            Logger.Log($"UI_HOP_FAIL err={ex.Message}");
        }
    }

    // Same hop for the answers a worker has to wait for - a consent dialog is
    // a window, and a window belongs on the thread that runs the pump. With
    // no dispatcher nothing is swallowed: a caller that cannot ask the user
    // must not carry on as though it had. Once the hop is taken, an exception
    // out of the callback is App's to handle, exactly as above.
    internal static T ToUiThread<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // The Action overload above drops its work, because a report
            // nobody can see is a lost report and nothing else. An answer
            // cannot be dropped: raising the window here would put a modal on
            // a thread-pool thread, and returning a default would consent on
            // the user's behalf. So the caller is told it cannot ask.
            Logger.Log("UI_HOP_REFUSED reason=no-dispatcher");
            throw new InvalidOperationException(
                "Magic Tray is shutting down, so it cannot ask this question.");
        }
        try
        {
            return dispatcher.Invoke(func);
        }
        catch (InvalidOperationException ex)
            when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            // The same state as the branch above, reached a few instructions
            // later: the dispatcher began shutting down between the null
            // check and the Invoke. The caller is told the same way - the
            // throw is rethrown, not converted into an answer - but it is
            // logged under its own reason, because a refusal this seam chose
            // and a race it lost read identically in the log otherwise.
            Logger.Log($"UI_HOP_REFUSED reason=shutdown-race err={ex.Message}");
            throw;
        }
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

        // Before the hop, not inside it: the hop can be dropped (no
        // dispatcher) or refused (one that is shutting down), and a refusal
        // raised before the work launched - DeviceEnable.Apply's guards - has
        // been written down nowhere else yet. Logging it here is what makes
        // "the report is lost, not the record" true.
        if (outcome.Failure is { } carried)
            Logger.Log($"UI_HOP_CARRIES_FAILURE err={carried.Message}");

        marshal(() =>
        {
            if (outcome.Failure is { } failure)
                failed(failure);
            else
                report(outcome.Value);
        });
    }
}
