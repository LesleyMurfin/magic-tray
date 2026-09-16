// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// Menu copy for DriverAdvisor: the advice line and the per-driver "what you
// would get" lines shown inside a device's Driver submenu.
//
// This file is pure text. It reads no registry, starts no process, opens no HID
// handle and touches no WinForms type, so every string below is testable off
// device. DriverAdvisor owns the facts; this file owns only their shape.
//
// ---------------------------------------------------------------------------
// Two lengths, and which one a menu may have
// ---------------------------------------------------------------------------
// DriverAdvisor's Why strings are paragraphs: they were written to be read in
// a dialog, with sources and caveats. Rendered as ToolStripItem text they came
// out as one unbroken line the width of the screen, over the top of the rest
// of the menu. So every user-facing string here exists in two forms:
//
//   AdviceShort / OptionRowsShort  - menu. One line, hard character caps
//                                    (AdviceShortMax, OptionRowShortMax),
//                                    proven exhaustively by the tests.
//   AdviceFull / OptionRows        - dialog. The paragraph, verbatim.
//
// The caps are the contract, not a hope: DriverAdviceViewTests walks every
// DeviceKind x PID x DriverStatus and fails on the first string that grows
// past its cap or gains a line break.
//
// ---------------------------------------------------------------------------
// The predicted-vs-observed hazard, and how this file answers it
// ---------------------------------------------------------------------------
// The device row already carries DeviceCapability.Rows: "Pointer: working",
// "Scroll: not working (driver service not running)", "Battery: 35%". Those are
// READINGS of this PC right now. The lines here are PREDICTIONS about drivers
// the user is not on. A user who reads "Scroll: working" two lines above
// "no scroll" and mistakes the second for a second opinion about their own
// machine has been actively misled, which is the one failure this repo will not
// ship.
//
// Three separations are applied at once, so no single rendering accident can
// collapse them:
//   1. Word order. An observed line begins with the capability
//      ("Scroll: ..."). A predicted line begins with the DRIVER
//      ("Stock Windows: ..."). Capability names only ever appear lowercase and
//      mid-line here, never as the leading token and never followed by a colon.
//   2. Mood. The full rows say "would give" in every line; the short rows are
//      too narrow to carry it, so OptionsHeader() - which is on screen directly
//      above them, and is itself menu-safe - carries the mood for the block.
//   3. Vocabulary. Observed lines judge: "working", "not working",
//      "not installed", "unknown". Predicted lines only tally: "pointer",
//      "no scroll", "battery?", "yes", "no", "unknown". The one shared word,
//      "unknown", never appears in a short row: a capability that cannot be
//      predicted is written "battery?" there instead. No substring of a
//      predicted line can be read as a verdict on the present state.
//
// Honesty rules, same as the rest of the app:
//   - Cannot be predicted renders as "unknown" (full) or a trailing "?"
//     (short). It is never a fault and never a prediction.
//   - NotApplicable never renders as a failure. A keyboard has no wheel; saying
//     its scroll is "no" would read as a defect, so inapplicable capabilities
//     leave the tally entirely - named in a "do not apply" tail in the full
//     row, absent from the short one, matching DeviceCapability.Rows, which
//     omits the lines that do not apply.
//   - EitherOrOnly renders as one exclusive clause ("scroll or battery but
//     never both", short: "one of scroll or battery"), never as two independent
//     capabilities that happen to be hedged.
// All text is plain ASCII (hyphens only, no em dashes, no smart quotes): this
// repo has been bitten by mojibake in tray strings.
internal static class DriverAdviceView
{
    // Menu caps. A tray menu item is as wide as its text, so these are what
    // stops a driver paragraph from spanning the screen. They are deliberately
    // well inside the layout's own clamp (TrayMenu.MenuTextMaxChars): copy that
    // needs clamping has already failed.
    internal const int AdviceShortMax = 60;
    internal const int OptionRowShortMax = 50;
    internal const int OptionsHeaderMax = 60;

    // The one line that introduces the option rows. Read together with the
    // capability rows above it, this is the sentence that tells the user the
    // block below is about drivers they are NOT on - and, since the short rows
    // drop the "would give" mood to fit, it is the line that carries it.
    internal static string OptionsHeader() =>
        "What each driver would give - a prediction, not a reading";

    // The menu form of the advice line: one line, at most AdviceShortMax
    // characters, and still the two things the user needs - what this device is
    // on now, and what this app recommends. AdviceFull is the same reasoning at
    // dialog length.
    //
    // stock / sdp are the readings for the kinds DriverHealthChecker skips
    // (keyboards, trackpads). When the stock driver has actually been read,
    // that reading is the line: it NAMES the driver instead of saying "Driver
    // not read yet", which on a Magic Keyboard was simply false. Both default
    // to null, which is exactly the old behaviour, so every mouse row is
    // byte-identical to before.
    internal static string AdviceShort(
        DeviceKind kind, string? pid, DriverStatus? status,
        StockDriverInfo? stock = null, SdpPatchState? sdp = null)
    {
        if (StockAdviceShort(kind, pid, stock, sdp) is string read)
            return read;

        var rec = DriverAdvisor.RecommendedFor(kind, pid);
        if (rec is null)
            return "No driver advice for this device";

        var currentId = DriverAdvisor.CurrentOptionId(kind, pid, status);
        if (currentId == rec.Id)
            return $"On the recommended driver ({ShortLabel(rec)})";

        if (currentId is not null)
        {
            foreach (var o in DriverAdvisor.OptionsFor(kind, pid))
            {
                if (o.Id == currentId)
                    return $"On {ShortLabel(o)} - {ShortLabel(rec)} is recommended";
            }
        }

        return $"{ShortStateClause(kind, pid, status)} - {ShortLabel(rec)} is recommended";
    }

    // The menu form of a READ stock driver, for the keyboards and trackpads
    // this app binds nothing to: what the driver is, that it is working, and
    // where the battery percent comes from - three facts in under
    // AdviceShortMax characters. Null means "not applicable": a mouse, or a
    // device whose driver could not be read, both of which fall back to the
    // status-based line above.
    //
    // There is no "recommended" tail here, and dropping it is not a cap bought
    // with a lie: these kinds have exactly ONE option, and it is the one the
    // reading just named, so a recommendation would be the same sentence
    // twice. The paragraph in the dialog still carries it.
    internal static string? StockAdviceShort(
        DeviceKind kind, string? pid, StockDriverInfo? info, SdpPatchState? sdp)
    {
        if (!DriverAdvisor.HasStockDriverEvidence(kind, pid, info))
            return null;

        // AllNodesOk false is missing evidence, never a fault (StockDriverReader
        // only sets it true for nodes positively observed present and started),
        // so the unconfirmed line says that and stops: the battery tail is the
        // first thing to give up for width, and it is the less urgent fact.
        if (!info!.AllNodesOk)
            return $"{ShortDriverName(kind)} - health not confirmed";

        var battery = kind == DeviceKind.MagicKeyboard
            ? (sdp ?? SdpPatchState.Unknown) switch
            {
                SdpPatchState.Applied => "SDP patch applied",
                SdpPatchState.NotApplied => "battery needs patch",
                // Never "no patch": a check that did not happen is not a
                // missing patch.
                _ => "patch not checked",
            }
            // Trackpads never involve the patch at all - the percent is HID.
            : "battery over HID";

        return $"{ShortDriverName(kind)} - working; {battery}";
    }

    // The dialog form: DriverAdvisor.AdviceLine is already a full, sourced
    // sentence about the present state plus the recommendation, so it is passed
    // through verbatim rather than re-worded here - two copies of that
    // reasoning would drift. Null from the advisor means this app has no driver
    // story for the device at all (a Logitech row, say). That is said as an
    // absence of knowledge, never as a fault and never as silence.
    //
    // NOT menu-safe: this is paragraphs, and a ToolStripItem will render it as
    // one screen-wide line. Menus take AdviceShort.
    internal static string AdviceFull(
        DeviceKind kind, string? pid, DriverStatus? status,
        StockDriverInfo? stock = null, SdpPatchState? sdp = null)
    {
        var line = DriverAdvisor.AdviceLine(kind, pid, status, stock, sdp);
        if (!string.IsNullOrWhiteSpace(line))
            return line.Trim();

        return "Magic Tray has no driver advice for this device: it does not manage a "
             + "driver here, so what another driver would give is unknown.";
    }

    // The dialog-length twin of StockAdviceShort, for a caller that wants the
    // read-driver prose on its own rather than the whole advice paragraph.
    // Null on the same terms: a mouse, or a driver that could not be read.
    internal static string? StockAdviceFull(
        DeviceKind kind, string? pid, StockDriverInfo? info, SdpPatchState? sdp) =>
        DriverAdvisor.StockDriverLine(kind, pid, info, sdp);

    // The menu form of the per-driver rows: one line each, at most
    // OptionRowShortMax characters, in DriverAdvisor's own order so a caller
    // pairing these with the radios can index them against
    // DriverAdvisor.OptionsFor. A device this app binds no driver for has no
    // choices to predict, and gets no invented rows; use ShowOptions to decide
    // whether the header is worth drawing.
    internal static IReadOnlyList<string> OptionRowsShort(DeviceKind kind, string? pid)
    {
        var options = DriverAdvisor.OptionsFor(kind, pid);
        if (options.Count == 0)
            return [];

        var rows = new string[options.Count];
        for (var i = 0; i < options.Count; i++)
            rows[i] = OptionSummaryShort(options[i]);
        return rows;
    }

    // The dialog form of the same rows, one long sentence each. NOT menu-safe.
    internal static IReadOnlyList<string> OptionRows(DeviceKind kind, string? pid)
    {
        var options = DriverAdvisor.OptionsFor(kind, pid);
        if (options.Count == 0)
            return [];

        var rows = new string[options.Count];
        for (var i = 0; i < options.Count; i++)
            rows[i] = OptionSummary(options[i]);
        return rows;
    }

    // Whether OptionsHeader and the option rows have anything to show. Kept
    // beside them so a caller cannot draw a header over an empty list.
    internal static bool ShowOptions(DeviceKind kind, string? pid) =>
        DriverAdvisor.OptionsFor(kind, pid).Count > 0;

    // The menu form of a single choice:
    //
    //   <Driver>: <what it gives>[, no <what it does not>][ (recommended)]
    //
    // for example:
    //   KMDF: pointer, scroll, battery (recommended)
    //   Patched Apple: pointer, one of scroll or battery
    //   Stock Windows: pointer, battery, no scroll
    //
    // Positives lead and the dead capabilities come last, so the row reads as
    // what the driver gives rather than as a list of complaints. A capability
    // that cannot be predicted carries a trailing "?" ("battery?"): that is the
    // short spelling of "unknown", and the only spelling narrow enough to fit
    // beside the other two on the widest row this app produces (Boot Camp on a
    // v2, where the battery has never been measured).
    internal static string OptionSummaryShort(DriverOption option)
    {
        var caps = Capabilities(option);

        var exclusive = new List<string>(3);
        var dead = new List<string>(3);
        foreach (var (name, exp) in caps)
        {
            if (exp == CapabilityExpectation.EitherOrOnly)
                exclusive.Add(name);
            else if (exp == CapabilityExpectation.Dead)
                dead.Add(name);
        }

        var clauses = new List<string>(4);
        foreach (var (name, exp) in caps)
        {
            switch (exp)
            {
                case CapabilityExpectation.Works:
                    clauses.Add(name);
                    break;
                case CapabilityExpectation.Unknown:
                    clauses.Add($"{name}?");
                    break;
                case CapabilityExpectation.EitherOrOnly:
                    if (exclusive.Count > 0 && exclusive[0] == name)
                        clauses.Add(ExclusiveClauseShort(exclusive));
                    break;
                case CapabilityExpectation.Dead:
                case CapabilityExpectation.NotApplicable:
                    break; // dead in the tail below; inapplicable is never named
            }
        }

        if (dead.Count > 0)
            clauses.Add($"no {string.Join(" or ", dead)}");

        var tally = clauses.Count > 0 ? string.Join(", ", clauses) : "nothing this app can predict";
        var recommended = option.Recommended ? " (recommended)" : string.Empty;
        return $"{ShortLabel(option)}: {tally}{recommended}";
    }

    // The dialog form of a single choice. One shape for every option:
    //
    //   <Driver> - would give: <tally>[; <what does not apply>][ (recommended)]
    //
    // for example:
    //   KMDF - would give: pointer yes, scroll yes, battery yes (recommended)
    internal static string OptionSummary(DriverOption option)
    {
        var label = FullLabel(option);
        var caps = Capabilities(option);

        var exclusive = new List<string>(3);
        var notApplicable = new List<string>(3);
        foreach (var (name, exp) in caps)
        {
            if (exp == CapabilityExpectation.EitherOrOnly)
                exclusive.Add(name);
            else if (exp == CapabilityExpectation.NotApplicable)
                notApplicable.Add(name);
        }

        var clauses = new List<string>(3);
        foreach (var (name, exp) in caps)
        {
            switch (exp)
            {
                case CapabilityExpectation.Works:
                    clauses.Add($"{name} yes");
                    break;
                case CapabilityExpectation.Dead:
                    clauses.Add($"{name} no");
                    break;
                case CapabilityExpectation.Unknown:
                    clauses.Add($"{name} unknown");
                    break;
                case CapabilityExpectation.EitherOrOnly:
                    // The whole exclusive set is one clause, written once at
                    // the position of its first member: "scroll or battery but
                    // never both" cannot be misread as two hedged capabilities.
                    if (exclusive.Count > 0 && exclusive[0] == name)
                        clauses.Add(ExclusiveClause(exclusive));
                    break;
                case CapabilityExpectation.NotApplicable:
                    break; // carried by the tail below, never as a failure
            }
        }

        // Everything about this choice is inapplicable: still a sentence, and
        // still not an accusation.
        var tally = clauses.Count > 0 ? string.Join(", ", clauses) : "nothing this app can predict";
        var tail = notApplicable.Count > 0
            ? $"; {JoinAnd(notApplicable)} {(notApplicable.Count == 1 ? "does" : "do")} not apply to this device"
            : string.Empty;
        var recommended = option.Recommended ? " (recommended)" : string.Empty;

        return $"{label} - would give: {tally}{tail}{recommended}";
    }

    static (string Name, CapabilityExpectation Exp)[] Capabilities(DriverOption option) =>
    [
        ("pointer", option.Pointer),
        ("scroll", option.Scroll),
        ("battery", option.Battery),
    ];

    // "scroll or battery but never both" for the documented pair. A lone
    // either-or capability has no named rival to exclude, so it says only what
    // is certain: it is available in one of that driver's modes, not always.
    static string ExclusiveClause(List<string> names) =>
        names.Count == 1
            ? $"{names[0]} in one mode only"
            : $"{string.Join(" or ", names)} but never both";

    // Same fact at menu width. "one of scroll or battery" is the exclusivity
    // said in four words: one of them, therefore not both.
    static string ExclusiveClauseShort(List<string> names) =>
        names.Count == 1
            ? $"{names[0]} in one mode"
            : $"one of {string.Join(" or ", names)}";

    static string FullLabel(DriverOption option) =>
        string.IsNullOrWhiteSpace(option.Label)
            ? (string.IsNullOrWhiteSpace(option.Id) ? "This driver" : option.Id.Trim())
            : option.Label.Trim();

    // The radio copy, trimmed only where the radio label carries a word the
    // menu line cannot afford. "Patched Apple driver" -> "Patched Apple" is the
    // one case: it is a prefix of the radio label, so the row and the radio
    // still name the same thing and cannot drift apart.
    static string ShortLabel(DriverOption option) =>
        option.Id == DriverAdvisor.IdPatchedApple ? "Patched Apple" : FullLabel(option);

    // The read driver in plain words, at menu width. Not a radio label: this
    // names what Windows itself is running on the device ("Windows' own
    // keyboard driver" for kbdhid from keyboard.inf), which is the fact the
    // keyboard row was missing. The long form, with the service, INF,
    // provider, version and stack, lives in DriverAdvisor.
    static string ShortDriverName(DeviceKind kind) =>
        kind == DeviceKind.MagicKeyboard ? "Windows' own keyboard driver" : "Windows' own driver";

    // Says what is unknown, at menu width, for the states where this app cannot
    // name the driver the device is on. Never predicts capabilities from a
    // status it could not match, and never reports the device as configured
    // correctly. The long forms live in DriverAdvisor.
    static string ShortStateClause(DeviceKind kind, string? pid, DriverStatus? status) =>
        status switch
        {
            null => "Driver not read yet",
            DriverStatus.NotBound =>
                TrayMenu.IsV3(kind, pid) || TrayMenu.IsV1V2Mouse(kind)
                    ? "No scroll driver bound"
                    : "No driver bound",
            DriverStatus.NotInstalled => "No scroll driver installed",
            DriverStatus.Error => "Driver state not readable",
            DriverStatus.UnknownAppleMouse => "Apple mouse not in catalog",
            _ => "Driver not matched",
        };

    static string JoinAnd(List<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.GetRange(0, names.Count - 1))} and {names[^1]}",
    };
}
