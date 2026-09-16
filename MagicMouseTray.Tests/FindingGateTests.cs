// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// FindingGate is the persistence rule between RepairPlanner.Plan and everything
// the user sees. The faults it has to survive are not hypothetical: a driver
// rebuild/reinstall on the reference PC walks the BTHENUM stack through
// FilterStoppedButBound, FilterNotInStack, ConflictingFilters and
// PointerChildMissing for a few seconds each, and every one of those would
// otherwise toast and offer an elevated pnputil /restart-device against an
// install still in flight.
//
// Every time here is injected from a fixed T0 - no Thread.Sleep, no wall clock -
// and each test builds its own gate, so there is no shared state to leak.
public class FindingGateTests
{
    static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    static readonly TimeSpan Hold = FindingGate.HoldWindow;

    static RepairFinding Finding(string pid, RepairProblem problem) => new(
        Pid: pid,
        Problem: problem,
        Action: RepairAction.RestartBtHidParent,
        Title: $"{problem} on {pid}",
        Detail: "detail",
        AutoFixable: true);

    static IReadOnlyList<RepairFinding> Raw(params RepairFinding[] findings) => findings;

    static readonly IReadOnlyList<RepairFinding> Nothing = Array.Empty<RepairFinding>();

    [Fact]
    public void SeenOnce_IsNotConfirmed()
    {
        var gate = new FindingGate();
        var fault = Finding("0323", RepairProblem.FilterNotInStack);

        var result = gate.Confirm(Raw(fault), T0);

        Assert.Empty(result.Confirmed);
        Assert.Equal(new[] { fault }, result.Pending);
    }

    [Fact]
    public void SeenTwiceInsideHoldWindow_IsNotConfirmed()
    {
        var gate = new FindingGate();
        var fault = Finding("0323", RepairProblem.FilterStoppedButBound);

        // Refreshes here are bursty: menu Opening, a poll tick and
        // AfterFindingHandled can all land within the same second. Two
        // observations that close together prove nothing about persistence.
        gate.Confirm(Raw(fault), T0);
        var result = gate.Confirm(Raw(fault), T0 + Hold - TimeSpan.FromMilliseconds(1));

        Assert.Empty(result.Confirmed);
        Assert.Equal(new[] { fault }, result.Pending);
    }

    [Fact]
    public void SeenTwiceSpanningHoldWindow_IsConfirmed()
    {
        var gate = new FindingGate();
        var fault = Finding("0323", RepairProblem.FilterNotInStack);

        gate.Confirm(Raw(fault), T0);
        var result = gate.Confirm(Raw(fault), T0 + Hold);

        Assert.Equal(new[] { fault }, result.Confirmed);
        Assert.Empty(result.Pending);
    }

    [Fact]
    public void OneObservationHoweverOld_IsNeverEnough()
    {
        var gate = new FindingGate();
        var fault = Finding("0323", RepairProblem.PointerChildMissing);

        // The poll interval has been observed live at a full day
        // (POLL_SCHEDULED next_in=1.00:00:00), so elapsed time on its own would
        // let a single sample confirm itself. A single sample cannot tell a
        // standing fault from the instant a device stack was torn down.
        var result = gate.Confirm(Raw(fault), T0 + TimeSpan.FromDays(1));

        Assert.Empty(result.Confirmed);
        Assert.Equal(new[] { fault }, result.Pending);
    }

    [Fact]
    public void VanishingAndReappearing_RestartsTheClock()
    {
        var gate = new FindingGate();
        // The driver-rebuild case exactly: the filter drops out of
        // DEVPKEY_Device_Stack while pnputil is restarting the device, the next
        // PnP step puts it back, and the whole excursion is over inside the
        // repair script's own 10 s poll (DeviceRepair.cs:195).
        var fault = Finding("0323", RepairProblem.FilterNotInStack);

        gate.Confirm(Raw(fault), T0);
        gate.Confirm(Raw(fault), T0 + TimeSpan.FromSeconds(5));
        gate.Confirm(Nothing, T0 + TimeSpan.FromSeconds(6));

        // Credit is gone, so the reappearance is a first observation again: a
        // point past T0 + Hold is not past reappearance + Hold.
        gate.Confirm(Raw(fault), T0 + TimeSpan.FromSeconds(7));
        var stillHeld = gate.Confirm(Raw(fault), T0 + Hold + TimeSpan.FromSeconds(1));

        Assert.Empty(stillHeld.Confirmed);
        Assert.Equal(new[] { fault }, stillHeld.Pending);

        // It only confirms once it has persisted a full window from the
        // reappearance, which a transient by definition does not.
        var confirmed = gate.Confirm(Raw(fault), T0 + TimeSpan.FromSeconds(7) + Hold);

        Assert.Equal(new[] { fault }, confirmed.Confirmed);
    }

    [Fact]
    public void ConfirmedFault_StaysConfirmedWhileSeen_AndIsDroppedWhenItStops()
    {
        var gate = new FindingGate();
        var fault = Finding("0323", RepairProblem.ConflictingFilters);

        gate.Confirm(Raw(fault), T0);
        gate.Confirm(Raw(fault), T0 + Hold);

        // The menu row must not flicker between the fault and "No problems
        // found" from one open to the next, so a confirmed pair stays confirmed
        // for as long as it keeps appearing - including on a burst refresh.
        var again = gate.Confirm(Raw(fault), T0 + Hold + TimeSpan.FromMilliseconds(200));
        Assert.Equal(new[] { fault }, again.Confirmed);
        Assert.Empty(again.Pending);

        // Fixed (or fixed itself): gone from the planner, gone from the menu.
        var cleared = gate.Confirm(Nothing, T0 + Hold + TimeSpan.FromSeconds(1));
        Assert.Empty(cleared.Confirmed);
        Assert.Empty(cleared.Pending);

        // And the confirmation is not remembered: coming back has to earn it.
        var returned = gate.Confirm(Raw(fault), T0 + Hold + TimeSpan.FromSeconds(2));
        Assert.Empty(returned.Confirmed);
        Assert.Equal(new[] { fault }, returned.Pending);
    }

    [Fact]
    public void TwoPids_AreTrackedIndependently()
    {
        var gate = new FindingGate();
        var mouse = Finding("0323", RepairProblem.FilterNotInStack);
        var keyboard = Finding("0267", RepairProblem.NoInstances);

        gate.Confirm(Raw(mouse), T0);
        var result = gate.Confirm(Raw(mouse, keyboard), T0 + Hold);

        // The mouse has spanned the window; the keyboard was first seen on this
        // very refresh and must not inherit the mouse's credit.
        Assert.Equal(new[] { mouse }, result.Confirmed);
        Assert.Equal(new[] { keyboard }, result.Pending);
    }

    [Fact]
    public void TwoProblemsOnOnePid_AreTrackedIndependently()
    {
        var gate = new FindingGate();
        // PlanOne returns one finding per device, but the ladder's first match
        // changes as an install progresses, so the same PID legitimately
        // presents different problems across refreshes.
        var stopped = Finding("0323", RepairProblem.FilterStoppedButBound);
        var notInStack = Finding("0323", RepairProblem.FilterNotInStack);

        gate.Confirm(Raw(stopped), T0);
        var result = gate.Confirm(Raw(stopped, notInStack), T0 + Hold);

        Assert.Equal(new[] { stopped }, result.Confirmed);
        Assert.Equal(new[] { notInStack }, result.Pending);
    }

    [Fact]
    public void OutputPreservesReaderOrder()
    {
        var gate = new FindingGate();
        var first = Finding("0323", RepairProblem.FilterNotInStack);
        var middle = Finding("030D", RepairProblem.NoInstances);
        var last = Finding("0267", RepairProblem.BatteryReadBlocked);

        // first and last have spanned the window; middle joins late, so the
        // result is the mixed case - and the menu shows findings in the order
        // the reader produced them, so both lists must keep it.
        gate.Confirm(Raw(first, last), T0);
        var result = gate.Confirm(Raw(first, middle, last), T0 + Hold);

        Assert.Equal(new[] { first, last }, result.Confirmed);
        Assert.Equal(new[] { middle }, result.Pending);

        var all = gate.Confirm(Raw(first, middle, last), T0 + Hold + Hold);
        Assert.Equal(new[] { first, middle, last }, all.Confirmed);
        Assert.Empty(all.Pending);
    }
}
