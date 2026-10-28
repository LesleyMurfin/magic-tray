// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// What a second "Check for problems now" during a live ten-second leg gets.
//
// The hazard is not a duplicate dialog. Raw-input registration is one
// hwndTarget per usage per PROCESS, so two probes share one registration and
// the first to unwind deregisters the other, which then measures a scrolling
// mouse as silent and reports a dead wheel on healthy hardware. Everything
// asserted here is what the caller can see: whether it was given a run, and
// whether a later request is given one.
public class ScrollProbeGateTests
{
    // A run the test finishes by hand, so "in flight" is a real state and not a
    // race against a timer.
    static (Func<Task> Start, TaskCompletionSource Finish) Blocking()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (() => tcs.Task, tcs);
    }

    [Fact]
    public async Task SecondProbeWhileOneIsInFlight_IsRefused()
    {
        var gate = new ScrollProbeGate();
        var (start, finish) = Blocking();

        var first = gate.TryStart(start);
        var second = gate.TryStart(() => Task.CompletedTask);

        Assert.NotNull(first);
        // Refused, not queued and not silently run alongside: a second
        // registration is what makes the first probe accuse a healthy mouse.
        Assert.Null(second);
        Assert.True(gate.Busy);

        // And refusal has something to say. A menu item that visibly does
        // nothing is how the user learns to click it again.
        Assert.Contains("still running", RepairPlanner.ScrollProbeAlreadyRunning);
        Assert.Contains(
            "Only one scroll test can run at a time", RepairPlanner.ScrollProbeAlreadyRunning);

        finish.SetResult();
        await first;
    }

    [Fact]
    public async Task GuardIsReleasedOnceTheProbeFinishes()
    {
        var gate = new ScrollProbeGate();
        var (start, finish) = Blocking();

        var first = gate.TryStart(start);
        Assert.NotNull(first);
        finish.SetResult();
        await first;

        Assert.False(gate.Busy);
        var second = gate.TryStart(() => Task.CompletedTask);
        Assert.NotNull(second);
        await second;
    }

    [Fact]
    public async Task GuardIsReleasedWhenTheProbeThrows()
    {
        // A probe that faults must not take the feature down with it: the next
        // click still gets a probe.
        Exception? reported = null;
        var gate = new ScrollProbeGate(ex => reported = ex);
        var (start, finish) = Blocking();

        var first = gate.TryStart(start);
        Assert.NotNull(first);
        finish.SetException(new InvalidOperationException("probe failed"));
        await first;

        Assert.Equal("probe failed", reported?.Message);
        Assert.False(gate.Busy);
        Assert.NotNull(gate.TryStart(() => Task.CompletedTask));
    }

    [Fact]
    public async Task GuardIsReleasedWhenTheProbeNeverStarts()
    {
        // Everything between claiming the slot and the probe task's own catch -
        // resolving the filter service, constructing the sink and its pump -
        // can throw. A claim leaked there leaves the probe dead for the rest of
        // the process: every later click is refused and nothing ever runs
        // again. So a start that throws must release, and must not fling the
        // exception back through a menu click handler either.
        Exception? reported = null;
        var gate = new ScrollProbeGate(ex => reported = ex);
        var escaped = Record.Exception(
            () => { _ = gate.TryStart(() => throw new InvalidOperationException("sink construction")); });

        Assert.Null(escaped);
        Assert.Equal("sink construction", reported?.Message);
        Assert.False(gate.Busy);

        var second = gate.TryStart(() => Task.CompletedTask);
        Assert.NotNull(second);
        await second;
    }
}
