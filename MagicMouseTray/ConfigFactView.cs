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
        // Test Mode is the ONE security setting this tray may change, and only
        // on the terms AGENTS.md:76 sets: a per-action confirmation that names
        // the command, the reboot and the BitLocker recovery-key risk, never on
        // startup, never from a poll, with the way back offered on the same
        // row. So this line may no longer carry the old blanket "changes
        // nothing" promise - that promise is still true of every OTHER setting
        // and this wording keeps it exactly that wide.
        SystemConfigChecker.TestModeFactId =>
            "You decide, and Magic Tray can carry it out. This row can run "
            + "bcdedit /set testsigning on or off for you, but only for the one action you confirm "
            + "in the dialog that names the command, the reboot it needs and the BitLocker "
            + "recovery-key risk - never on startup, never on a timer, never without that answer, "
            + "and the way back is offered on this same row. Memory integrity and Secure Boot it "
            + "still never changes: those it only reads.",

        // Memory integrity stays a hand-operated Windows setting. A programmatic
        // HVCI change is an explicit non-goal, so this line must keep promising
        // the tray will not make one, and must name Test Mode as the single
        // exception rather than imply the tray changes nothing at all.
        SystemConfigChecker.MemoryIntegrityFactId =>
            "You, by hand, in Windows Security. Magic Tray never changes Memory integrity, "
            + "Secure Boot or any other security setting on this PC - it reports what it read and "
            + "points you at the page. Test Mode is the only setting it can change, and only from "
            + "its own row after you confirm that one action.",

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
    // Everything below renders a TestModePlan (TestModeSwitch.cs), which is
    // built pure from the readings taken at the moment of the click. The view
    // never re-derives whether the action is possible: OffersRun is the single
    // gate the caller asks, so "may this click reach bcdedit" is decided in one
    // testable place instead of once per dialog, and a plan the switch refused
    // cannot be talked back into running by a wording change here.

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

    // The only "yes" in this repo's UI layer to running bcdedit. A blocked plan
    // is a plan whose action is impossible or pointless on this PC, and the
    // charter answer to that is to explain and offer the documentation - never
    // an OK that runs something (AGENTS.md:76).
    internal static bool OffersRun(TestModePlan plan) => plan.Block == TestModeBlock.None;

    // Why the action is not on offer, in the user's terms. Phrased as what this
    // PC does, not as a refusal by the tray, because every one of these is a
    // property of the machine rather than a policy choice made here.
    internal static string BlockReason(TestModePlan plan) => plan.Block switch
    {
        TestModeBlock.SecureBootOn =>
            "Secure Boot is on. Secure Boot refuses the test signing flag outright - the command "
            + "would answer that the value is protected by Secure Boot policy and nothing on this "
            + "PC would change. Secure Boot is turned off in this PC's UEFI firmware, by you, "
            + "before Windows starts; Magic Tray never touches it and cannot.",

        TestModeBlock.AlreadyInThatState =>
            "This PC is already in that state, so there is nothing to run. If the wheel is still "
            + "dead, the remaining step is Memory integrity, or a reboot that has not happened yet.",

        TestModeBlock.UnknownState =>
            "Magic Tray could not read the current test signing state on this PC, so it will not "
            + "run a command whose effect it cannot predict. Read the state yourself with "
            + "bcdedit /enum {current} in an administrator Command Prompt.",

        // Not reachable through OffersRun, which is the only caller gate, but a
        // blank string here would render as a dialog with a missing paragraph.
        _ => "There is nothing to do here.",
    };

    // The dialog for a row whose action is blocked: the checker's own prose,
    // the reason, the owner, and at most a page to open. It never names an OK
    // that runs anything, which is what makes the blocked path structurally
    // incapable of reaching TestModeSwitch.RunAsync.
    internal static string TestModeBlockedText(ConfigFact fact, TestModePlan plan)
    {
        var blocks = new List<string>(4)
        {
            fact.Title,
            "What is wrong, what it costs you, and the steps:\n" + fact.Detail,
            "Why Magic Tray will not run this for you:\n" + BlockReason(plan),
            "Who fixes this:\n" + OwnerLine(fact.Id),
        };

        var footer = ActionFooter(fact);
        if (footer is not null)
            blocks.Add(footer);
        return string.Join("\n\n", blocks);
    }

    // Shown once the elevated bcdedit has run. Two things must survive every
    // future edit of this string: the reboot is the user's to make - the tray
    // does not reboot the PC, ever - and the way back is spelled out here
    // rather than left to be discovered.
    internal static string TestModeDoneText(TestModePlan plan)
    {
        var what = plan.Action == TestModeAction.Enable
            ? "Test Mode is set to turn on at the next boot. Windows shows a Test Mode watermark "
                + "on the desktop while it is on - that watermark is the expected sign it worked."
            : "Test Mode is set to turn off at the next boot. A self-signed driver, including this "
                + "project's scroll driver, will stop loading once it is off.";
        return string.Join("\n\n", new[]
        {
            what,
            plan.Command + " ran and reported success.",
            "Nothing has changed yet: this PC has to reboot first. Magic Tray will not reboot it "
            + "for you - reboot when it suits you, then open this menu again.",
            "To undo this: open this same row and confirm the opposite action, or run "
            + RevertCommand(plan) + " in an administrator Command Prompt, then reboot.",
        });
    }

    // UAC declined. The elevated process never started, so this says exactly
    // that and claims no partial change.
    internal static string TestModeCancelledText(TestModePlan plan) =>
        "Nothing was changed. The administrator prompt was declined, so " + plan.Command
        + " never ran and this PC is exactly as it was. The action stays on this row for "
        + "whenever you want it.";

    // The elevated step launched and did not report success. The honest claim
    // is that the result is unknown, not that nothing happened.
    internal static string TestModeFailedText(TestModePlan plan) =>
        "Magic Tray could not complete " + plan.Command + " on this PC, so do not assume the "
        + "setting moved. Check it with bcdedit /enum {current} in an administrator Command "
        + "Prompt, where you can also run the command yourself. Diagnostics -> Open logs has "
        + "what the tray saw.";

    // The way back, derived from the action rather than from the command text,
    // so a reworded Command in TestModeSwitch cannot silently turn the revert
    // line into the same command the user just ran.
    internal static string RevertCommand(TestModePlan plan) =>
        plan.Action == TestModeAction.Enable
            ? "bcdedit /set testsigning off"
            : "bcdedit /set testsigning on";
}
