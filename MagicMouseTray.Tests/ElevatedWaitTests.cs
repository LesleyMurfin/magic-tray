// SPDX-License-Identifier: MIT
using System;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

/// <summary>
/// A declined UAC prompt ends the wait, it does not start a two-minute one.
/// </summary>
/// <remarks>
/// DeviceEnable launches an elevated PowerShell and then waits for the status
/// sidecar that script writes. The wait has exactly three ways to end, and
/// only two of them were ever decided on evidence: the sidecar appeared, or
/// the ceiling expired. The third - the elevated process is gone and no
/// report was ever written, which is what a refused consent prompt looks like
/// from here - has nothing left to wait for, and anything the loop spends
/// after that point is spent on a verdict it already has.
///
/// That matters because the loop is what the tray blocks on. Two minutes of
/// polling after a prompt the user dismissed in one second is two minutes of
/// dead message pump followed by a message saying the prompt was declined.
///
/// A verdict, not a boolean: "the sidecar is here" and "the process is gone
/// and nothing was written" and "time is up" are three different facts and
/// they decide three different things, so the loop reports which one it saw
/// instead of leaving the caller to re-derive it. The precedence is the part
/// that is easy to get wrong, and it is pinned below: a complete report wins
/// over everything, including a process that has already exited and a clock
/// that has already run out, because a report is a state somebody measured
/// and a timeout is only a state nobody waited for.
///
/// The settle window is the one concession: a script that writes its report
/// and exits can be observed exited a few milliseconds before the file is
/// readable, so "gone" is only believed once the process has been gone for
/// that long. Gone for that long, and not waited for that long - the two are
/// the same number only when the elevated step returns instantly, and the
/// ordinary run is a UAC prompt plus a pnputil pass. So the verdict reads
/// two clocks: the age of the exit it observed, and the age of the wait.
/// The settle window is bounded here so it can never grow into the ceiling.
/// </remarks>
public class ElevatedWaitTests
{
    // Two clocks. sinceExit is null while the process is still running and
    // the age of the FIRST observed exit afterwards; elapsed is the age of
    // the whole wait, which only the ceiling reads.
    static ElevatedWaitVerdict Step(bool sidecarComplete, TimeSpan? sinceExit, TimeSpan elapsed) =>
        DeviceEnable.NextWaitStep(sidecarComplete, sinceExit, elapsed);

    // Still running at this point in the wait: no exit to age.
    static ElevatedWaitVerdict Running(bool sidecarComplete, TimeSpan elapsed) =>
        Step(sidecarComplete, sinceExit: null, elapsed);

    static TimeSpan Settle => DeviceEnable.ElevatedWaitSettle;

    static TimeSpan Ceiling => DeviceEnable.ElevatedWaitCeiling;

    [Fact]
    public void NextWaitStep_ProcessGoneWithNoReport_EndsTheWaitThere()
    {
        Assert.Equal(
            ElevatedWaitVerdict.ProcessGone,
            Step(false, sinceExit: Settle, elapsed: Settle));
        Assert.Equal(
            ElevatedWaitVerdict.ProcessGone,
            Step(false,
                sinceExit: Settle + TimeSpan.FromMilliseconds(150),
                elapsed: Settle + TimeSpan.FromMilliseconds(150)));

        // Promptly: the verdict is reached in the settle window, not at the
        // ceiling, and the two are not remotely the same length.
        Assert.True(Settle * 4 < Ceiling, "the settle window has grown into the ceiling");
    }

    [Fact]
    public void NextWaitStep_TheSettleWindowIsMeasuredFromTheExit_NotFromTheStartOfTheWait()
    {
        // The ordinary elevated run is a UAC prompt the user reads plus a
        // pnputil pass, so it is well past the settle window by the time the
        // script exits. Measured from the start of the wait the grace would
        // already be spent, and the very first pass that saw the process gone
        // would report a change that succeeded as unverified - which is what
        // the sidecar's own lag exists to prevent.
        var realRun = TimeSpan.FromSeconds(40);
        Assert.Equal(
            ElevatedWaitVerdict.KeepWaiting,
            Step(false, sinceExit: TimeSpan.Zero, elapsed: realRun));
        Assert.Equal(
            ElevatedWaitVerdict.KeepWaiting,
            Step(false,
                sinceExit: Settle - TimeSpan.FromTicks(1),
                elapsed: realRun + Settle));

        // And the grace is spent from there, not extended: one settle window
        // after the exit it saw, the wait is over.
        Assert.Equal(
            ElevatedWaitVerdict.ProcessGone,
            Step(false, sinceExit: Settle, elapsed: realRun + Settle));
    }

    [Fact]
    public void NextWaitStep_InsideTheSettleWindow_StillWaitsForTheReportTheScriptWrote()
    {
        // The script writes the report and then exits; the file can lag the
        // exit. Declaring the attempt dead at the first exited read would turn
        // a completed change into "nothing was verified".
        Assert.Equal(
            ElevatedWaitVerdict.KeepWaiting,
            Step(false, sinceExit: TimeSpan.Zero, elapsed: TimeSpan.Zero));
        Assert.Equal(
            ElevatedWaitVerdict.KeepWaiting,
            Step(false,
                sinceExit: Settle - TimeSpan.FromTicks(1),
                elapsed: Settle - TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void NextWaitStep_ProcessStillRunning_KeepsWaitingRightUpToTheCeiling()
    {
        // An elevated install that is working is not a hung one: while the
        // process lives, the absent report means "not finished", never
        // "declined". No exit observed, so the settle window never opens
        // however long the wait has been going.
        Assert.Equal(ElevatedWaitVerdict.KeepWaiting, Running(false, TimeSpan.Zero));
        Assert.Equal(ElevatedWaitVerdict.KeepWaiting, Running(false, Settle));
        Assert.Equal(
            ElevatedWaitVerdict.KeepWaiting,
            Running(false, Ceiling - TimeSpan.FromTicks(1)));

        // And the ceiling is still a wall: a script that never reports and
        // never exits is given up on, and said to be given up on.
        Assert.Equal(ElevatedWaitVerdict.CeilingExpired, Running(false, Ceiling));
        Assert.Equal(
            ElevatedWaitVerdict.CeilingExpired,
            Running(false, Ceiling + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void NextWaitStep_ACompleteReport_IsTheVerdictWhateverElseIsTrue()
    {
        // Present and the process still running - the script reports before it
        // exits, so this is the ordinary success.
        Assert.Equal(ElevatedWaitVerdict.SidecarReady, Running(true, TimeSpan.Zero));
        Assert.Equal(ElevatedWaitVerdict.SidecarReady, Running(true, Settle));

        // Present and the process gone: a finished attempt, never a declined
        // prompt. ProcessGone here would report "nothing was verified" over a
        // state the script actually measured.
        Assert.Equal(
            ElevatedWaitVerdict.SidecarReady,
            Step(true, sinceExit: Settle, elapsed: Settle));
        Assert.Equal(
            ElevatedWaitVerdict.SidecarReady,
            Step(true,
                sinceExit: Settle + TimeSpan.FromSeconds(1),
                elapsed: Settle + TimeSpan.FromSeconds(1)));

        // Present and the clock already out: the report was measured, the
        // timeout was not, so the measurement wins.
        Assert.Equal(ElevatedWaitVerdict.SidecarReady, Running(true, Ceiling));
        Assert.Equal(
            ElevatedWaitVerdict.SidecarReady,
            Step(true,
                sinceExit: Ceiling,
                elapsed: Ceiling + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ElevatedWait_TheSettleWindowIsSecondsAndTheCeilingIsTheWall()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), Ceiling);
        Assert.True(Settle > TimeSpan.Zero, "a zero settle window calls a reporting script declined");
        Assert.True(
            Settle <= TimeSpan.FromSeconds(5),
            "the settle window is the lag on one file write, not a second ceiling");
        Assert.True(Settle < Ceiling);
    }

    [Fact]
    public void Decide_StartedThenGoneWithNoReport_IsAFailureAndNeverBlamesTheUacPrompt()
    {
        // The verdict this is the end state of.
        Assert.Equal(
            ElevatedWaitVerdict.ProcessGone,
            Step(false, sinceExit: Settle, elapsed: Settle));

        // ShellExecute handed back a process, so elevation was granted: a
        // silent step is a failure of the step, and the user is not told they
        // cancelled something they approved.
        var started = DeviceEnable.Decide(
            new DeviceEnableEvidence(Started: true, Reading: null, Exit: null), enable: true);
        Assert.Equal(DeviceEnableOutcome.Failed, started.Outcome);
        Assert.False(started.Succeeded);
        Assert.DoesNotContain("UAC", started.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cancel", started.Detail, StringComparison.OrdinalIgnoreCase);

        // No process at all is the one case that is the prompt, and it says so.
        var declined = DeviceEnable.Decide(
            new DeviceEnableEvidence(Started: false, Reading: null, Exit: null), enable: true);
        Assert.Equal(DeviceEnableOutcome.UacDeclined, declined.Outcome);
        Assert.False(declined.Succeeded);
        Assert.Contains("UAC", declined.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(started.Detail, declined.Detail);
    }
}
