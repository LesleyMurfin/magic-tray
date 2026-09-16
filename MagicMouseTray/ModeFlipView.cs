// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// Every word the user reads about the battery flip, and nothing else: no
// WinForms, no registry, no process. ModeFlip owns the cycle and produces a
// ModeFlipResult; this file turns that result into sentences, so the wording
// can be tested off-device and a new ModeFlipOutcome cannot ship unworded.
//
// Three rules shape all of it, and they are the repo's rules, not style:
//
//   1. The cost is stated BEFORE consent. The flip stops the pointer and the
//      wheel for a few seconds and costs one administrator approval. A user
//      who only learns that after clicking has been misled, so OfferText says
//      it in the dialog that asks.
//   2. A restore is never claimed unless ModeFlip verified it. ModeFlipResult
//      .RestoredToModeB is that verification, and it is the only thing allowed
//      to produce "the mouse is back in scroll mode". Anything else says so.
//   3. The dangerous branch leads with the danger. RestoreFailed means the
//      wheel may be dead right now, so that sentence is the first line and the
//      one-click fix is its own paragraph, never a clause inside prose.
//
// ModeFlipResult.Detail is deliberately not shown here. It is the terse
// transcript line (ModeFlip.DetailFor) meant for debug.log, and it names
// registry values; the user-facing text says the same thing in plain words.
//
// Vocabulary, fixed here and matched by docs/ENABLE-DISABLE.md:
//   "battery mode"  the shape where the percent is readable and scroll is dead
//   "scroll mode"   the resting shape where scroll works and the percent is not
// The internal Mode A / Mode B names never reach the screen.
internal static class ModeFlipView
{
    // The label of the recovery action, named in every text that tells the user
    // to take it, and the same string the tray menu item carries - so the
    // sentence "use X" and the item the user hunts for cannot drift apart.
    internal const string RestoreItemLabel = "Restore scroll mode";

    // What OK will do, and what the tray guarantees afterwards. Shared by the
    // before-the-flip offer and by the startup recovery offer so the promise is
    // worded once.
    const string RestorePromise =
        "One administrator approval, and Magic Tray puts back the exact setting it recorded "
        + "before it started and then checks that scrolling is back.";

    // The end state a verified restore earns. Nothing else may say this.
    const string ScrollConfirmed =
        "The mouse is back in scroll mode. Magic Tray re-read the setting and checked the mouse "
        + "itself, so that is confirmed rather than assumed.";

    // The fix, as its own paragraph, plus the hand route if it fails too. The
    // pair-again route is the measured last resort from docs/ENABLE-DISABLE.md.
    const string FixItNow =
        "Put it back now: use \"" + RestoreItemLabel + "\" on this mouse in the tray menu. "
        + RestorePromise
        + "\n\nIf that does not work either: open Windows Settings -> Bluetooth & devices, "
        + "remove this mouse, switch it off and on again, then add it back.";

    // The end state for an unverified restore on an outcome whose headline is
    // something else (no reading, or a reading with a shaky restore). Still
    // leads with the danger inside its own paragraph.
    //
    // Worded so that it does not contain ScrollConfirmed's phrase "is back in
    // scroll mode" as a substring: a negated sentence that quotes the success
    // wording is one careless search away from being read as success, by a
    // skimming user and by a test alike.
    const string ScrollInDoubt =
        "Scrolling may be dead right now: Magic Tray could not confirm this mouse came back out "
        + "of battery mode.\n\n" + FixItNow;

    /// <summary>
    /// The device-row item the user clicks to ask for a reading. Worded as an
    /// action that costs something, not as a battery display: a passive-looking
    /// "Battery" row would be clicked by someone who did not expect their
    /// pointer to stop.
    /// </summary>
    internal static string MenuItemLabel() =>
        "Read battery now - stops the mouse for 10–20 seconds...";

    /// <summary>
    /// The confirmation shown BEFORE anything is flipped. Every cost the user
    /// is about to pay is on this screen: the pause, the single approval, and
    /// the promise that the tray always puts the mouse back.
    /// </summary>
    internal static string OfferText(string pid)
    {
        var device = string.IsNullOrEmpty(pid) ? "this Magic Mouse" : $"this Magic Mouse ({pid})";
        return $"Read the battery level of {device}?\n\n"
            + "This mouse cannot report its battery level and work as a mouse at the same time, "
            + "so Magic Tray has to switch it over for a moment and then switch it back.\n\n"
            + "What that costs:\n\n"
            + "- The pointer and scrolling stop for 10–20 seconds while Windows rebuilds the "
            + "connection. That is expected and it is not a fault.\n"
            + "- Windows asks you to approve one administrator prompt. That one approval covers "
            + "the whole reading, start to finish.\n"
            + "- Magic Tray always puts the mouse back into scroll mode afterwards, checks that "
            + "it is back, and tells you plainly if that check ever fails.\n\n"
            + "Nothing is installed or removed, the mouse stays paired, and the driver you chose "
            + "for it does not change.\n\n"
            + "OK takes the reading. Cancel changes nothing.";
    }

    /// <summary>
    /// What the user sees once the cycle has finished, one branch per
    /// <see cref="ModeFlipOutcome"/>.
    /// </summary>
    /// <remarks>
    /// A statement switch with no default arm: every member is named, and a
    /// member added later falls to the throw instead of being absorbed by a
    /// catch-all that would ship it unworded. ModeFlipViewTests drives
    /// Enum.GetValues, so that throw is a build-time-red test, not a surprise
    /// in front of a user. (A switch EXPRESSION cannot be used here: C# reports
    /// CS8524 for the unnamed-enum-value case even when every member is
    /// covered, and this project builds warning-free.)
    /// </remarks>
    internal static string ResultText(ModeFlipResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Outcome)
        {
            case ModeFlipOutcome.Ok:
                // RestoreModeB() also returns Ok, with no percent to report.
                return result.Percent >= 0
                    ? $"Battery: {DeviceCapability.BatteryLabel(result.Percent)}\n\n"
                        + RestoreTail(result)
                    : "Scrolling is back on.\n\n" + RestoreTail(result);

            case ModeFlipOutcome.NotPathA:
                // Nothing was elevated and nothing was written, so there is no
                // restore to report either way.
                return "No reading was taken, and nothing was changed.\n\n"
                    + "This mouse only needs the switch-over while it is on the patched Apple "
                    + "driver, and it is not on that driver now. On the KMDF driver the battery "
                    + "level arrives on its own, with no pause and no approval.";

            case ModeFlipOutcome.NoInstances:
                return "No reading was taken, and nothing was changed.\n\n"
                    + "Windows has no live Bluetooth connection to this mouse right now, so there "
                    + "was nothing to read from.\n\n"
                    + "Switch the mouse on, give it a moment to reconnect, then ask again.";

            case ModeFlipOutcome.FlipFailed:
                return "No battery level was read.\n\n"
                    + "The mouse did not finish switching over in the time allowed, so it never "
                    + "got as far as reporting a level.\n\n"
                    + RestoreTail(result)
                    + "\n\nYou can ask again later. The battery level is also readable without "
                    + "any of this on the KMDF driver.";

            case ModeFlipOutcome.BatteryUnreadable:
                // The switch-over worked; the mouse simply sent nothing. An idle
                // mouse legitimately does that, so this is unknown, not a fault,
                // and with RestoredToModeB it must not read as a failed restore.
                return "No battery level was read.\n\n"
                    + "The mouse switched over, but it sent no battery level back before the time "
                    + "allowed ran out. A mouse that has been sitting still often sends nothing.\n\n"
                    + RestoreTail(result)
                    + "\n\nMove the mouse around for a few seconds, then ask again.";

            case ModeFlipOutcome.RestoreFailed:
                // The one branch where the user has to act. Danger first, fix
                // second, everything else after.
                return "Scrolling may be dead right now.\n\n"
                    + "Magic Tray took this mouse out of scroll mode to read its battery and could "
                    + "not confirm it got back. It will not tell you the mouse is fine when it "
                    + "cannot see that it is.\n\n"
                    + FixItNow
                    + (result.Percent >= 0
                        ? $"\n\nThe battery level it did manage to read was {result.Percent}%."
                        : "");

            case ModeFlipOutcome.Cancelled:
                return "No reading was taken, and nothing was changed.\n\n"
                    + "The administrator prompt was not approved, so no setting was written, the "
                    + "mouse was not restarted, and it never left scroll mode.\n\n"
                    + "Ask for the reading again whenever you want it.";
        }

        throw new ArgumentOutOfRangeException(
            nameof(result), result.Outcome, "ModeFlipOutcome has no user-facing text");
    }

    /// <summary>
    /// Shown at launch while <see cref="ModeFlip.StaleModeAOnStartup"/> is
    /// true: an earlier reading was never confirmed finished, which means the
    /// mouse may still be parked in the shape where its wheel does not work.
    /// </summary>
    internal static string StartupRestoreOffer() =>
        "Scrolling may be dead right now.\n\n"
        + "A battery reading on your Magic Mouse did not finish last time. That reading switches "
        + "the mouse into a mode where its scroll wheel does not work, and Magic Tray never got "
        + "to confirm the mouse was switched back.\n\n"
        + "OK puts it back in one click. " + RestorePromise + "\n\n"
        + "Cancel leaves the mouse as it is. Magic Tray will offer this again next time it starts.";

    // Verified restores get the confirmation sentence; everything else gets the
    // warning and the fix. This is rule 2 in one place, so no branch above can
    // promise a restore the cycle did not actually check.
    static string RestoreTail(ModeFlipResult result) =>
        result.RestoredToModeB ? ScrollConfirmed : ScrollInDoubt;
}
