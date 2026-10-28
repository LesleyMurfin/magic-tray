// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.Versioning;
using System.Text;

namespace MagicMouseTray;

// The automated driver-signing fix: ONE confirmation, ONE UAC prompt, then
// every step this PC actually needs, in order, without another question
// (AGENTS.md:76).
//
// Two parts on purpose, so every rule is testable without a PC: BuildPlan is
// pure - four readings and a resume flag in, an ordered step list and the exact
// words the user agrees to out - and RunAsync is the only code here that starts
// a process. So "nothing reboots this PC without a confirmed plan that said it
// would" is a property of two small functions rather than of every caller's
// discipline.
//
// The one thing in the fix that is NOT automatable, and the reason this file
// still has to send the user somewhere:
//
//   Secure Boot is firmware. There is no API, no registry value and no command
//   that turns it off from inside Windows, and while it is on `bcdedit /set
//   testsigning on` is refused outright with "The value is protected by Secure
//   Boot policy" (SystemConfigChecker.cs:421-425). The automated answer is
//   therefore `shutdown /r /fw /t 0` - restart straight
//   into this PC's own firmware setup screen, where the user flips it - and a
//   pending marker in Config so the tray can offer to finish the job when
//   Windows comes back. That is the whole of the NeedsFirmware branch.
//
// Everything else IS automatable and is done in one elevated pass: suspend
// BitLocker for exactly one reboot (so the 48-digit recovery key is never
// asked for), write HVCI Enabled=0, run bcdedit, restart.
//
// Note the deliberate split in the firmware branch: it carries ONLY the
// BitLocker suspend and the firmware restart. HVCI and testsigning are not in
// it, because with Secure Boot still on the bcdedit write cannot succeed - a
// plan that included it would be a plan that fails halfway on this machine.
// They arrive on the resume pass, after the user has turned Secure Boot off.
internal enum SigningStep
{
    // --- the forward fix. These five are the contract Main handed every agent
    // on this branch and their order here is load-bearing: FixUi's labels and
    // the resume check are written against these names.
    SuspendBitLocker,
    DisableHvci,
    EnableTestSigning,
    RebootToFirmware,
    Reboot,

    // --- the revert (BuildRevertPlan). Appended AFTER the five above so the
    // numeric value of every contract member stays what it was. The undo cannot
    // reuse the forward names - a script built from a step list has to know
    // whether it is writing Enabled=0 or Enabled=1 - and the plan record is
    // positional (callers deconstruct it), so the direction is carried in the
    // steps rather than in a sixth record field.
    DisableTestSigning,
    EnableHvci,
    ResumeBitLocker,
}

internal enum SigningFixState
{
    // Measured: Test Mode is already on and Memory integrity is already off.
    // Running anything would be a UAC prompt and a reboot for nothing.
    NothingToDo,

    // Every step in Steps can run right now, in one elevated pass.
    Ready,

    // Secure Boot is on. Steps stop at the firmware restart; the rest of the
    // fix needs the user to flip Secure Boot in firmware first.
    NeedsFirmware,

    // The fix cannot proceed and repeating it would be a loop: the user has
    // already been sent to firmware and Secure Boot is STILL on. BlockedReason
    // says so. Nothing to run.
    Unavailable,
}

internal sealed record SigningFixPlan(
    SigningFixState State,
    IReadOnlyList<SigningStep> Steps,   // ordered, only the steps this PC actually needs
    string Prompt,                      // the ONE confirmation: every step, the reboot, BitLocker, revert
    string? BlockedReason);

internal static class SigningFix
{
    internal const string TestSigningOnCommand = "bcdedit /set testsigning on";
    internal const string TestSigningOffCommand = "bcdedit /set testsigning off";

    internal const string HvciKeyPath =
        @"HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    // Status tokens the generated script writes, and the only ones the poller
    // below accepts. Same sidecar shape as DeviceRepair (DeviceRepair.cs:33-35,
    // 580-638), because ExitCode is unavailable once UseShellExecute is true.
    const string TokenRunning = "running";
    const string TokenOk = "ok";
    const string TokenFailed = "failed";

    // The elevated pass does registry writes, a bcdedit write and a shutdown
    // request. Two minutes is the same ceiling DeviceRepair uses
    // (DeviceRepair.cs:610) and it is what bounds a hung elevated process.
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    // Verbatim text from the failed step - bcdedit's own stderr, when a Secure
    // Boot refusal is what went wrong. Written by RunAsync only, cleared at the
    // start of every run, and read by the caller right after a
    // InstallOutcome.Failed so the error dialog can quote Windows rather than
    // paraphrase it.
    internal static string? LastFailureReason { get; private set; }

    // ---- plan ------------------------------------------------------------

    // Pure. No IO. Every rule about what this PC needs, in what order, and
    // every word the user is asked to agree to, is decided here.
    //
    // Readings are tri-state and null means "no evidence", never a fault:
    //
    //   secureBootOn == true        the bcdedit write cannot succeed, so the
    //                               plan is the firmware trip and nothing else.
    //                               null or false => the fix runs; only a
    //                               POSITIVE reading diverts: an unreadable
    //                               reading is the ordinary case on an
    //                               unelevated tray, and bcdedit reports the
    //                               refusal in its own words if it is wrong.
    //   hvciOn != false             DisableHvci is included. Unknown writes
    //                               Enabled=0 anyway: writing 0 over 0 is a
    //                               no-op, and skipping it on a PC that could
    //                               not be read would leave the fix incomplete.
    //   testSigningOn != true       EnableTestSigning is included. bcdedit
    //                               setting a flag that is already set is a
    //                               no-op, so an unreadable reading acts.
    //   bitLockerProtected != false SuspendBitLocker is included. Unknown
    //                               suspends: the suspend is harmless on a PC
    //                               with no BitLocker (the script tolerates the
    //                               cmdlet being absent) and skipping it on a
    //                               PC that could not be read is what makes
    //                               Windows demand the recovery key.
    //   resuming                    this is the pass after the firmware trip.
    //                               It only changes what happens when Secure
    //                               Boot is STILL on: first time that is the
    //                               firmware trip, second time it is
    //                               Unavailable rather than another trip.
    internal static SigningFixPlan BuildPlan(
        bool? secureBootOn, bool? hvciOn, bool? testSigningOn, bool? bitLockerProtected, bool resuming)
    {
        bool needHvci = hvciOn != false;
        bool needTestSigning = testSigningOn != true;
        bool needBitLocker = bitLockerProtected != false;

        // Measured-correct PC. Checked before Secure Boot, because a PC that
        // already has Test Mode on and Memory integrity off needs nothing -
        // including on a Secure Boot machine where testsigning was set before
        // Secure Boot was turned back on.
        if (!needHvci && !needTestSigning)
        {
            return new SigningFixPlan(
                SigningFixState.NothingToDo,
                [],
                "There is nothing to fix: this PC is already set up to load the mouse's "
                + "self-signed scroll driver.\n\n"
                + "WHAT HAPPENS NOW\n"
                + "- Nothing. Magic Tray will not ask for administrator and will not restart "
                + "this PC.\n\n"
                + "WHAT YOU NEED TO DO\n"
                + "- Nothing here. Test Mode is already on and Memory integrity (Core "
                + "isolation) is already off.\n"
                + "- If the scroll wheel still does not work, the problem is the driver or the "
                + "device itself, and the top row of this menu reports that.\n\n"
                + "TO UNDO\n"
                + "One click: \"Undo the driver signing fix\" on this same menu puts all of it "
                + "back (" + TestSigningOffCommand + ", Memory integrity back on, one restart).",
                null);
        }

        if (secureBootOn == true)
        {
            if (resuming)
            {
                // The user has already been sent to the firmware screen and
                // Secure Boot is still on - they could not find it, the
                // firmware would not let them, or they changed their mind.
                // Sending them back in would be a loop, and Magic Tray cannot
                // do it for them, so the fix stops here and says why.
                const string reason =
                    "Secure Boot is still on, so Test Mode still cannot be turned on. Secure Boot "
                    + "is a firmware setting and no program can change it from inside Windows.";
                return new SigningFixPlan(
                    SigningFixState.Unavailable,
                    [],
                    "The fix stopped: the one setting it needed from you is still on.\n\n"
                    + "WHAT HAPPENED\n"
                    + "- " + reason + "\n"
                    + "- Magic Tray already restarted this PC into the firmware setup screen "
                    + "once, and it will not restart this PC again on its own.\n\n"
                    + "WHAT YOU NEED TO DO, IF YOU WANT TO TRY AGAIN\n"
                    + "- Start \"Fix driver signing\" again from this menu: it restarts this PC "
                    + "into the firmware setup screen.\n"
                    + "- There, look in the Security menu first, then the Boot menu, for the "
                    + "setting named Secure Boot. That is the one that was missed.\n"
                    + "- Set Secure Boot to Disabled - not just saved, actually set to Disabled "
                    + "- then save and exit (F10 on most PCs).\n"
                    + "- Turning it off lowers this PC's boot security for as long as it stays "
                    + "off.\n\n"
                    + "WHAT THIS CHANGED ON THIS PC\n"
                    + "- Nothing, and nothing is left half-done: Memory integrity and Test Mode "
                    + "are exactly as they were.\n\n"
                    + "OK closes this and changes nothing.",
                    reason);
            }

            List<SigningStep> firmwareSteps = [];
            if (needBitLocker)
                firmwareSteps.Add(SigningStep.SuspendBitLocker);
            firmwareSteps.Add(SigningStep.RebootToFirmware);
            return new SigningFixPlan(
                SigningFixState.NeedsFirmware,
                firmwareSteps,
                FirmwarePrompt(firmwareSteps, bitLockerProtected),
                null);
        }

        List<SigningStep> steps = [];
        if (needBitLocker)
            steps.Add(SigningStep.SuspendBitLocker);
        if (needHvci)
            steps.Add(SigningStep.DisableHvci);
        if (needTestSigning)
            steps.Add(SigningStep.EnableTestSigning);
        steps.Add(SigningStep.Reboot);
        return new SigningFixPlan(
            SigningFixState.Ready,
            steps,
            ReadyPrompt(steps, testSigningOn, bitLockerProtected, resuming),
            null);
    }

    // The undo the charter requires to always be on offer (AGENTS.md:76):
    // Test Mode off, Memory integrity back on, BitLocker protection resumed,
    // one restart. It is never blocked by Secure Boot - turning test signing
    // OFF is always permitted by Windows, Secure Boot or not - so the undo is
    // available on every PC, including one whose forward fix is Unavailable.
    //
    // BitLocker is resumed unconditionally: Resume-BitLocker on a volume that
    // was never suspended is a no-op, and the alternative - leaving protection
    // suspended because a reading failed - is the one outcome that actually
    // costs the user something.
    internal static SigningFixPlan BuildRevertPlan(bool? testSigningOn, bool? hvciOn)
    {
        bool needTestSigning = testSigningOn != false;
        bool needHvci = hvciOn != true;

        if (!needTestSigning && !needHvci)
        {
            return new SigningFixPlan(
                SigningFixState.NothingToDo,
                [],
                "Nothing to undo. Test Mode is already off and Memory integrity (Core isolation) is "
                + "already on, so this PC is back to loading only drivers Windows trusts. Magic "
                + "Tray will not ask for administrator and will not restart this PC.",
                null);
        }

        List<SigningStep> steps = [];
        if (needTestSigning)
            steps.Add(SigningStep.DisableTestSigning);
        if (needHvci)
            steps.Add(SigningStep.EnableHvci);
        steps.Add(SigningStep.ResumeBitLocker);
        steps.Add(SigningStep.Reboot);
        return new SigningFixPlan(SigningFixState.Ready, steps, RevertPrompt(steps), null);
    }

    // ---- the one confirmation --------------------------------------------

    // Every user-facing string in this file has the same skeleton, because the
    // owner's complaint about the first version was precise: the text was
    // accurate but it buried the two things she needed - what the app does and
    // what SHE has to do. So:
    //
    //   headline  -> WHAT HAPPENS NOW -> WHAT YOU NEED TO DO
    //             -> BEFORE YOU SAY YES -> WHAT THIS CHANGES ON THIS PC
    //             -> TO UNDO -> what OK and Cancel mean.
    //
    // Every line in WHAT HAPPENS NOW is tagged with who does it, and the
    // commands stay in the text - they are the only way to check the app by
    // hand - but they sit at the END of their line, in brackets, so the plain
    // English leads and the identifier is a footnote rather than the subject.

    // Exactly the steps this PC will take, in the order they run, each one
    // short enough to scan and tagged with its actor. Built from the plan's
    // own step list so the dialog cannot drift from what the script does.
    static string StepText(SigningStep step) => step switch
    {
        SigningStep.SuspendBitLocker =>
            "Magic Tray: suspends BitLocker protection for one restart "
            + "(Suspend-BitLocker -RebootCount 1)",
        SigningStep.DisableHvci =>
            "Magic Tray: turns Memory integrity (Core isolation) off "
            + "(Enabled = 0 under HypervisorEnforcedCodeIntegrity)",
        SigningStep.EnableTestSigning =>
            "Magic Tray: turns Test Mode on (" + TestSigningOnCommand + ")",
        SigningStep.RebootToFirmware =>
            "Magic Tray: restarts this PC into the firmware setup screen (shutdown /r /fw)",
        SigningStep.Reboot =>
            "Magic Tray: restarts this PC, because the settings only take effect after a restart "
            + "(shutdown /r)",
        SigningStep.DisableTestSigning =>
            "Magic Tray: turns Test Mode off (" + TestSigningOffCommand + ")",
        SigningStep.EnableHvci =>
            "Magic Tray: turns Memory integrity (Core isolation) back on "
            + "(Enabled = 1 under HypervisorEnforcedCodeIntegrity)",
        SigningStep.ResumeBitLocker =>
            "Magic Tray: resumes BitLocker protection (Resume-BitLocker)",
        _ => "Magic Tray: an unrecognised step, which Magic Tray will not run",
    };

    // Numbered from 1 and never trailing a newline: sections are joined by
    // Block() below, so spacing is decided in one place.
    static string NumberedSteps(IReadOnlyList<SigningStep> steps, params string[] extra)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < steps.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(i + 1).Append(". ").Append(StepText(steps[i]));
        }
        for (int i = 0; i < extra.Length; i++)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(steps.Count + i + 1).Append(". ").Append(extra[i]);
        }
        return sb.ToString();
    }

    // One blank line between sections, no blank line inside one, and no
    // section that turned out empty.
    static string Block(List<string> sections)
    {
        sections.RemoveAll(string.IsNullOrEmpty);
        return string.Join("\n\n", sections);
    }

    // One UAC prompt, named out loud, because the user is agreeing to a
    // sequence and not to a command: the promise that nothing else will ask is
    // part of what they are approving.
    const string AdminOnce =
        "Windows will ask you ONCE to allow this as administrator. After that Magic Tray does "
        + "every step on its own and will not ask you anything else.";

    // The undo, as the one click it actually is. The commands behind it live
    // on the undo's own confirmation, which is where a user who wants to check
    // them is about to be.
    const string UndoSection =
        "TO UNDO\n"
        + "One click: \"Undo the driver signing fix\" on this same menu puts all of it back "
        + "(" + TestSigningOffCommand + ", Memory integrity back on with Enabled = 1, "
        + "Resume-BitLocker, one restart).";

    // IReadOnlyList has no Contains / IndexOf of its own, and these two run
    // over a list of at most four entries, so a plain scan is both the cheapest
    // and the clearest answer - no LINQ allocation on a UI path.
    static bool HasStep(IReadOnlyList<SigningStep> steps, SigningStep step)
    {
        for (int i = 0; i < steps.Count; i++)
            if (steps[i] == step)
                return true;
        return false;
    }

    // 1-based, so it matches the numbering the user is reading. 0 means the
    // step is not in this plan at all.
    static int StepNumber(IReadOnlyList<SigningStep> steps, SigningStep step)
    {
        for (int i = 0; i < steps.Count; i++)
            if (steps[i] == step)
                return i + 1;
        return 0;
    }

    // Unknown counts as protected: a sentence the user did not need costs
    // nothing, a missing one costs them their data. The difference here is that
    // the fix SUSPENDS BitLocker, so the sentence is a reassurance rather than
    // a warning - the recovery key is not asked for at all.
    static string BitLockerLines(IReadOnlyList<SigningStep> steps, bool? bitLockerProtected)
    {
        if (!HasStep(steps, SigningStep.SuspendBitLocker))
        {
            return "BitLocker is not protecting this PC's system drive, so no recovery key is "
                + "involved in any of this.";
        }

        var lead = bitLockerProtected == true
            ? "This PC is protected by BitLocker, so Magic Tray suspends it for one restart first."
            : "Magic Tray could not tell whether BitLocker protects this PC, so it suspends "
                + "BitLocker for one restart anyway.";
        return lead
            + "\nThat is what stops Windows asking for the 48-digit recovery key: you will not "
            + "need the key, and BitLocker turns itself back on after that one restart.";
    }

    // What the user is trading away, one line each, with the Memory integrity
    // line only when that step is in THIS plan - the firmware pass does not
    // touch it, and a dialog that claimed it did would be describing work that
    // is not going to happen yet.
    static string ChangesSection(IReadOnlyList<SigningStep> steps, bool firmware)
    {
        var sb = new StringBuilder("WHAT THIS CHANGES ON THIS PC\n");
        sb.Append("- A \"Test Mode\" watermark sits in the corner of the desktop. That is the "
            + "sign it worked.\n");
        sb.Append("- Windows will load drivers Microsoft has not checked, including this mouse's "
            + "self-signed driver, which is the point.\n");
        if (HasStep(steps, SigningStep.DisableHvci))
        {
            sb.Append("- Memory integrity (Core isolation) is off until you undo this "
                + "(Enabled = 0 under HypervisorEnforcedCodeIntegrity).\n");
        }
        if (firmware)
        {
            sb.Append("- Secure Boot stays off until you turn it back on, which lowers this PC's "
                + "boot security.\n");
        }
        sb.Append("- All of that is a real reduction in this PC's security for as long as Test "
            + "Mode stays on.");
        return sb.ToString();
    }

    static string ReadyPrompt(
        IReadOnlyList<SigningStep> steps, bool? testSigningOn, bool? bitLockerProtected, bool resuming)
    {
        var headline = resuming
            ? "Magic Tray can finish the driver signing fix now, and then the Magic Mouse scroll "
                + "wheel will work again."
            : "This makes the Magic Mouse scroll wheel work again on this PC.";

        // The unreadable-state sentence, on a fix that is going ahead anyway.
        // Acting and saying what was not known is the honest shape.
        var unknown = testSigningOn is null
            ? "Magic Tray could not read whether Test Mode is already on, so step "
                + StepNumber(steps, SigningStep.EnableTestSigning)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " may change nothing. Running it is safe either way."
            : string.Empty;

        return Block(
        [
            headline,
            "WHAT HAPPENS NOW\n" + NumberedSteps(steps),
            "WHAT YOU NEED TO DO\n"
            + "- Save your work and close anything you care about.\n"
            + "- " + (resuming ? "Answer Yes below." : "Click OK below.") + "\n"
            + "- Click Yes on the Windows administrator prompt, once.\n"
            + "- Let this PC restart. You do not have to do anything while it does.\n"
            + "- When Windows is back, try the scroll wheel.",
            "BEFORE YOU SAY YES\n"
            + "THIS PC WILL RESTART at the end of the list above, on its own and without asking "
            + "again. Save your work now.\n"
            + AdminOnce,
            BitLockerLines(steps, bitLockerProtected),
            unknown,
            ChangesSection(steps, firmware: false),
            UndoSection,
            resuming
                ? "Yes starts all of it now. No changes nothing at all."
                : "OK starts all of it now. Cancel changes nothing at all.",
        ]);
    }

    static string FirmwarePrompt(IReadOnlyList<SigningStep> steps, bool? bitLockerProtected)
    {
        return Block(
        [
            "This makes the Magic Mouse scroll wheel work again on this PC, but it needs one "
            + "thing from you first.",
            "Secure Boot has to be off, and that is the one part no program can do from inside "
            + "Windows: it lives in this PC's firmware (while it is on, " + TestSigningOnCommand
            + " is refused with \"The value is protected by Secure Boot policy\").",
            "WHAT HAPPENS NOW\n"
            + NumberedSteps(
                steps,
                "YOU: turn Secure Boot off on that screen, then save and exit",
                "YOU: answer Yes when Magic Tray asks to finish the job"),
            "WHAT YOU NEED TO DO\n"
            + "- Save your work and close anything you care about.\n"
            + "- Click OK below, then click Yes on the Windows administrator prompt, once.\n"
            + "- Wait: this PC restarts into the firmware (UEFI) setup screen instead of into "
            + "Windows.\n"
            + "- On that screen, find the setting named Secure Boot. It is usually in the "
            + "Security or the Boot menu.\n"
            + "- Set Secure Boot to Disabled.\n"
            + "- Save and exit (F10 on most PCs). Windows starts again by itself.\n"
            + "- When Windows is back, answer Yes to the one question Magic Tray asks, and it "
            + "finishes the rest on its own.\n"
            + "That screen belongs to your PC's manufacturer, not to Windows, so Magic Tray "
            + "cannot click it for you. That is why it has to be you.",
            "BEFORE YOU SAY YES\n"
            + "THIS PC WILL RESTART IMMEDIATELY, into the firmware setup screen instead of into "
            + "Windows. Save your work now.\n"
            + AdminOnce,
            BitLockerLines(steps, bitLockerProtected),
            ChangesSection(steps, firmware: true),
            UndoSection,
            "OK restarts this PC into the firmware screen now. Cancel changes nothing at all.",
        ]);
    }

    static string RevertPrompt(IReadOnlyList<SigningStep> steps)
    {
        return Block(
        [
            "This puts this PC back the way it was, before the driver signing fix.",
            "WHAT HAPPENS NOW\n" + NumberedSteps(steps),
            "WHAT YOU NEED TO DO\n"
            + "- Save your work and close anything you care about.\n"
            + "- Click OK below.\n"
            + "- Click Yes on the Windows administrator prompt, once.\n"
            + "- Let this PC restart. You do not have to do anything while it does.",
            "BEFORE YOU SAY YES\n"
            + "THIS PC WILL RESTART at the end of the list above, on its own. Save your work "
            + "now.\n"
            + AdminOnce,
            "WHAT THIS CHANGES ON THIS PC\n"
            + "- The \"Test Mode\" watermark is gone and Windows loads only drivers it trusts "
            + "again.\n"
            + "- Any self-signed driver stops loading: if this mouse was relying on one, scroll "
            + "stops working after the restart, while the pointer and the battery percent keep "
            + "working.\n"
            + "- BitLocker protection is resumed as part of this, so it cannot be left "
            + "suspended.",
            "TO REDO\n"
            + "One click: \"Fix driver signing\" on this same menu runs the fix again from the "
            + "start.",
            "OK starts the undo now. Cancel changes nothing at all.",
        ]);
    }

    // ---- run -------------------------------------------------------------

    static bool IsRebootStep(SigningStep step) =>
        step is SigningStep.Reboot or SigningStep.RebootToFirmware;

    // A revert is recognised by the steps that only a revert can contain. The
    // marker exists to say "a FORWARD fix is half-done", so the undo must never
    // set it, and finishing the undo clears it.
    static bool IsRevert(SigningFixPlan plan)
    {
        foreach (var step in plan.Steps)
            if (step is SigningStep.DisableTestSigning or SigningStep.EnableHvci or SigningStep.ResumeBitLocker)
                return true;
        return false;
    }

    // Every plan this method will run has to end in exactly one reboot, contain
    // no step twice, and contain no step this build cannot translate into
    // script. Anything else is a caller that built a plan by hand, and the
    // answer is to run nothing.
    static bool IsRunnable(SigningFixPlan plan)
    {
        if (plan.State is not (SigningFixState.Ready or SigningFixState.NeedsFirmware))
            return false;
        if (plan.Steps.Count == 0 || !IsRebootStep(plan.Steps[^1]))
            return false;
        var seen = new HashSet<SigningStep>();
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (!Enum.IsDefined(step) || !seen.Add(step))
                return false;
            if (IsRebootStep(step) && i != plan.Steps.Count - 1)
                return false;
        }
        return true;
    }

    // The pending-fix marker, written through the existing settings store
    // (Config.cs:12-14) and never through a second one. A fresh Config.Load()
    // each time on purpose: this runs on the thread pool, a long way from the
    // tray's own instance, and the file on disk is the thing the next process
    // reads after the reboot.
    static void Mark(bool pending)
    {
        try
        {
            Config.Load().SetSigningFixPending(pending);
            Logger.Log($"SIGNING_FIX_MARKER pending={pending.ToString().ToLowerInvariant()}");
        }
        catch (Exception ex)
        {
            // A marker that could not be written costs the resume prompt, not
            // the fix: the steps still run and the next config read simply sees
            // nothing pending.
            Logger.Log($"SIGNING_FIX_MARKER_FAILED err=\"{ex.Message}\"");
        }
    }

    internal static string StatusSidecarPath(long nonce) =>
        Path.Combine(Path.GetTempPath(), $"mm-signing-{ProcIdSegment()}-{ValidateNonce(nonce)}.status");

    internal static string ScriptPath(long nonce) =>
        Path.Combine(Path.GetTempPath(), $"mm-signing-{ProcIdSegment()}-{ValidateNonce(nonce)}.ps1");

    // Both segments cross the elevation boundary as part of a file name on both
    // sides, so both are rendered as plain digits and nothing else - same rule
    // and same reason as DeviceRepair.cs:114-130.
    static string ValidateNonce(long nonce)
    {
        if (nonce <= 0)
            throw new InvalidOperationException($"SigningFix needs a positive attempt nonce, got {nonce}.");
        return nonce.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    static string ProcIdSegment()
    {
        var procId = Environment.ProcessId;
        if (procId <= 0)
            throw new InvalidOperationException($"SigningFix needs a positive process id, got {procId}.");
        return procId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // ONE script for the whole plan, because one script is what makes one UAC
    // prompt possible: every elevated action is in here, in plan order, and
    // nothing between them can stop to ask a question.
    //
    // Failure policy, step by step:
    //   BitLocker  tolerated. A PC with no BitLocker (Home edition, or an
    //              unencrypted volume) has no Suspend-BitLocker cmdlet and
    //              nothing to suspend, which is not a fault - the status line
    //              records whether it happened and the run continues.
    //   HVCI       hard. If Memory integrity cannot be turned off, the driver
    //              will not load even with Test Mode on, so finishing the rest
    //              would be finishing a fix that does not work.
    //   bcdedit    hard, and its output is kept verbatim: a Secure Boot refusal
    //              is exactly this exit code plus exactly that sentence, and
    //              paraphrasing it would hide the one message that explains the
    //              whole situation.
    //   shutdown   last, always. The status file is written BEFORE the shutdown
    //              request, because Windows can tear this process down the
    //              moment the request is accepted; if the request itself is
    //              refused the status is overwritten with the refusal.
    internal static string BuildScript(SigningFixPlan plan, long nonce)
    {
        if (!IsRunnable(plan))
            throw new InvalidOperationException($"SigningFix refuses plan state={plan.State} steps={plan.Steps.Count}.");

        var sb = new StringBuilder();
        sb.Append(
            """
            $ErrorActionPreference = 'Continue'
            $procId = '__PROCID__'
            $nonce = '__NONCE__'
            $statusFile = Join-Path $env:TEMP ('mm-signing-' + $procId + '-' + $nonce + '.status')
            $hvciKey = '__HVCIKEY__'
            function Write-Status([string]$token, [string]$detail) {
                ($token + "`r`n" + $detail) | Set-Content -LiteralPath $statusFile -Encoding UTF8
            }
            Write-Status '__RUNNING__' ''

            """);

        foreach (var step in plan.Steps)
        {
            sb.Append('\n').Append(ScriptFor(step));
        }

        var script = sb.ToString()
            .Replace("__PROCID__", ProcIdSegment(), StringComparison.Ordinal)
            .Replace("__NONCE__", ValidateNonce(nonce), StringComparison.Ordinal)
            .Replace("__HVCIKEY__", HvciKeyPath, StringComparison.Ordinal)
            .Replace("__RUNNING__", TokenRunning, StringComparison.Ordinal)
            .Replace("__OK__", TokenOk, StringComparison.Ordinal)
            .Replace("__FAILED__", TokenFailed, StringComparison.Ordinal);

        // This script has no business naming a driver installer, pnputil or a
        // FLIP value - it changes boot policy and nothing else - so the same
        // guard DeviceRepair.cs:289-293 uses is kept as a tripwire.
        foreach (var name in DeviceEnable.ForbiddenNames)
        {
            if (script.Contains(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SigningFix refuses {name}.");
        }
        return script;
    }

    static string ScriptFor(SigningStep step) => step switch
    {
        SigningStep.SuspendBitLocker =>
            """
            Write-Host 'SIGNING_FIX_STEP suspend-bitlocker'
            $suspended = $false
            try {
                $null = Get-Command Suspend-BitLocker -ErrorAction Stop
                Suspend-BitLocker -MountPoint $env:SystemDrive -RebootCount 1 -ErrorAction Stop | Out-Null
                $suspended = $true
            } catch {
                Write-Host ('SIGNING_FIX_BITLOCKER skipped err="' + $_.Exception.Message + '"')
            }
            Write-Host "SIGNING_FIX_BITLOCKER suspended=$suspended"

            """,

        SigningStep.ResumeBitLocker =>
            """
            Write-Host 'SIGNING_FIX_STEP resume-bitlocker'
            $resumed = $false
            try {
                $null = Get-Command Resume-BitLocker -ErrorAction Stop
                Resume-BitLocker -MountPoint $env:SystemDrive -ErrorAction Stop | Out-Null
                $resumed = $true
            } catch {
                Write-Host ('SIGNING_FIX_BITLOCKER resume-skipped err="' + $_.Exception.Message + '"')
            }
            Write-Host "SIGNING_FIX_BITLOCKER resumed=$resumed"

            """,

        SigningStep.DisableHvci =>
            """
            Write-Host 'SIGNING_FIX_STEP disable-hvci'
            try {
                if (-not (Test-Path -LiteralPath $hvciKey)) {
                    New-Item -Path $hvciKey -Force -ErrorAction Stop | Out-Null
                }
                Set-ItemProperty -LiteralPath $hvciKey -Name Enabled -Value 0 -Type DWord -ErrorAction Stop
                Write-Host 'SIGNING_FIX_HVCI enabled=0'
            } catch {
                Write-Host ('SIGNING_FIX_HVCI failed err="' + $_.Exception.Message + '"')
                Write-Status '__FAILED__' ('Memory integrity could not be turned off: ' + $_.Exception.Message)
                exit 1
            }

            """,

        SigningStep.EnableHvci =>
            """
            Write-Host 'SIGNING_FIX_STEP enable-hvci'
            try {
                if (-not (Test-Path -LiteralPath $hvciKey)) {
                    New-Item -Path $hvciKey -Force -ErrorAction Stop | Out-Null
                }
                Set-ItemProperty -LiteralPath $hvciKey -Name Enabled -Value 1 -Type DWord -ErrorAction Stop
                Write-Host 'SIGNING_FIX_HVCI enabled=1'
            } catch {
                Write-Host ('SIGNING_FIX_HVCI failed err="' + $_.Exception.Message + '"')
                Write-Status '__FAILED__' ('Memory integrity could not be turned back on: ' + $_.Exception.Message)
                exit 1
            }

            """,

        // 2>&1 | Out-String keeps bcdedit's own words. On a Secure Boot machine
        // this is "The value is protected by Secure Boot policy." and it is the
        // single most useful line in the whole run, so it is handed back
        // unedited.
        SigningStep.EnableTestSigning =>
            """
            Write-Host 'SIGNING_FIX_STEP enable-testsigning'
            $bcdOut = (& bcdedit.exe /set testsigning on 2>&1 | Out-String).Trim()
            $bcdExit = $LASTEXITCODE
            Write-Host "SIGNING_FIX_BCDEDIT exit=$bcdExit"
            Write-Host $bcdOut
            if ($bcdExit -ne 0) {
                Write-Status '__FAILED__' ("bcdedit /set testsigning on failed (exit $bcdExit): " + $bcdOut)
                exit 1
            }

            """,

        SigningStep.DisableTestSigning =>
            """
            Write-Host 'SIGNING_FIX_STEP disable-testsigning'
            $bcdOut = (& bcdedit.exe /set testsigning off 2>&1 | Out-String).Trim()
            $bcdExit = $LASTEXITCODE
            Write-Host "SIGNING_FIX_BCDEDIT exit=$bcdExit"
            Write-Host $bcdOut
            if ($bcdExit -ne 0) {
                Write-Status '__FAILED__' ("bcdedit /set testsigning off failed (exit $bcdExit): " + $bcdOut)
                exit 1
            }

            """,

        SigningStep.RebootToFirmware =>
            """
            Write-Host 'SIGNING_FIX_STEP reboot-to-firmware'
            Write-Status '__OK__' 'Restarting into the firmware setup screen.'
            $shutOut = (& shutdown.exe /r /fw /t 0 2>&1 | Out-String).Trim()
            $shutExit = $LASTEXITCODE
            Write-Host "SIGNING_FIX_SHUTDOWN fw=1 exit=$shutExit"
            Write-Host $shutOut
            if ($shutExit -ne 0) {
                Write-Status '__FAILED__' ("shutdown /r /fw failed (exit $shutExit): " + $shutOut)
                exit 1
            }
            exit 0

            """,

        SigningStep.Reboot =>
            """
            Write-Host 'SIGNING_FIX_STEP reboot'
            Write-Status '__OK__' 'Restarting.'
            $shutOut = (& shutdown.exe /r /t 0 2>&1 | Out-String).Trim()
            $shutExit = $LASTEXITCODE
            Write-Host "SIGNING_FIX_SHUTDOWN fw=0 exit=$shutExit"
            Write-Host $shutOut
            if ($shutExit -ne 0) {
                Write-Status '__FAILED__' ("shutdown /r failed (exit $shutExit): " + $shutOut)
                exit 1
            }
            exit 0

            """,

        _ => throw new InvalidOperationException($"SigningFix has no script for step {step}."),
    };

    // One elevated pass for the whole plan. The caller has already shown
    // plan.Prompt and the user said yes; from here nothing asks anything.
    [SupportedOSPlatform("windows")]
    internal static async Task<InstallOutcome> RunAsync(SigningFixPlan plan, CancellationToken ct = default)
    {
        LastFailureReason = null;

        if (!IsRunnable(plan))
        {
            // Not Failed: nothing went wrong, there is simply nothing this PC
            // needs or nothing it can do - which is what Unavailable means
            // (DriverInstaller.cs:11-16).
            Logger.Log($"SIGNING_FIX_REFUSED state={plan.State} steps={plan.Steps.Count}");
            return InstallOutcome.Unavailable;
        }

        var revert = IsRevert(plan);
        var needsResume = HasStep(plan.Steps, SigningStep.RebootToFirmware);
        var stepList = string.Join(",", plan.Steps);
        Logger.Log($"SIGNING_FIX_RUN state={plan.State} revert={revert.ToString().ToLowerInvariant()} steps={stepList}");

        // The marker goes down BEFORE the elevated pass, because the pass ends
        // in a restart: once shutdown is accepted there is no later moment in
        // this process that is guaranteed to run. A forward fix that gets
        // interrupted is then still resumable, and a fix that completes clears
        // it below.
        if (!revert)
            Mark(true);

        var nonce = AttemptNonce.Next();
        var script = BuildScript(plan, nonce);
        var scriptPath = ScriptPath(nonce);
        var statusPath = StatusSidecarPath(nonce);

        (bool Started, bool Declined, string Token, string Detail) run;
        try
        {
            // The launch blocks until the elevated script exits or the sidecar
            // settles, so it goes on the thread pool and the UI thread stays
            // free. The UAC prompt is modal to the user, not to us.
            run = await Task.Run(() => LaunchElevated(scriptPath, script, statusPath), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The tray is shutting down or the caller gave up waiting. The
            // elevated script may still be running, so the marker is left where
            // it is: a half-done fix that nobody cleared is exactly what the
            // marker is for.
            Logger.Log("SIGNING_FIX_RESULT outcome=Cancelled reason=token");
            return InstallOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            LastFailureReason = ex.Message;
            Logger.Log($"SIGNING_FIX_RESULT outcome=Failed reason=launch err=\"{ex.Message}\"");
            if (!revert)
                Mark(false);
            return InstallOutcome.Failed;
        }

        if (run.Declined)
        {
            // The user said no at the UAC prompt (Win32 1223,
            // DriverInstaller.cs:959-960). Nothing ran, so nothing is pending
            // and the caller must not show an error for a decision the user
            // made.
            Logger.Log("SIGNING_FIX_RESULT outcome=Cancelled reason=uac-declined");
            if (!revert)
                Mark(false);
            return InstallOutcome.Cancelled;
        }

        if (!run.Started || run.Token != TokenOk)
        {
            // Everything else is a failure the user needs the words for: the
            // script never started, a step refused, or the sidecar never
            // settled. bcdedit's refusal arrives here verbatim.
            LastFailureReason = run.Detail.Length > 0
                ? run.Detail
                : (run.Started
                    ? "The elevated step did not report a result. Nothing may have been changed."
                    : "Windows did not start the elevated step, so nothing was changed.");
            Logger.Log(
                $"SIGNING_FIX_RESULT outcome=Failed started={run.Started.ToString().ToLowerInvariant()} "
                + $"token={(run.Token.Length == 0 ? "none" : run.Token)} detail=\"{LastFailureReason}\"");
            if (!revert)
                Mark(false);
            return InstallOutcome.Failed;
        }

        // Confirmed means every configuration step applied and the restart was
        // requested; the PC is on its way down as this returns. The marker
        // survives only when the firmware trip is what was requested, because
        // that is the one plan that leaves work for after the reboot.
        if (!needsResume)
            Mark(false);
        Logger.Log(
            $"SIGNING_FIX_RESULT outcome=Confirmed reboot={(needsResume ? "firmware" : "normal")} "
            + $"pending={(needsResume && !revert).ToString().ToLowerInvariant()}");
        return InstallOutcome.Confirmed;
    }

    // Same elevation shape as DeviceRepair.LaunchElevated (DeviceRepair.cs:
    // 580-638): write the script, ShellExecute it with Verb=runas, and poll the
    // status sidecar because ExitCode is unavailable once UseShellExecute is
    // true. Two differences, both forced by the reboot:
    //   - Declined is reported separately. Process.Start raises Win32 1223 when
    //     the UAC prompt is dismissed, and that is a user decision, not a fault.
    //   - the sidecar carries a detail block after the token, so bcdedit's
    //     refusal can be quoted back to the user word for word.
    static (bool Started, bool Declined, string Token, string Detail) LaunchElevated(
        string scriptPath, string script, string statusPath)
    {
        try { File.Delete(statusPath); } catch { /* ignore */ }
        File.WriteAllText(scriptPath, script);
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Path.GetTempPath(),
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (DriverInstaller.IsUacDeclined(ex)) { return (false, true, "", ""); }
        catch (System.ComponentModel.Win32Exception ex) { return (false, false, "", ex.Message); }
        if (p is null)
            return (false, false, "", "");

        var token = "";
        var detail = "";
        using (p)
        {
            var started = DateTime.UtcNow;
            var until = started + RunTimeout;
            while (DateTime.UtcNow < until)
            {
                if (File.Exists(statusPath))
                {
                    try
                    {
                        var text = File.ReadAllText(statusPath);
                        var split = text.IndexOf('\n');
                        token = (split < 0 ? text : text[..split]).Trim();
                        detail = split < 0 ? "" : text[(split + 1)..].Trim();
                    }
                    catch { token = ""; detail = ""; }
                    if (token is TokenOk or TokenFailed)
                        break;
                }
                try
                {
                    // Cancelled at the prompt that ShellExecute itself did not
                    // report, or a script that died before writing anything.
                    if (p.HasExited
                        && token.Length == 0
                        && DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
                        break;
                }
                catch { /* UseShellExecute */ }
                Thread.Sleep(150);
            }
            if (!p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
        }
        return (true, false, token, detail);
    }

    // Is the system drive BitLocker-protected? Only used to decide whether the
    // plan carries SuspendBitLocker and which BitLocker sentence the one
    // confirmation shows, and BOTH treat null like true - so a failure here can
    // only ever make the fix more careful, never less.
    //
    // Win32_EncryptableVolume lives in root\CIMV2\Security\MicrosoftVolumeEncryption
    // and is readable only by an administrator, so an unelevated tray - the
    // normal case, since TrayApp takes this reading without elevation - gets an
    // access-denied exception and returns null. That is the whole reason null
    // must still suspend: skipping the suspend on a PC that could not be read
    // is what makes Windows demand the 48-digit recovery key after the reboot.
    //
    // ProtectionStatus: 0 unprotected, 1 protected, 2 unknown/locked.
    [SupportedOSPlatform("windows")]
    internal static bool? ReadBitLockerProtected()
    {
        try
        {
            var systemRoot = Path.GetPathRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrEmpty(systemRoot) || systemRoot.Length < 2)
                return null;

            // "C:\" -> "C:", which is how DriveLetter is stored.
            var driveLetter = systemRoot[..2];

            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
            var query = new ObjectQuery(
                "SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '"
                + driveLetter + "'");
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();
            foreach (var o in results)
            {
                using var volume = (ManagementObject)o;
                var status = volume["ProtectionStatus"];
                if (status is null)
                    continue;
                var value = Convert.ToUInt32(status, System.Globalization.CultureInfo.InvariantCulture);
                if (value == 0)
                    return false;
                if (value == 1)
                    return true;
                return null; // 2 = unknown, e.g. a locked volume.
            }

            // No row for the system drive at all: BitLocker is not available on
            // this edition / this volume is not encryptable. Nothing to suspend,
            // but nothing proven either - and null suspends, which is the safe
            // direction.
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"SIGNING_FIX_BITLOCKER_READ_FAILED err=\"{ex.Message}\"");
            return null;
        }
    }
}
