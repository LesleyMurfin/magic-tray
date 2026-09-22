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
// stopped, two filters are registered (RepairPlanner.RepairProblem). A ConfigFact is
// a property of the PC that at most EXPLAINS such a fault - Test Mode off does
// not break anything by itself, it only means Windows will refuse to load a
// self-signed driver if one is there. So the device fault is the more specific,
// more present claim and always wins the top of the menu.
//
// That is enforced structurally rather than by a number this file could get
// wrong:
//   - RepairPlanner.MenuLabel owns the first row and speaks in the bare fault
//     voice ("Scroll driver is not installed", "2 problems found"), rendered
//     at TrayApp.cs:834;
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
    // reason RepairPlanner.MenuLabel says "No driver or connection problems
    // found" instead of hiding its row: an absent row is indistinguishable
    // from a tray that never looked, so the one case where the user is
    // entitled to be told the checks passed would be the case that renders as
    // silence. The wording carries no count, no severity word and nothing
    // negated, so it cannot be misread as a warning at a glance.
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
            // (SystemConfigChecker.TestModeFact and MemoryIntegrityFact, whose
            // severity comes from SigningSeverity), so "settings" is accurate;
            // the singular branch prints the title and needs no such assumption.
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
        // The tray is read-only about the security posture of the PC by
        // construction (SystemConfigChecker.cs:12-21). Saying so here is what
        // stops a user waiting for a button that will never exist.
        SystemConfigChecker.TestModeFactId or SystemConfigChecker.MemoryIntegrityFactId =>
            "You, by hand, in Windows. Magic Tray never changes Test Mode, Memory integrity, "
            + "Secure Boot or any other security setting on this PC - it reports what it read and "
            + "nothing more.",

        SystemConfigChecker.DriverPackageFactId =>
            "The driver package. Magic Tray offers the install from its own driver step, and "
            + "nothing is installed until you confirm it there.",

        // Fault B in docs/ENABLE-DISABLE.md, "Which dead-wheel fault is this -
        // read the stack before you chase filters". The Apple multitouch enable
        // FEATURE report is owned by the driver package's watcher, and a second
        // sender inside Magic Tray is an explicit NON-GOAL
        // (RepairPlanner.RepairAction.RecommendMultitouchWatcher).
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
}
