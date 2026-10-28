// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// How a ConfigFact is allowed to look in the menu and in a dialog. Pure text:
// no WinForms type appears here, so every string this repo ever shows about the
// configuration of the PC is testable off-device.
//
// The division of labour with SystemConfigChecker is deliberate and one-way.
// The checker owns the judgement (what is Blocking, what an unreadable reading
// may claim) and writes the prose that states what is wrong, what it costs the
// user and the exact steps. This file owns only the shape: which line collapses
// the section, how severity is spelled, and what wrapping a dialog puts around
// the checker's own words. It NEVER paraphrases a Detail - a view layer that
// re-words driver steps is how a UI starts lying about them.
//
// PRECEDENCE, and why a config fact can never bury a device fault
// ---------------------------------------------------------------
// A RepairPlanner finding is a measured fault on the device in front of the
// user right now: the pointer child is missing, the filter is bound but
// stopped, two filters are registered (RepairPlanner.cs:5-17). A ConfigFact is
// a property of the PC that at most EXPLAINS such a fault - Test Mode off does
// not break anything by itself, it only means Windows will refuse to load a
// self-signed driver if one is there. So the device fault is the more specific,
// more present claim and always wins the top of the menu.
//
// That is enforced structurally rather than by a number this file could get
// wrong:
//   - RepairPlanner.MenuLabel owns the first row and speaks in the bare fault
//     voice ("Scroll driver is not installed", "2 problems found",
//     RepairPlanner.cs:453-458, rendered at TrayApp.cs:408-412);
//   - every string SectionLabel can return is prefixed with "System config: ",
//     so a config line is never readable as that headline no matter how severe
//     the fact behind it is. Rank below orders facts only WITHIN this section.
internal static class ConfigFactView
{
    internal const string SectionPrefix = "System config";

    // Lower sorts first. Blocking before Advisory because only Blocking has a
    // positive reading behind it that this repo has measured to break the
    // driver in use; Advisory covers both "worth checking" and "we could not
    // read it", and an unreadable reading must never push a measured one down
    // the list. Ok sorts last: it is shown so the user can see the check ran,
    // not because it needs attention.
    internal static int Rank(ConfigSeverity severity) => severity switch
    {
        ConfigSeverity.Blocking => 0,
        ConfigSeverity.Advisory => 1,
        _ => 2,
    };

    // The facts in display order. Exposed alongside Rows because the caller
    // needs the fact behind each row to wire its dialog, and a pre-sorted list
    // of strings cannot be mapped back. Stable within a rank, so the checker's
    // own reading order (Test Mode, Memory integrity, package, watcher) is kept.
    internal static IReadOnlyList<ConfigFact> Ordered(IReadOnlyList<ConfigFact> facts)
    {
        var ordered = new List<ConfigFact>(facts.Count);
        for (int rank = 0; rank <= 2; rank++)
        {
            for (int i = 0; i < facts.Count; i++)
            {
                if (Rank(facts[i].Severity) == rank)
                    ordered.Add(facts[i]);
            }
        }
        return ordered;
    }

    // The collapsed one-line summary for the menu, or null when there is
    // nothing worth a line.
    //
    // An empty list is null: no fact at all means nothing about this PC was
    // checked (a stock-Windows mouse, a device we do not know, or a Check that
    // failed and correctly returned nothing), and a row saying "checked" would
    // be claiming a verification that never happened.
    //
    // All-Ok collapses to a reassuring line rather than to null, for the same
    // reason RepairPlanner.MenuLabel says "No problems found" instead of hiding
    // its row: an absent row is indistinguishable from a tray that never
    // looked, so the one case where the user is entitled to be told everything
    // passed would be the case that renders as silence. The wording carries no
    // count, no severity word and nothing negated, so it cannot be misread as a
    // warning at a glance.
    internal static string? SectionLabel(IReadOnlyList<ConfigFact> facts)
    {
        if (facts.Count == 0)
            return null;

        int blocking = 0, advisory = 0;
        for (int i = 0; i < facts.Count; i++)
        {
            if (facts[i].Severity == ConfigSeverity.Blocking)
                blocking++;
            else if (facts[i].Severity == ConfigSeverity.Advisory)
                advisory++;
        }

        // One offender: name it, the way the repair row names a single finding.
        // Several: count them, because two titles do not fit one menu row and a
        // truncated one would hide the other.
        if (blocking > 0)
        {
            // Only the two signing-policy facts can reach Blocking today
            // (SystemConfigChecker.cs:255, 300), so "settings" is accurate; the
            // singular branch prints the title and needs no such assumption.
            return blocking == 1
                ? $"{SectionPrefix}: {Ordered(facts)[0].Title}"
                : $"{SectionPrefix}: {blocking} settings on this PC are blocking this driver";
        }

        if (advisory > 0)
        {
            return advisory == 1
                ? $"{SectionPrefix}: {Ordered(facts)[0].Title}"
                : $"{SectionPrefix}: {advisory} things worth checking";
        }

        return $"{SectionPrefix}: all checks passed";
    }

    // One display line per fact, most severe first. Severity is spelled as a
    // word: the console and the tray's own menu font mangle glyphs, and the
    // capability rows next to these already carry their state in words
    // ("Scroll: not working - ...", DeviceCapability.cs:113-156).
    internal static IReadOnlyList<string> Rows(IReadOnlyList<ConfigFact> facts)
    {
        var ordered = Ordered(facts);
        var rows = new List<string>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
            rows.Add(Row(ordered[i]));
        return rows;
    }

    internal static string Row(ConfigFact fact) => $"{SeverityWord(fact.Severity)}: {fact.Title}";

    // "Blocking" states a measured consequence, not a verdict on the user's
    // choice; "Check" asks rather than accuses, which is what an Advisory - and
    // an unreadable reading in particular - is entitled to do; "OK" is the
    // shortest thing that cannot be mistaken for a complaint.
    internal static string SeverityWord(ConfigSeverity severity) => severity switch
    {
        ConfigSeverity.Blocking => "Blocking",
        ConfigSeverity.Advisory => "Check",
        _ => "OK",
    };

    // The body shown when the user clicks a fact. Title, then the checker's own
    // prose verbatim, then who owns the fix, then what the buttons do.
    internal static string DialogText(ConfigFact fact)
    {
        var blocks = new List<string>(4) { fact.Title };

        if (fact.Severity == ConfigSeverity.Ok)
        {
            // A passing check gets no "wrong" heading and no owner: there is no
            // fix to own. Without this line an Ok dialog would read like a
            // warning whose problem statement went missing.
            blocks.Add(fact.Detail);
            blocks.Add("Nothing to do here - this line is shown so you can see the check ran.");
            return string.Join("\n\n", blocks);
        }

        blocks.Add("What is wrong, what it costs you, and the steps:\n" + fact.Detail);
        blocks.Add("Who fixes this:\n" + OwnerLine(fact.Id));

        var footer = ActionFooter(fact);
        if (footer is not null)
            blocks.Add(footer);

        return string.Join("\n\n", blocks);
    }

    // Ownership is derived from the fact id, never from the Detail text, so it
    // holds for every fact the checker can grow later and cannot be lost by a
    // rewording.
    internal static string OwnerLine(string factId) => factId switch
    {
        // The signing prerequisites are now ONE user-approved action that then
        // runs by itself (AGENTS.md:76), so this line promises the shape of
        // that action rather than a blanket "changes nothing": the single
        // confirmation that lists every step, the one administrator prompt, the
        // reboot, and the undo. Dropping any of those four turns a confirmed
        // sequence back into a silent one, which is why each is spelled out
        // here and pinned in ConfigFactViewTests.
        SystemConfigChecker.TestModeFactId =>
            "You decide once, and Magic Tray carries it out. " + SigningFixLabel + " runs every "
            + "step this PC needs in one administrator action after the single confirmation that "
            + "lists them: suspend BitLocker for one restart, turn Memory integrity off, run "
            + "bcdedit /set testsigning on, and reboot. One question, one administrator prompt, "
            + "nothing asked after it unprompted - never on a timer, never without that answer. "
            + "The one thing that can begin at launch is the single question to finish a fix you "
            + "already confirmed before a firmware restart, and only because that answer left it "
            + "half done. Secure Boot it cannot touch: that is firmware, so the fix restarts this "
            + "PC into the firmware screen and you turn it off there. The undo is offered "
            + "whenever there is anything to undo.",

        // Memory integrity is no longer a hand-operated setting this tray only
        // reads: the one confirmed sequence writes the HVCI policy value, so
        // the old "Magic Tray never changes Memory integrity" promise would now
        // be false and must never come back. What replaces it is the narrower
        // true promise - only inside that sequence, only after the user's one
        // answer, and put back by the undo - plus the by-hand route for a user
        // who would rather do it herself.
        SystemConfigChecker.MemoryIntegrityFactId =>
            "You, or Magic Tray with your permission. Memory integrity (HVCI) makes Windows "
            + "refuse a self-signed driver, so " + SigningFixLabel + " can turn it off for you - "
            + "only as part of the one confirmed sequence that lists every step, never on a "
            + "timer, and never unprompted at startup: the only thing launch can raise is the "
            + "question to finish a fix you already confirmed. The undo turns it back on. By "
            + "hand it lives in Windows Security -> Device security -> Core isolation.",

        // Secure Boot keeps the absolute promise, because it is the one setting
        // here that no program can change: it lives in firmware, and
        // `bcdedit /set testsigning on` is refused while it is on with "The
        // value is protected by Secure Boot policy". The most the fix does is
        // restart this PC into the firmware setup screen, and only after the
        // confirmation that says it will.
        SystemConfigChecker.SecureBootFactId =>
            "You, in this PC's firmware. Secure Boot is not a Windows setting - no program can "
            + "turn it off from inside Windows, and Magic Tray never tries. The most it does is "
            + "restart this PC into the firmware setup screen, after you confirm that restart, so "
            + "you can turn Secure Boot off there yourself.",

        SystemConfigChecker.DriverPackageFactId =>
            "The driver package. Magic Tray offers the install from its own driver step, and "
            + "nothing is installed until you confirm it there.",

        // Fault B in docs/ENABLE-DISABLE.md:93-99. The Apple multitouch enable
        // FEATURE report is owned by the driver package's watcher, and a second
        // sender inside Magic Tray is an explicit NON-GOAL (RepairPlanner.cs:28-34).
        // So this line names the package as the owner and must never grow into
        // an instruction for the tray to send the report itself.
        SystemConfigChecker.WatcherFactId =>
            "The driver package, not Magic Tray. The Apple multitouch enable report {F1,02,01} is "
            + "sent by the package's own watcher, mm-auto-f1-watcher.ps1, and that watcher is the "
            + "only thing allowed to send it. Magic Tray never sends the report and will not add a "
            + "second sender, so a watcher that is not running is fixed in the "
            + DriverPackageCatalog.V3RepoName + " driver package.",

        _ =>
            "You, on this PC. Magic Tray reports what it read here and changes nothing on its own.",
    };

    // ActionUrlOrScript only ever NAMES a page or a script that already ships
    // in a package; the caller opens it and never runs it, so this footer only
    // ever promises to open something.
    static string? ActionFooter(ConfigFact fact)
    {
        if (string.IsNullOrEmpty(fact.ActionLabel) || string.IsNullOrEmpty(fact.ActionUrlOrScript))
            return null;

        const string Open = "Open ";
        var opens = fact.ActionLabel.StartsWith(Open, StringComparison.Ordinal)
            ? "OK opens " + fact.ActionLabel.Substring(Open.Length)
            : "OK: " + fact.ActionLabel;
        return opens + ". Cancel changes nothing.";
    }

    // ---- the one action this view may offer -------------------------------
    //
    // Everything below renders a SigningFixPlan (SigningFix.cs), built pure
    // from the readings taken at the moment of the click. The view never
    // re-derives whether the sequence is possible: SigningFixOffersRun is the
    // single gate every caller asks, so "may this click reach the elevated
    // script" is decided in one testable place instead of once per dialog, and
    // a plan the planner refused cannot be talked back into running by a
    // wording change here.
    //
    // Exactly ONE confirmation stands between a click and the whole sequence,
    // and it is plan.Prompt - the planner's own words, shown verbatim by the
    // caller. Nothing in this file may add a second question to the fix path:
    // every text below is either that prompt's replacement for a resumed fix
    // (SigningFixResumeText) or an after-the-fact report.

    internal const string SigningFixLabel = "Fix driver signing";
    internal const string SigningFixRevertLabel = "Undo the driver signing fix";

    // The Blocking facts, in display order. These are the configuration
    // problems that belong in the "problems found" answer next to the device
    // faults (TrayApp.ShowRepairFlow). Advisory never qualifies: it covers both
    // "worth checking" and "we could not read it", and neither is a measured
    // problem the user can be told to fix (Rank above, and the precedence note
    // at the top of this file).
    internal static IReadOnlyList<ConfigFact> Blocking(IReadOnlyList<ConfigFact> facts)
    {
        List<ConfigFact>? blocking = null;
        var ordered = Ordered(facts);
        for (var i = 0; i < ordered.Count; i++)
        {
            // Ordered puts Blocking first (Rank == 0), so the first non-Blocking
            // entry ends the run and the rest of the list needs no visit.
            if (ordered[i].Severity != ConfigSeverity.Blocking)
                break;
            (blocking ??= new List<ConfigFact>(2)).Add(ordered[i]);
        }
        return (IReadOnlyList<ConfigFact>?)blocking ?? Array.Empty<ConfigFact>();
    }

    // The Blocking facts as rows for the top-of-menu problem count
    // (RepairPlanner.MenuLabel). Each stays behind the section prefix, so a
    // configuration line can never be read as the bare device-fault voice that
    // row owns even when it is the only problem standing - the same structural
    // rule SectionLabel follows, and the reason the precedence note at the top
    // of this file does not need a severity number to hold.
    internal static IReadOnlyList<string> ProblemTitles(IReadOnlyList<ConfigFact> facts)
    {
        var blocking = Blocking(facts);
        if (blocking.Count == 0)
            return Array.Empty<string>();
        var titles = new List<string>(blocking.Count);
        for (var i = 0; i < blocking.Count; i++)
            titles.Add($"{SectionPrefix}: {blocking[i].Title}");
        return titles;
    }

    // Whether the elevated sequence may be offered at all. Ready and
    // NeedsFirmware are the two states with real work behind them; NothingToDo
    // and Unavailable each get a dialog that runs nothing. The empty-step guard
    // is belt and braces: a plan with no steps would show a confirmation
    // listing nothing and then elevate for nothing.
    internal static bool SigningFixOffersRun(SigningFixPlan plan) =>
        (plan.State == SigningFixState.Ready || plan.State == SigningFixState.NeedsFirmware)
        && plan.Steps.Count > 0;

    internal static bool HasStep(SigningFixPlan plan, SigningStep step)
    {
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            if (plan.Steps[i] == step)
                return true;
        }
        return false;
    }

    // Unavailable: the planner could not see enough of this PC to predict what
    // its own steps would do. BlockedReason is the planner's own wording and is
    // shown verbatim for the same reason a Detail is - a view that re-words a
    // refusal is a view that can drop the part the user needed. Nothing here
    // names an OK that runs anything, which is what makes this branch
    // structurally incapable of reaching SigningFix.RunAsync.
    //
    // The one refusal that has a next move is Secure Boot still being on after
    // the firmware trip, so that case gets the toggle's name and location and
    // an explicit offer to go round again. Keyed off the planner's reason, not
    // off the state, because Unavailable is the only state that carries one.
    internal static string SigningFixBlockedText(SigningFixPlan plan)
    {
        var reason = plan.BlockedReason ?? "It could not read enough of this PC's current state "
            + "to predict what the steps would do, and it will not run a sequence whose effect "
            + "it cannot predict.";
        var secureBoot = reason.Contains("Secure Boot", StringComparison.Ordinal);

        var todo = "WHAT YOU NEED TO DO\n"
            + "- Nothing has been changed on this PC, so there is nothing to undo.\n";
        if (secureBoot)
        {
            todo += "- To try again: start " + SigningFixLabel + " from this menu and it restarts "
                + "this PC into the firmware setup screen once more.\n"
                + "- On that screen, the setting to change is named Secure Boot, usually in the "
                + "Security or the Boot menu. Set it to Disabled, then save and exit (F10 on "
                + "most PCs).\n";
        }
        todo += "- To check the state yourself: bcdedit /enum {current} in an administrator "
            + "Command Prompt.\n"
            + "- Diagnostics -> Open logs has what the tray saw.";

        return string.Join("\n\n", new[]
        {
            "Magic Tray will not run the driver signing fix on this PC.",
            reason,
            todo,
            "OK closes this and changes nothing.",
        });
    }

    // NothingToDo: every prerequisite this fix exists to set is already set.
    // The honest offer in that state is the way back out, so OK leads to the
    // undo - and only to the undo's OWN confirmation, never straight into a
    // run, so the one-confirmation rule holds on that path too.
    internal static string SigningFixNothingToDoText(SigningFixPlan plan) => string.Join("\n\n", new[]
    {
        "This PC is already configured to load the self-signed scroll driver: nothing in the "
        + "driver signing fix is left to do.",
        plan.BlockedReason ?? "Test Mode is on, Memory integrity is off and Secure Boot is not "
            + "refusing the test signing flag. If the wheel is still dead, what is left is a "
            + "reboot that has not happened yet or a fault on the device itself - the top row of "
            + "this menu reports those.",
        "WHAT YOU NEED TO DO\n"
        + "- Nothing, unless you want this PC put back the way it was.\n"
        + "- OK shows the undo (" + SigningFixRevertLabel + "): test signing off, Memory "
        + "integrity back on, one restart, and BitLocker suspended for just that restart.\n"
        + "- Nothing runs until you accept the undo's own confirmation.",
        "OK shows the undo. Cancel leaves this PC exactly as it is.",
    });

    // The undo asked for on a PC that is not in Test Mode. Nothing to take
    // back, so this states that and claims no change either way - the opposite
    // mistake to a dialog that reports an undo it never ran.
    internal static string SigningFixNothingToRevertText(SigningFixPlan plan) =>
        plan.BlockedReason ?? "There is nothing to undo on this PC: test signing is already off "
            + "and Memory integrity is not being held off by Magic Tray. Nothing was changed.\n\n"
            + "WHAT YOU NEED TO DO\n"
            + "- Nothing. This PC is already in the state the undo would put it in.\n\n"
            + "OK closes this and changes nothing.";

    // The undo ran. It is the mirror of SigningFixRebootingText and must keep
    // the same two promises in the other direction: the reboot the user
    // confirmed is under way, and the way back IN is still on the menu - a user
    // who undid the fix by mistake is not locked out of redoing it.
    //
    // Keyed off the revert-only steps (SigningFix.cs: DisableTestSigning,
    // EnableHvci), never off the forward ones: the elevated script needs to
    // know which direction each setting moves, so the two legs carry different
    // step values and a text that read the forward names would silently
    // describe nothing. SuspendBitLocker is shared with the forward legs
    // because the undo rewrites the same boot configuration and needs the same
    // one-restart suspend to avoid the recovery-key prompt.
    internal static string SigningFixRevertDoneText(SigningFixPlan plan)
    {
        var blocks = new List<string>(6)
        {
            "The undo ran and this PC is rebooting now, as the confirmation said it would.",
            "WHAT YOU NEED TO DO\n"
            + "- Nothing. Let the PC restart.",
        };

        if (HasStep(plan, SigningStep.DisableTestSigning))
        {
            blocks.Add("After the reboot test signing is off: the Test Mode watermark goes from "
                + "the desktop, and Windows stops loading the self-signed scroll driver, so the "
                + "wheel goes back to however it behaved before the fix.");
        }

        if (HasStep(plan, SigningStep.EnableHvci))
            blocks.Add("Memory integrity is back on, which is the state Windows ships with.");

        if (HasStep(plan, SigningStep.SuspendBitLocker))
        {
            blocks.Add("BitLocker protection is suspended for this one reboot, exactly as the "
                + "fix itself did it, so the boot configuration change does not ask you for a "
                + "recovery key. It is active again when Windows comes back.");
        }

        blocks.Add("TO REDO\n"
            + "One click: " + SigningFixLabel + " is still on this menu if you want the scroll "
            + "driver loading again.");
        return string.Join("\n\n", blocks);
    }

    // The resumed fix, after the firmware restart. This text REPLACES
    // plan.Prompt rather than preceding it: a tray that starts with a pending
    // fix asks exactly one question, and that question has to carry the
    // planner's own step list inside it or "Yes" would be consent to something
    // the dialog never named.
    internal static string SigningFixResumeText(SigningFixPlan plan) => string.Join("\n\n", new[]
    {
        "You started the driver signing fix before this PC restarted into its firmware screen. "
        + "Finish it now?",
        "WHAT IS LEFT\n"
        + "- The rest of the fix, below. It is one administrator step and one restart.\n"
        + "- Answer Yes to run it now, or No to leave this PC exactly as it is.",
        plan.Prompt,
        "Yes runs the rest in one administrator step and asks nothing further. No drops the fix "
        + "and changes nothing - " + SigningFixLabel + " stays on this menu for whenever you want "
        + "it.",
    });

    // NeedsFirmware, accepted and run. Secure Boot is a firmware setting and no
    // OS API can change it, so the sequence stops at the firmware restart and
    // this text is the whole of the user's part: where the setting is, what to
    // do there, and that exactly one question is left afterwards.
    internal static string SigningFixFirmwareText(SigningFixPlan plan)
    {
        var blocks = new List<string>(6)
        {
            "This PC is restarting into its UEFI firmware setup screen now. Windows is not "
            + "coming back until you are done there.",
            "WHAT YOU NEED TO DO ON THE SCREEN THAT IS COMING UP\n"
            + "- Find the setting named Secure Boot. It is usually under Security, Boot or "
            + "Authentication.\n"
            + "- Set it to Disabled.\n"
            + "- Save and exit (F10 on most PCs). Windows starts again by itself.",
            "Secure Boot cannot be turned off from inside Windows at all, which is why this "
            + "restart exists: Magic Tray never touches the setting and cannot.",
        };

        if (HasStep(plan, SigningStep.SuspendBitLocker))
        {
            blocks.Add("BitLocker protection is suspended for this one restart so the firmware "
                + "change does not ask you for a recovery key. Windows turns it back on by "
                + "itself at the next boot.");
        }

        blocks.Add("WHAT HAPPENS WHEN WINDOWS IS BACK\n"
            + "- Magic Tray asks once whether to finish the rest: Memory integrity off, test "
            + "signing on, one more reboot. That is the only question left.\n"
            + "- Answer Yes and it does the rest on its own.\n"
            + "- If you left the firmware screen without changing Secure Boot, answer No: "
            + "nothing else on this PC will have moved.");
        return string.Join("\n\n", blocks);
    }

    // Ready, accepted and run: every step is done and the reboot is part of the
    // sequence the user confirmed in plan.Prompt. The undo is named here
    // because a user who can get into Test Mode is entitled to the way out of
    // it on the same screen that told her she is in it.
    internal static string SigningFixRebootingText(SigningFixPlan plan)
    {
        var blocks = new List<string>(5)
        {
            "The driver signing fix ran and this PC is rebooting now, as the confirmation said "
            + "it would.",
            "WHAT YOU NEED TO DO\n"
            + "- Nothing. Let the PC restart, then try the scroll wheel.",
        };

        if (HasStep(plan, SigningStep.EnableTestSigning))
        {
            blocks.Add("After the reboot Windows shows a Test Mode watermark on the desktop - "
                + "that watermark is the expected sign it worked. Open the Magic Tray menu then "
                + "and the scroll driver should be loading.");
        }

        if (HasStep(plan, SigningStep.SuspendBitLocker))
        {
            blocks.Add("BitLocker protection was suspended for this one reboot so the boot "
                + "configuration change does not ask you for a recovery key. Windows turns it "
                + "back on by itself.");
        }

        blocks.Add("TO UNDO\n"
            + "One click: " + SigningFixRevertLabel + " on the same menu puts test signing back "
            + "off and Memory integrity back on, over one more reboot, with BitLocker suspended "
            + "for that reboot the same way this one was.");
        return string.Join("\n\n", blocks);
    }

    // UAC declined. The elevated script never started, so this claims no
    // partial change at all - not one step, not the reboot, not BitLocker.
    internal static string SigningFixCancelledText() =>
        "Nothing was changed: the administrator prompt was declined, so none of the steps ran.\n\n"
        + "WHAT THIS LEFT ON THIS PC\n"
        + "- This PC is exactly as it was, no reboot is scheduled and BitLocker was not "
        + "suspended.\n\n"
        + "WHAT YOU NEED TO DO\n"
        + "- Nothing, unless you want to try again: " + SigningFixLabel + " stays on this menu "
        + "for whenever you want it.";

    // The elevated script started and did not report success. The honest claim
    // is that the result is unknown, never that nothing happened - so the
    // script's own reported reason is shown VERBATIM rather than summarised: it
    // is the only evidence of how far the sequence got.
    internal static string SigningFixFailedText(string? reason)
    {
        var blocks = new List<string>(4)
        {
            "Something in the driver signing fix did not work, so do not assume the settings "
            + "moved. Magic Tray could not finish it on this PC.",
        };

        if (!string.IsNullOrWhiteSpace(reason))
            blocks.Add("What the elevated step reported:\n" + reason!.Trim());

        blocks.Add("WHAT YOU NEED TO DO\n"
            + "- Check the state yourself: bcdedit /enum {current} in an administrator Command "
            + "Prompt.\n"
            + "- Check Memory integrity in Windows Security -> Device security -> Core "
            + "isolation.\n"
            + "- Diagnostics -> Open logs has what the tray saw.");
        return string.Join("\n\n", blocks);
    }
}
