// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

/// <summary>
/// Blocking work leaves the UI thread, and its answer comes back through one
/// hop - so the caller is never blocked by the work and never reports from
/// the worker.
/// </summary>
/// <remarks>
/// Both halves were missing. ApplyWindowsDeviceState called
/// DeviceEnable.Apply inline and then raised its MessageBox on whatever
/// thread it happened to be on, which was the UI thread only because nothing
/// had moved off it yet; RunDriverActionAsync awaited the driver offers with
/// no Task.Run and no ConfigureAwait, so the continuation after the package
/// download resumed on the WPF dispatcher and ran a fifteen-minute
/// WaitForExit there.
///
/// Fixing only the first half moves the bug: work hopped onto a thread pool
/// thread reports from the thread pool thread, and a MessageBox, a tray-icon
/// repaint or a menu rebuild from there is an exception at best and a
/// cross-thread corruption at worst. So the hop is one seam, used by every
/// caller, and the only place a result is allowed to be reported from: the
/// work runs off the pump, the report runs on it, and neither side can be
/// written the other way round by a caller that forgets.
///
/// marshal is the dispatcher in production (Application.Current.Dispatcher),
/// and a recorder here, so the ordering is a fact a test can read instead of
/// a thread identity it has to guess at.
/// </remarks>
public class UiThreadHandoffTests
{
    // Stands in for the dispatcher: records every hop and marks the window in
    // which a callback is running on the "UI thread".
    sealed class FakeUiThread
    {
        internal int Hops { get; private set; }

        internal bool Inside { get; private set; }

        internal void Marshal(Action action)
        {
            Hops++;
            Inside = true;
            try { action(); }
            finally { Inside = false; }
        }
    }

    static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RunThenReport_RunsTheWorkOffThePumpAndReportsOnIt()
    {
        var ui = new FakeUiThread();
        var order = new List<string>();
        var workOnUiThread = true;
        var reportOnUiThread = false;
        var reported = 0;
        Exception? failure = null;

        await UiDispatch.RunThenReport(
            () =>
            {
                workOnUiThread = ui.Inside;
                order.Add("work");
                return 71;
            },
            value =>
            {
                reported = value;
                reportOnUiThread = ui.Inside;
                order.Add("report");
            },
            ex => failure = ex,
            ui.Marshal);

        Assert.Null(failure);
        Assert.Equal(71, reported);
        Assert.False(workOnUiThread, "the blocking work ran on the UI thread");
        Assert.True(reportOnUiThread, "the result was reported from the worker thread");
        Assert.Equal(new[] { "work", "report" }, order);

        // One hop, for one result: a second Invoke is a second chance to
        // report half an answer.
        Assert.Equal(1, ui.Hops);
    }

    [Fact]
    public async Task RunThenReport_DoesNotBlockTheCallerWhileTheWorkWaits()
    {
        // The defect itself: a click handler that returns only when the
        // elevated step does. The work here does not finish until this test
        // lets it, so a caller that is still blocked is a caller that ran it
        // inline.
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var ui = new FakeUiThread();

        var hop = UiDispatch.RunThenReport(
            () =>
            {
                started.Set();
                release.Wait(Generous);
                return 1;
            },
            _ => { },
            _ => { },
            ui.Marshal);

        Assert.True(started.Wait(Generous), "the work never started");
        Assert.False(hop.IsCompleted, "the caller is blocked for as long as the work takes");
        Assert.Equal(0, ui.Hops);

        release.Set();
        await hop;
        Assert.Equal(1, ui.Hops);
    }

    [Fact]
    public async Task RunThenReport_AThrownFailureComesBackThroughTheSameHop()
    {
        // DeviceEnable.Apply and the Plan*/Execute* guards throw for a request
        // they refuse, and every caller here is fire-and-forget. An exception
        // left on the worker is a silent nothing-happened.
        var ui = new FakeUiThread();
        var reports = 0;
        Exception? failure = null;
        var failureOnUiThread = false;

        var hop = UiDispatch.RunThenReport<int>(
            () => throw new InvalidOperationException("No catalog VID for pid=9999."),
            _ => reports++,
            ex =>
            {
                failure = ex;
                failureOnUiThread = ui.Inside;
            },
            ui.Marshal);

        await hop;

        Assert.False(hop.IsFaulted, "a discarded task hands the caller nothing to observe");
        var refused = Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("No catalog VID for pid=9999.", refused.Message);
        Assert.True(failureOnUiThread, "the failure was reported from the worker thread");
        Assert.Equal(0, reports);
        Assert.Equal(1, ui.Hops);
    }

    [Fact]
    public async Task RunThenReport_ReportsTheResultExactlyOnceAndNeverBoth()
    {
        var ui = new FakeUiThread();
        var results = 0;
        var failures = 0;

        await UiDispatch.RunThenReport(
            () => 7,
            _ => results++,
            _ => failures++,
            ui.Marshal);

        Assert.Equal(1, results);
        Assert.Equal(0, failures);
        Assert.Equal(1, ui.Hops);
    }

    [Fact]
    public async Task RunThenReport_WithAMissingStep_IsRefusedRatherThanLosingTheAnswer()
    {
        var ui = new FakeUiThread();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => UiDispatch.RunThenReport<int>(null!, _ => { }, _ => { }, ui.Marshal));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => UiDispatch.RunThenReport(() => 1, null!, _ => { }, ui.Marshal));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => UiDispatch.RunThenReport(() => 1, _ => { }, null!, ui.Marshal));
        // No marshal is the one that matters most: it would leave the report
        // with nowhere to run but the worker thread.
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => UiDispatch.RunThenReport(() => 1, _ => { }, _ => { }, null!));

        Assert.Equal(0, ui.Hops);
    }

    // The two below are the only tests in this change whose red state is an
    // assertion rather than a missing symbol: revert UiDispatch's
    // no-dispatcher branch to running the callback inline - the exact defect
    // the previous review reported - and the first one fails while everything
    // else in the repository stays green.
    //
    // They need no setup. The test assembly never constructs a WPF
    // Application, so Application.Current is null for the whole run and the
    // branch is reached directly.
    [Fact]
    public void ToUiThread_WithNoDispatcher_DropsTheReportInsteadOfRunningItOnTheWorker()
    {
        // The window between the WPF host shutting down and the process
        // exiting - and this test host. What the hop carries is a modal, a
        // tray-icon repaint or a menu rebuild, so there is no thread left
        // that may legally run it.
        Assert.Null(System.Windows.Application.Current);

        var ran = false;
        UiDispatch.ToUiThread(() => { ran = true; });

        Assert.False(ran, "the report ran on the calling thread instead of being dropped");
    }

    [Fact]
    public void ToUiThread_WithNoDispatcher_RefusesAnAnswerRatherThanGivingOneForTheUser()
    {
        // An answer cannot be dropped the way a report can: returning a
        // default DialogResult would consent on the user's behalf, and
        // raising the window here would put a modal on a worker thread.
        Assert.Null(System.Windows.Application.Current);

        var refused = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = UiDispatch.ToUiThread(() => System.Windows.Forms.DialogResult.OK);
        });
        Assert.Contains("shutting down", refused.Message, StringComparison.Ordinal);
    }
}
