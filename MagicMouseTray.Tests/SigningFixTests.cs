// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// SigningFix.BuildPlan is the whole automated driver-signing fix with the
// machine lifted out: three tri-state readings and a resume flag in, the
// ordered list of steps this PC actually needs plus the one confirmation the
// user is asked to agree to out. Everything that could hurt someone - a reboot
// nobody agreed to, a plan that rewrites boot configuration without suspending
// BitLocker first, a plan that runs bcdedit while Secure Boot is on and cannot
// succeed - is decided there, so that is what is pinned here, for every
// combination of readings.
//
// BitLocker is not one of those readings and there is nothing tri-state to pin
// about it: only an administrator can read whether this drive is protected, so
// every plan that rewrites boot configuration carries the suspend and the
// elevated script decides suspend-or-nothing. What IS pinned here is the order
// that makes that safe - the suspend, and its refusal to continue, both come
// before the first boot-configuration write.
//
// RunAsync is deliberately untested: it writes a .ps1 into %TEMP%, raises a UAC
// prompt and restarts the PC. Faking that would only test the fake. What IS
// testable about it is the gate in front of it - IsRunnable, reached through
// BuildScript: a runnable state, a last step that is a restart, and no step
// twice - plus the exact command order the elevated pass will use, and the
// nonce validation on the two file names that cross the elevation boundary.
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

    // 0-based, and -1 when the step is not in this plan at all.
    static int IndexOfStep(IReadOnlyList<SigningStep> steps, SigningStep step)
    {
        for (int i = 0; i < steps.Count; i++)
            if (steps[i] == step)
                return i;
        return -1;
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
                    foreach (var resuming in new[] { false, true })
                        yield return SigningFix.BuildPlan(secureBoot, hvci, testSigning, resuming);
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
        // HVCI Enabled=1, TESTSIGNING=False. This is what the very first real
        // run does, so it is pinned exactly.
        var plan = SigningFix.BuildPlan(
            secureBootOn: true, hvciOn: true, testSigningOn: false, resuming: false);

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
    public void SecureBootStillOnAfterTheFirmwareTrip_IsUnavailableAndRunsNothing()
    {
        // The resume pass found Secure Boot still on: the user could not find
        // the setting, the firmware would not allow it, or they changed their
        // mind. Sending them back into firmware would be a loop, and Magic Tray
        // cannot flip it for them.
        var plan = SigningFix.BuildPlan(true, true, false, resuming: true);

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
        var plan = SigningFix.BuildPlan(false, true, false, resuming: false);

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
        var plan = SigningFix.BuildPlan(null, true, false, resuming: false);

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
    public void HvciMeasuredOff_DropsTheMemoryIntegrityStep()
    {
        var plan = SigningFix.BuildPlan(false, hvciOn: false, testSigningOn: false, resuming: false);

        Assert.Equal(
            new[] { SigningStep.SuspendBitLocker, SigningStep.EnableTestSigning, SigningStep.Reboot },
            plan.Steps);
    }

    [Fact]
    public void TestSigningAlreadyOn_DropsTheBcdeditStep()
    {
        var plan = SigningFix.BuildPlan(false, hvciOn: true, testSigningOn: true, resuming: false);

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
        var plan = SigningFix.BuildPlan(false, null, null, resuming: false);

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
    public void ResumingWithSecureBootOff_FinishesTheJob()
    {
        var plan = SigningFix.BuildPlan(false, true, false, resuming: true);

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
            resuming: false);

        Assert.Equal(SigningFixState.NothingToDo, plan.State);
        Assert.Empty(plan.Steps);
        Assert.Null(plan.BlockedReason);
    }

    [Fact]
    public void APlanWithNoStepsNeverPromisesARestart()
    {
        foreach (var plan in new[]
                 {
                     SigningFix.BuildPlan(false, false, true, false),
                     SigningFix.BuildPlan(true, true, false, resuming: true),
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

            // The command is in the text because it is the only way to check
            // the app by hand, and manage-bde is the one that ships on every
            // edition of Windows, Home included.
            Assert.Contains("manage-bde -protectors -disable -rebootcount 1", plan.Prompt,
                StringComparison.Ordinal);
            Assert.Contains("one restart", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("48-digit recovery key", plan.Prompt, StringComparison.Ordinal);
            Assert.Contains("you will not need the key", plan.Prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheFirmwarePromptSaysSecureBootCannotBeChangedFromWindows()
    {
        var plan = SigningFix.BuildPlan(true, true, false, resuming: false);

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
        var plan = SigningFix.BuildPlan(true, true, false, resuming: false);

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
        var plan = SigningFix.BuildPlan(false, true, false, resuming: false);

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

            // The firmware pass carries the BitLocker suspend and the trip, and
            // never a policy write: with Secure Boot still on the bcdedit write
            // is refused, and the HVCI write would be a change made for a reboot
            // that is not going to Windows.
            Assert.Equal(
                new[] { SigningStep.SuspendBitLocker, SigningStep.RebootToFirmware },
                plan.Steps);
        }
    }

    [Fact]
    public void AForwardFixNeverContainsARevertStepAndTheRevertNeverContainsAForwardOne()
    {
        foreach (var plan in ForwardPlans())
        {
            Assert.DoesNotContain(SigningStep.DisableTestSigning, plan.Steps);
            Assert.DoesNotContain(SigningStep.EnableHvci, plan.Steps);
        }

        foreach (var plan in RevertPlans())
        {
            Assert.DoesNotContain(SigningStep.EnableTestSigning, plan.Steps);
            Assert.DoesNotContain(SigningStep.DisableHvci, plan.Steps);
            Assert.DoesNotContain(SigningStep.RebootToFirmware, plan.Steps);
        }
    }

    // The lockout this branch exists to prevent: a pass that rewrote the BCD or
    // the HVCI scenario with protection still active, so the TPM seal no longer
    // matched the next boot and Windows came back asking for the 48-digit
    // recovery key. The suspend is in every runnable plan in both directions,
    // and it is ahead of every write and ahead of the restart.
    [Fact]
    public void EveryRunnablePlanSuspendsBitLockerBeforeItRewritesBootConfiguration()
    {
        foreach (var plan in AllPlans())
        {
            if (plan.Steps.Count == 0)
                continue;

            var suspend = IndexOfStep(plan.Steps, SigningStep.SuspendBitLocker);
            Assert.True(suspend >= 0, $"{plan.State} plan had no suspend: {string.Join(",", plan.Steps)}");

            foreach (var write in new[]
                     {
                         SigningStep.DisableHvci,
                         SigningStep.EnableHvci,
                         SigningStep.EnableTestSigning,
                         SigningStep.DisableTestSigning,
                     })
            {
                var at = IndexOfStep(plan.Steps, write);
                if (at >= 0)
                    Assert.True(suspend < at, $"{write} at {at} ran before the suspend at {suspend}");
            }

            Assert.True(suspend < plan.Steps.Count - 1,
                $"the suspend at {suspend} was the restart itself");
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
                SigningStep.SuspendBitLocker,
                SigningStep.DisableTestSigning,
                SigningStep.EnableHvci,
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
    public void TheRevertWithNothingKnownStillPutsBothSettingsBack()
    {
        // Leaving Test Mode on or Memory integrity off because a reading failed
        // is the outcome that actually costs the user something, and both
        // writes are no-ops on a PC that is already back.
        var plan = SigningFix.BuildRevertPlan(testSigningOn: null, hvciOn: null);

        Assert.Equal(
            new[]
            {
                SigningStep.SuspendBitLocker,
                SigningStep.DisableTestSigning,
                SigningStep.EnableHvci,
                SigningStep.Reboot,
            },
            plan.Steps);
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
        var plan = SigningFix.BuildPlan(false, true, false, resuming: false);
        var script = SigningFix.BuildScript(plan, nonce: 1);

        var suspend = script.IndexOf("manage-bde.exe -protectors -disable", StringComparison.Ordinal);
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

    // The other half of the lockout fix, and the half only the script can do:
    // the tray cannot read whether this drive is protected, so the script does,
    // and a protected drive that will not suspend stops the run BEFORE the
    // first boot-configuration write rather than after it. Pinned as order, not
    // as wording.
    [Fact]
    public void TheScriptGivesUpOnAFailedSuspendBeforeTheFirstBootConfigurationWrite()
    {
        var plan = SigningFix.BuildPlan(false, true, false, resuming: false);
        var script = SigningFix.BuildScript(plan, nonce: 1);

        var suspend = script.IndexOf("manage-bde.exe -protectors -disable", StringComparison.Ordinal);
        Assert.True(suspend >= 0, "the script never suspends BitLocker");

        // The failure branch of the suspend, pinned on the condition rather than
        // on the call: a branch that still contains the word Fail but no longer
        // tests the suspend's exit code is the exact regression this guards, so
        // the exit-code test is what has to come between the suspend and the
        // first write. "function Fail(" has no space, so "Fail (" is a call.
        var guard = script.IndexOf("if ($bdeExit -ne 0)", suspend, StringComparison.Ordinal);
        Assert.True(guard > suspend, $"suspend={suspend} is not checked for failure at all");
        var abort = script.IndexOf("Fail (", guard, StringComparison.Ordinal);
        Assert.True(abort > guard, $"the failed-suspend branch at {guard} does not give up");

        var hvci = script.IndexOf("Set-ItemProperty", StringComparison.Ordinal);
        var bcdedit = script.IndexOf("bcdedit.exe", StringComparison.Ordinal);
        Assert.True(hvci > abort && bcdedit > abort,
            $"abort={abort} hvci={hvci} bcdedit={bcdedit}");
    }

    [Fact]
    public void TheFirmwareScriptAsksForTheFirmwareRestartAndTouchesNoPolicy()
    {
        var plan = SigningFix.BuildPlan(true, true, false, resuming: false);
        var script = SigningFix.BuildScript(plan, nonce: 1);

        Assert.Contains("shutdown.exe /r /fw /t 0", script, StringComparison.Ordinal);
        Assert.Contains("manage-bde.exe -protectors -disable", script, StringComparison.Ordinal);
        Assert.DoesNotContain("bcdedit", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Name Enabled", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRevertScriptSuspendsFirstAndThenPutsTheValuesBack()
    {
        var plan = SigningFix.BuildRevertPlan(testSigningOn: true, hvciOn: false);
        var script = SigningFix.BuildScript(plan, nonce: 1);

        var suspend = script.IndexOf("manage-bde.exe -protectors -disable", StringComparison.Ordinal);
        var bcdedit = script.IndexOf("bcdedit.exe /set testsigning off", StringComparison.Ordinal);
        var hvci = script.IndexOf("-Name Enabled -Value 1", StringComparison.Ordinal);
        var shutdown = script.IndexOf("shutdown.exe /r /t 0", StringComparison.Ordinal);

        Assert.True(suspend >= 0 && bcdedit > suspend && hvci > bcdedit && shutdown > hvci,
            $"order was suspend={suspend} bcdedit={bcdedit} hvci={hvci} shutdown={shutdown}");
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
            SigningFix.BuildPlan(false, false, true, false), 1));
        Assert.Throws<InvalidOperationException>(() => SigningFix.BuildScript(
            SigningFix.BuildPlan(true, true, false, resuming: true), 1));
    }

    // The state is part of the gate and not decoration. Unavailable means the
    // fix has already sent this user to firmware once and Secure Boot is still
    // on; a record in that state carrying a plausible step list is a caller
    // that built it by hand, and running it would be the reboot loop the state
    // exists to stop.
    [Fact]
    public void AScriptIsRefusedForAPlanWhoseStateIsNotRunnableEvenWhenItsStepsLookFine()
    {
        foreach (var state in new[] { SigningFixState.Unavailable, SigningFixState.NothingToDo })
        {
            var plan = new SigningFixPlan(
                state,
                [SigningStep.SuspendBitLocker, SigningStep.EnableTestSigning, SigningStep.Reboot],
                "",
                state == SigningFixState.Unavailable ? "Secure Boot is still on" : null);

            Assert.Throws<InvalidOperationException>(() => SigningFix.BuildScript(plan, 1));
        }
    }

    // Each step is idempotent on its own, so a repeat is not dangerous by
    // itself - but it is proof the plan did not come from BuildPlan, and a
    // plan of unknown origin is exactly what this file runs nothing for.
    [Fact]
    public void AScriptIsRefusedForAPlanThatRepeatsAStep()
    {
        Assert.Throws<InvalidOperationException>(() => SigningFix.BuildScript(
            new SigningFixPlan(
                SigningFixState.Ready,
                [SigningStep.SuspendBitLocker, SigningStep.SuspendBitLocker, SigningStep.Reboot],
                "",
                null),
            1));
    }

    // Both file names cross the elevation boundary - this process writes them,
    // the elevated script rebuilds them from the same two segments - so the
    // nonce has to be a positive number on both sides. Zero is the value a
    // default-initialised counter has, which is the one that would collide
    // between two runs.
    [Fact]
    public void ANonPositiveNonceIsRefusedByBothFileNames()
    {
        foreach (long nonce in new long[] { 0, -1 })
        {
            Assert.Throws<InvalidOperationException>(() => SigningFix.ScriptPath(nonce));
            Assert.Throws<InvalidOperationException>(() => SigningFix.StatusSidecarPath(nonce));
        }

        Assert.Contains("-1.ps1", SigningFix.ScriptPath(1), StringComparison.Ordinal);
        Assert.Contains("-1.status", SigningFix.StatusSidecarPath(1), StringComparison.Ordinal);
    }

    // This script changes boot policy and nothing else, so it has no business
    // naming a driver installer, pnputil or a FLIP value - the same tripwire
    // DeviceEnable and DeviceRepair carry. Pinned as the property it protects:
    // the generated script names none of them.
    [Fact]
    public void TheScriptNamesNothingFromTheDriverInstallTripwire()
    {
        Assert.NotEmpty(DeviceEnable.ForbiddenNames);

        foreach (var plan in AllPlans())
        {
            if (plan.Steps.Count == 0)
                continue;
            var script = SigningFix.BuildScript(plan, nonce: 1);
            foreach (var name in DeviceEnable.ForbiddenNames)
                Assert.DoesNotContain(name, script, StringComparison.OrdinalIgnoreCase);
        }
    }
}
