// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// SigningFix.BuildPlan is the whole automated driver-signing fix with the
// machine lifted out: four tri-state readings and a resume flag in, the ordered
// list of steps this PC actually needs plus the one confirmation the user is
// asked to agree to out. Everything that could hurt someone - a reboot nobody
// agreed to, a BitLocker suspend that was skipped, a plan that runs bcdedit
// while Secure Boot is on and cannot succeed - is decided there, so that is
// what is pinned here, for every combination of readings.
//
// RunAsync is deliberately untested: it writes a .ps1 into %TEMP%, raises a UAC
// prompt and restarts the PC. Faking that would only test the fake. What IS
// testable about it is the gate in front of it - nothing runs without a plan
// whose last step is a restart, with no step twice - and BuildScript, which is
// that gate plus the exact command order the elevated pass will use.
//
// The physics this file keeps honest (AGENTS.md:76): Secure Boot cannot be
// turned off from Windows. There is no API and no command - bcdedit refuses the
// testsigning write while it is on - so a Secure Boot PC gets the firmware trip
// and NOTHING else in that pass, and the rest of the fix arrives on the resume
// pass after the user has flipped it in firmware.
public class SigningFixTests
{
    static readonly bool?[] TriStates = [true, false, null];

    static bool IsReboot(SigningStep step) =>
        step is SigningStep.Reboot or SigningStep.RebootToFirmware;

    static int Count(IReadOnlyList<SigningStep> steps, SigningStep step)
    {
        int n = 0;
        foreach (var s in steps)
            if (s == step)
                n++;
        return n;
    }

    // Every forward plan this machine's readings can produce, and every revert
    // plan, kept separate because the two make different promises: the fix
    // explains what Test Mode costs, the undo explains what turning it back off
    // costs.
    static IEnumerable<SigningFixPlan> ForwardPlans()
    {
        foreach (var secureBoot in TriStates)
            foreach (var hvci in TriStates)
                foreach (var testSigning in TriStates)
                    foreach (var bitLocker in TriStates)
                        foreach (var resuming in new[] { false, true })
                            yield return SigningFix.BuildPlan(
                                secureBoot, hvci, testSigning, bitLocker, resuming);
    }

    static IEnumerable<SigningFixPlan> RevertPlans()
    {
        foreach (var testSigning in TriStates)
            foreach (var hvci in TriStates)
                yield return SigningFix.BuildRevertPlan(testSigning, hvci);
    }

    static IEnumerable<SigningFixPlan> AllPlans()
    {
        foreach (var plan in ForwardPlans())
            yield return plan;
        foreach (var plan in RevertPlans())
            yield return plan;
    }

    // ---- the owner's live PC ---------------------------------------------

    [Fact]
    public void OwnersLiveReadings_AreTheFirmwareTripAndNothingElse()
    {
        // Measured on the owner's PC, 2026-10-28: UEFISecureBootEnabled=1,
        // HVCI Enabled=1, TESTSIGNING=False, BitLocker unreadable from an
        // unelevated tray (SigningFix.ReadBitLockerProtected explains why that
        // read needs an administrator and returns null without one). This is
        // what the very first real run does, so it is pinned exactly.
        var plan = SigningFix.BuildPlan(
            secureBootOn: true, hvciOn: true, testSigningOn: false, bitLockerProtected: null,
            resuming: false);

        Assert.Equal(SigningFixState.NeedsFirmware, plan.State);
        Assert.Equal(
            new[] { SigningStep.SuspendBitLocker, SigningStep.RebootToFirmware },
            plan.Steps);
        Assert.Null(plan.BlockedReason);

        // The two steps that cannot work yet are not in this plan: with Secure
        // Boot still on, bcdedit is refused, so a plan containing it would be a
        // plan that fails halfway on this exact machine.
        Assert.DoesNotContain(SigningStep.EnableTestSigning, plan.Steps);
        Assert.DoesNotContain(SigningStep.DisableHvci, plan.Steps);
        Assert.Equal(SigningStep.RebootToFirmware, plan.Steps[^1]);
    }

    // ---- Secure Boot on: the firmware branch ------------------------------

    [Fact]
    public void SecureBootOn_WithBitLockerMeasuredOff_DropsTheSuspendStep()
    {
        var plan = SigningFix.BuildPlan(true, true, false, bitLockerProtected: false, resuming: false);

        Assert.Equal(SigningFixState.NeedsFirmware, plan.State);
        Assert.Equal(new[] { SigningStep.RebootToFirmware }, plan.Steps);
    }

    [Fact]
    public void SecureBootOn_WithBitLockerProtected_SuspendsBeforeTheFirmwareTrip()
    {
        var plan = SigningFix.BuildPlan(true, true, false, bitLockerProtected: true, resuming: false);

        Assert.Equal(
            new[] { SigningStep.SuspendBitLocker, SigningStep.RebootToFirmware },
            plan.Steps);
    }

    [Fact]
    public void SecureBootStillOnAfterTheFirmwareTrip_IsUnavailableAndRunsNothing()
    {
        // The resume pass found Secure Boot still on: the user could not find
        // the setting, the firmware would not allow it, or they changed their
        // mind. Sending them back into firmware would be a loop, and Magic Tray
        // cannot flip it for them.
        var plan = SigningFix.BuildPlan(true, true, false, null, resuming: true);

        Assert.Equal(SigningFixState.Unavailable, plan.State);
        Assert.Empty(plan.Steps);
        Assert.NotNull(plan.BlockedReason);
        Assert.Contains("Secure Boot is still on", plan.BlockedReason!, StringComparison.Ordinal);
        Assert.Contains("firmware", plan.BlockedReason!, StringComparison.Ordinal);
    }

    // ---- Secure Boot off (or unread): the full fix ------------------------

    [Fact]
    public void SecureBootOff_IsTheFullOrderedSequenceWithTheRebootLast()
    {
        var plan = SigningFix.BuildPlan(false, true, false, true, resuming: false);

        Assert.Equal(SigningFixState.Ready, plan.State);
        Assert.Equal(
            new[]
            {
                SigningStep.SuspendBitLocker,
                SigningStep.DisableHvci,
                SigningStep.EnableTestSigning,
                SigningStep.Reboot,
            },
            plan.Steps);
        Assert.DoesNotContain(SigningStep.RebootToFirmware, plan.Steps);
    }

    [Fact]
    public void SecureBootUnread_RunsTheFixRatherThanTheFirmwareTrip()
    {
        // Only a POSITIVE Secure Boot reading diverts to firmware. An
        // unreadable reading is the ordinary case on an unelevated tray, and
        // sending that PC into its firmware screen on a guess would be worse
        // than letting bcdedit report the refusal in its own words.
        var plan = SigningFix.BuildPlan(null, true, false, null, resuming: false);

        Assert.Equal(SigningFixState.Ready, plan.State);
        Assert.Equal(
            new[]
            {
                SigningStep.SuspendBitLocker,
                SigningStep.DisableHvci,
                SigningStep.EnableTestSigning,
                SigningStep.Reboot,
            },
            plan.Steps);
    }

    [Fact]
    public void BitLockerMeasuredOff_DropsTheSuspendStepFromTheFullFix()
    {
        var plan = SigningFix.BuildPlan(false, true, false, bitLockerProtected: false, resuming: false);

        Assert.Equal(
            new[] { SigningStep.DisableHvci, SigningStep.EnableTestSigning, SigningStep.Reboot },
            plan.Steps);
    }

    [Fact]
    public void HvciMeasuredOff_DropsTheMemoryIntegrityStep()
    {
        var plan = SigningFix.BuildPlan(false, hvciOn: false, testSigningOn: false,
            bitLockerProtected: true, resuming: false);

        Assert.Equal(
            new[] { SigningStep.SuspendBitLocker, SigningStep.EnableTestSigning, SigningStep.Reboot },
            plan.Steps);
    }

    [Fact]
    public void TestSigningAlreadyOn_DropsTheBcdeditStep()
    {
        var plan = SigningFix.BuildPlan(false, hvciOn: true, testSigningOn: true,
            bitLockerProtected: true, resuming: false);

        Assert.Equal(SigningFixState.Ready, plan.State);
        Assert.Equal(
            new[] { SigningStep.SuspendBitLocker, SigningStep.DisableHvci, SigningStep.Reboot },
            plan.Steps);
    }

    [Fact]
    public void UnreadHvciAndTestSigning_KeepBothSteps()
    {
        // Writing Enabled=0 over Enabled=0 and setting a bcdedit flag that is
        // already set are both no-ops, so an unreadable policy state acts. The
        // alternative - skipping a step on the PCs that could not be read - is
        // the one that leaves the fix incomplete.
        var plan = SigningFix.BuildPlan(false, null, null, false, resuming: false);

        Assert.Equal(
            new[] { SigningStep.DisableHvci, SigningStep.EnableTestSigning, SigningStep.Reboot },
            plan.Steps);
    }

    [Fact]
    public void ResumingWithSecureBootOff_FinishesTheJob()
    {
        var plan = SigningFix.BuildPlan(false, true, false, null, resuming: true);

        Assert.Equal(SigningFixState.Ready, plan.State);
        Assert.Equal(
            new[]
            {
                SigningStep.SuspendBitLocker,
                SigningStep.DisableHvci,
                SigningStep.EnableTestSigning,
                SigningStep.Reboot,
            },
            plan.Steps);
        Assert.Contains("finish the driver signing fix", plan.Prompt, StringComparison.Ordinal);
    }

    // ---- nothing to do ----------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void TestModeOnAndMemoryIntegrityOff_IsNothingToDoWhateverSecureBootSays(bool? secureBootOn)
    {
        // A correctly prepared PC. Checked before Secure Boot on purpose: test
        // signing can have been set before Secure Boot was turned back on, and
        // that PC needs no UAC prompt and no reboot.
        var plan = SigningFix.BuildPlan(secureBootOn, hvciOn: false, testSigningOn: true,
            bitLockerProtected: true, resuming: false);

        Assert.Equal(SigningFixState.NothingToDo, plan.State);
        Assert.Empty(plan.Steps);
        Assert.Null(plan.BlockedReason);
    }

    [Fact]
    public void APlanWithNoStepsNeverPromisesARestart()
    {
        foreach (var plan in new[]
                 {
                     SigningFix.BuildPlan(false, false, true, true, false),
                     SigningFix.BuildPlan(true, true, false, null, resuming: true),
                     SigningFix.BuildRevertPlan(testSigningOn: false, hvciOn: true),
                 })
        {
            Assert.Empty(plan.Steps);
            Assert.DoesNotContain("WILL RESTART", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("not restart this PC", plan.Prompt, StringComparison.Ordinal);
        }
    }

    // ---- the one confirmation --------------------------------------------

    [Fact]
    public void EveryRunnableFixPromptPromisesTheRebootTheAdminPromptAndTheRevert()
    {
        foreach (var plan in ForwardPlans())
        {
            if (plan.Steps.Count == 0)
                continue;

            // The reboot. It is the irreversible part of saying yes, so it is
            // shouted and it names the restart.
            Assert.Contains("WILL RESTART", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("Save your work", plan.Prompt, StringComparison.Ordinal);

            // One UAC prompt, and the promise that nothing else will ask.
            Assert.Contains("administrator", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("ONCE", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("will not ask you anything else", plan.Prompt, StringComparison.Ordinal);

            // The exact revert, as commands, so it is undoable by hand from
            // this dialog alone and not only from the tray menu.
            Assert.Contains("bcdedit /set testsigning off", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("Enabled = 1", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("Resume-BitLocker", plan.Prompt, StringComparison.Ordinal);

            // What Test Mode costs: the watermark and the unchecked drivers.
            Assert.Contains("watermark", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("Microsoft has not checked", plan.Prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ASuspendStepIsAlwaysExplainedAsOneRebootWithNoRecoveryKey()
    {
        foreach (var plan in ForwardPlans())
        {
            if (!plan.Steps.Contains(SigningStep.SuspendBitLocker))
                continue;

            Assert.Contains("Suspend-BitLocker -RebootCount 1", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("one restart", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("48-digit recovery key", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("you will not need the key", plan.Prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheFirmwarePromptSaysSecureBootCannotBeChangedFromWindows()
    {
        var plan = SigningFix.BuildPlan(true, true, false, null, resuming: false);

        // The physics, in plain words: no API, no command, firmware only.
        Assert.Contains("no program can do from inside Windows", plan.Prompt,
            StringComparison.Ordinal);
        Assert.Contains("The value is protected by Secure Boot policy", plan.Prompt,
            StringComparison.Ordinal);
        Assert.Contains("firmware (UEFI) setup screen", plan.Prompt, StringComparison.Ordinal);

        // What the restart does, what she does there, and that the tray comes
        // back for the rest.
        Assert.Contains("THIS PC WILL RESTART IMMEDIATELY", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("YOU: turn Secure Boot off", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("When Windows is back", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("finish the job", plan.Prompt, StringComparison.Ordinal);

        // And that it lowers boot security for as long as it stays off - the
        // same honesty SystemConfigChecker.cs:475-478 requires of the fact row.
        Assert.Contains("lowers this PC's boot security", plan.Prompt, StringComparison.Ordinal);
    }

    // The owner's complaint about the first version of this text was that the
    // two things that matter - what the app does, and what SHE has to do - were
    // buried in prose. The skeleton below is the answer, so it is pinned:
    // headings she can scan for, and a step list where every line says who does
    // it.
    [Fact]
    public void EveryRunnableFixPromptIsSectionedAndSaysWhoDoesEachStep()
    {
        foreach (var plan in ForwardPlans())
        {
            if (plan.Steps.Count == 0)
                continue;

            Assert.Contains("WHAT HAPPENS NOW\n", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("WHAT YOU NEED TO DO\n", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("BEFORE YOU SAY YES\n", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("WHAT THIS CHANGES ON THIS PC\n", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("TO UNDO\n", plan.Prompt, StringComparison.Ordinal);

            // Every numbered step the app takes is tagged with its actor.
            for (int i = 1; i <= plan.Steps.Count; i++)
                Assert.Contains($"{i}. Magic Tray: ", plan.Prompt, StringComparison.Ordinal);

            // The scroll wheel is why she is reading this at all.
            Assert.Contains("scroll wheel", plan.Prompt, StringComparison.Ordinal);
        }
    }

    // The one step Magic Tray cannot take, as an explicit line in the SAME
    // numbered list as the steps it does take, tagged YOU rather than left in a
    // paragraph underneath.
    [Fact]
    public void TheFirmwarePromptNumbersTheUsersOwnStepAsAYouLine()
    {
        var plan = SigningFix.BuildPlan(true, true, false, null, resuming: false);

        Assert.Equal(
            new[] { SigningStep.SuspendBitLocker, SigningStep.RebootToFirmware },
            plan.Steps);
        Assert.Contains("1. Magic Tray: suspends BitLocker", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("2. Magic Tray: restarts this PC into the firmware setup screen",
            plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("3. YOU: turn Secure Boot off on that screen, then save and exit",
            plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("4. YOU: answer Yes when Magic Tray asks to finish the job",
            plan.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("5. ", plan.Prompt, StringComparison.Ordinal);

        // And the toggle's usual home, so she is not hunting blind.
        Assert.Contains("Security or the Boot menu", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("Set Secure Boot to Disabled", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("manufacturer", plan.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReadyPromptDoesNotSendAnyoneToFirmware()
    {
        var plan = SigningFix.BuildPlan(false, true, false, true, resuming: false);

        Assert.Contains("bcdedit /set testsigning on", plan.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("firmware", plan.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Secure Boot", plan.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("YOU: ", plan.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePromptNumbersExactlyTheStepsThisPcWillTake()
    {
        foreach (var plan in AllPlans())
        {
            for (int i = 1; i <= plan.Steps.Count; i++)
                Assert.Contains($"{i}. ", plan.Prompt, StringComparison.Ordinal);

            // No step the plan does not contain: a numbered line one past the
            // end would mean the dialog describes work that will not happen.
            // The firmware branch is the one exception and it is deliberate:
            // the numbers past its own steps are the USER's steps, tagged as
            // hers, because the trip is only worth anything if she flips the
            // setting at the end of it.
            var past = $"{plan.Steps.Count + 1}. ";
            if (plan.Steps.Contains(SigningStep.RebootToFirmware))
                Assert.Contains(past + "YOU: ", plan.Prompt, StringComparison.Ordinal);
            else
                Assert.DoesNotContain(past, plan.Prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OnlyAPlanThatTouchesMemoryIntegrityNamesTheKeyItWrites()
    {
        foreach (var plan in AllPlans())
        {
            bool touches = plan.Steps.Contains(SigningStep.DisableHvci)
                || plan.Steps.Contains(SigningStep.EnableHvci);
            if (!touches)
                continue;
            Assert.Contains("HypervisorEnforcedCodeIntegrity", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("Memory integrity (Core isolation)", plan.Prompt, StringComparison.Ordinal);
        }
    }

    // ---- shape, over every combination of readings ------------------------

    [Fact]
    public void TheRebootIsAlwaysTheLastStepAndThereIsOnlyEverOne()
    {
        foreach (var plan in AllPlans())
        {
            if (plan.Steps.Count == 0)
            {
                Assert.True(
                    plan.State is SigningFixState.NothingToDo or SigningFixState.Unavailable,
                    $"empty plan was {plan.State}");
                continue;
            }

            Assert.True(IsReboot(plan.Steps[^1]), $"last step was {plan.Steps[^1]}");
            Assert.Equal(
                1,
                Count(plan.Steps, SigningStep.Reboot) + Count(plan.Steps, SigningStep.RebootToFirmware));

            // No step twice: every step is idempotent on its own, but a plan
            // that repeats one was not built by BuildPlan.
            Assert.Equal(plan.Steps.Count, new HashSet<SigningStep>(plan.Steps).Count);
        }
    }

    [Fact]
    public void OnlyTheFirmwareBranchEverRestartsIntoFirmware()
    {
        foreach (var plan in AllPlans())
        {
            bool firmware = plan.Steps.Contains(SigningStep.RebootToFirmware);
            Assert.Equal(plan.State == SigningFixState.NeedsFirmware, firmware);
            if (!firmware)
                continue;

            // The firmware pass carries the BitLocker suspend at most, and
            // never a policy write: with Secure Boot still on the bcdedit write
            // is refused, and the HVCI write would be a change made for a reboot
            // that is not going to Windows.
            Assert.DoesNotContain(SigningStep.EnableTestSigning, plan.Steps);
            Assert.DoesNotContain(SigningStep.DisableHvci, plan.Steps);
            Assert.True(plan.Steps.Count is 1 or 2, $"firmware plan had {plan.Steps.Count} steps");
        }
    }

    [Fact]
    public void AForwardFixNeverContainsARevertStepAndTheRevertNeverContainsAForwardOne()
    {
        foreach (var plan in ForwardPlans())
        {
            Assert.DoesNotContain(SigningStep.DisableTestSigning, plan.Steps);
            Assert.DoesNotContain(SigningStep.EnableHvci, plan.Steps);
            Assert.DoesNotContain(SigningStep.ResumeBitLocker, plan.Steps);
        }

        foreach (var plan in RevertPlans())
        {
            Assert.DoesNotContain(SigningStep.EnableTestSigning, plan.Steps);
            Assert.DoesNotContain(SigningStep.DisableHvci, plan.Steps);
            Assert.DoesNotContain(SigningStep.SuspendBitLocker, plan.Steps);
            Assert.DoesNotContain(SigningStep.RebootToFirmware, plan.Steps);
        }
    }

    // ---- the revert -------------------------------------------------------

    [Fact]
    public void TheRevertPutsEverythingBackInOneOrderedPass()
    {
        var plan = SigningFix.BuildRevertPlan(testSigningOn: true, hvciOn: false);

        Assert.Equal(SigningFixState.Ready, plan.State);
        Assert.Equal(
            new[]
            {
                SigningStep.DisableTestSigning,
                SigningStep.EnableHvci,
                SigningStep.ResumeBitLocker,
                SigningStep.Reboot,
            },
            plan.Steps);
        Assert.Contains("bcdedit /set testsigning off", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("WILL RESTART", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("administrator", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("ONCE", plan.Prompt, StringComparison.Ordinal);

        // The undo has to be honest about its own cost: scroll stops if this
        // mouse was relying on a self-signed driver.
        Assert.Contains("scroll stops working", plan.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRevertAlwaysResumesBitLockerEvenWhenNothingElseIsKnown()
    {
        // Leaving protection suspended because a reading failed is the one
        // outcome that actually costs the user something, and Resume-BitLocker
        // on a volume that was never suspended is a no-op.
        var plan = SigningFix.BuildRevertPlan(testSigningOn: null, hvciOn: null);

        Assert.Contains(SigningStep.ResumeBitLocker, plan.Steps);
        Assert.Contains(SigningStep.DisableTestSigning, plan.Steps);
        Assert.Contains(SigningStep.EnableHvci, plan.Steps);
        Assert.Equal(SigningStep.Reboot, plan.Steps[^1]);
    }

    [Fact]
    public void TheRevertIsNeverBlockedBySecureBoot()
    {
        // Turning test signing OFF is always permitted, so the undo the charter
        // demands is available on every PC - including one whose forward fix is
        // Unavailable. BuildRevertPlan is not even given the Secure Boot
        // reading, which is how that is guaranteed.
        var plan = SigningFix.BuildRevertPlan(testSigningOn: true, hvciOn: false);

        Assert.Equal(SigningFixState.Ready, plan.State);
        Assert.Null(plan.BlockedReason);
    }

    [Fact]
    public void AlreadyRevertedPcIsNothingToUndo()
    {
        var plan = SigningFix.BuildRevertPlan(testSigningOn: false, hvciOn: true);

        Assert.Equal(SigningFixState.NothingToDo, plan.State);
        Assert.Empty(plan.Steps);
    }

    // ---- the one elevated pass --------------------------------------------

    [Fact]
    public void TheScriptRunsTheStepsInPlanOrderAndRebootsLast()
    {
        var plan = SigningFix.BuildPlan(false, true, false, true, resuming: false);
        var script = SigningFix.BuildScript(plan, nonce: 1);

        var suspend = script.IndexOf("Suspend-BitLocker", StringComparison.Ordinal);
        var hvci = script.IndexOf("-Name Enabled -Value 0", StringComparison.Ordinal);
        var bcdedit = script.IndexOf("bcdedit.exe /set testsigning on", StringComparison.Ordinal);
        var shutdown = script.IndexOf("shutdown.exe /r /t 0", StringComparison.Ordinal);

        Assert.True(suspend >= 0 && hvci > suspend && bcdedit > hvci && shutdown > bcdedit,
            $"order was suspend={suspend} hvci={hvci} bcdedit={bcdedit} shutdown={shutdown}");

        // A non-zero bcdedit exit is a hard stop, and its own words are what the
        // user is shown: on a Secure Boot machine that line is the entire
        // explanation.
        Assert.Contains("if ($bcdExit -ne 0)", script, StringComparison.Ordinal);
        Assert.Contains("2>&1 | Out-String", script, StringComparison.Ordinal);

        // A plain restart, never the firmware one, on a PC that is not going to
        // firmware.
        Assert.DoesNotContain("/fw", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirmwareScriptAsksForTheFirmwareRestartAndTouchesNoPolicy()
    {
        var plan = SigningFix.BuildPlan(true, true, false, null, resuming: false);
        var script = SigningFix.BuildScript(plan, nonce: 1);

        Assert.Contains("shutdown.exe /r /fw /t 0", script, StringComparison.Ordinal);
        Assert.Contains("Suspend-BitLocker", script, StringComparison.Ordinal);
        Assert.DoesNotContain("bcdedit", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Name Enabled", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRevertScriptPutsTheValuesBackAndResumesBitLocker()
    {
        var plan = SigningFix.BuildRevertPlan(testSigningOn: true, hvciOn: false);
        var script = SigningFix.BuildScript(plan, nonce: 1);

        var bcdedit = script.IndexOf("bcdedit.exe /set testsigning off", StringComparison.Ordinal);
        var hvci = script.IndexOf("-Name Enabled -Value 1", StringComparison.Ordinal);
        var resume = script.IndexOf("Resume-BitLocker", StringComparison.Ordinal);
        var shutdown = script.IndexOf("shutdown.exe /r /t 0", StringComparison.Ordinal);

        Assert.True(bcdedit >= 0 && hvci > bcdedit && resume > hvci && shutdown > resume,
            $"order was bcdedit={bcdedit} hvci={hvci} resume={resume} shutdown={shutdown}");
        Assert.DoesNotContain("testsigning on", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Value 0", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRunnablePlanProducesAScriptThatEndsInExactlyOneShutdown()
    {
        foreach (var plan in AllPlans())
        {
            if (plan.Steps.Count == 0)
                continue;
            var script = SigningFix.BuildScript(plan, nonce: 1);

            var shutdowns = 0;
            var index = script.IndexOf("shutdown.exe", StringComparison.Ordinal);
            while (index >= 0)
            {
                shutdowns++;
                index = script.IndexOf("shutdown.exe", index + 1, StringComparison.Ordinal);
            }
            Assert.Equal(1, shutdowns);

            // The status file is written before the restart is requested,
            // because Windows can tear the elevated process down the moment it
            // accepts the request.
            var statusOk = script.IndexOf("Write-Status 'ok'", StringComparison.Ordinal);
            var request = script.IndexOf("shutdown.exe", StringComparison.Ordinal);
            Assert.True(statusOk >= 0 && statusOk < request,
                $"status={statusOk} shutdown={request}");
        }
    }

    [Fact]
    public void AScriptIsRefusedForAnyPlanThatWouldNotEndInARestart()
    {
        // The gate in front of the one elevated pass: a plan BuildPlan would
        // never produce does not get a script, so a caller that builds a record
        // by hand cannot run bcdedit or a reboot through this file.
        Assert.Throws<InvalidOperationException>(() => SigningFix.BuildScript(
            new SigningFixPlan(SigningFixState.Ready, [SigningStep.EnableTestSigning], "", null), 1));
        Assert.Throws<InvalidOperationException>(() => SigningFix.BuildScript(
            new SigningFixPlan(
                SigningFixState.Ready,
                [SigningStep.Reboot, SigningStep.EnableTestSigning],
                "",
                null),
            1));
        Assert.Throws<InvalidOperationException>(() => SigningFix.BuildScript(
            SigningFix.BuildPlan(false, false, true, true, false), 1));
        Assert.Throws<InvalidOperationException>(() => SigningFix.BuildScript(
            SigningFix.BuildPlan(true, true, false, null, resuming: true), 1));
    }
}
