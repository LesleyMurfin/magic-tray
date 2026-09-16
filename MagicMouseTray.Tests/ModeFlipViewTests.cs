// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// ModeFlipView is pure text, so every rule it exists to enforce is testable
// here with no device, no registry and no clock:
//   - every ModeFlipOutcome is worded, and worded differently;
//   - an unverified restore is never reported as a success;
//   - the dangerous outcome names the recovery action;
//   - the offer states the cost before the user consents.
public class ModeFlipViewTests
{
    static ModeFlipOutcome[] AllOutcomes() =>
        (ModeFlipOutcome[])Enum.GetValues(typeof(ModeFlipOutcome));

    static ModeFlipResult Result(ModeFlipOutcome outcome, int percent, bool restored) =>
        new(outcome, percent, restored, "detail is log material, not screen material");

    // Drives the enum itself rather than a hand-written list, so adding a
    // ModeFlipOutcome without wording it fails here: ResultText has no default
    // arm and throws for an unnamed member. Distinctness is the second half -
    // two outcomes sharing one block of boilerplate would leave the user unable
    // to tell "no reading, scroll is fine" from "scroll may be dead".
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryOutcome_IsWordedAndWordedDistinctly(bool restored)
    {
        var seen = new Dictionary<string, ModeFlipOutcome>(StringComparer.Ordinal);

        foreach (var outcome in AllOutcomes())
        {
            var text = ModeFlipView.ResultText(Result(outcome, 57, restored));
            Assert.False(string.IsNullOrWhiteSpace(text), $"{outcome} has no text");

            Assert.False(
                seen.TryGetValue(text, out var clash),
                $"{outcome} and {clash} produce identical text");
            seen[text] = outcome;
        }
    }

    // The honesty rule, stated as a property over the whole enum rather than on
    // one branch: with the restore unverified, no outcome anywhere is allowed to
    // tell the user the mouse is back.
    [Fact]
    public void NoOutcome_ClaimsARestoreThatWasNeverVerified()
    {
        foreach (var outcome in AllOutcomes())
        {
            var text = ModeFlipView.ResultText(Result(outcome, 57, restored: false));
            Assert.DoesNotContain("is back in scroll mode", text, StringComparison.Ordinal);
        }
    }

    // The one branch the user has to act on. It must lead with the danger, name
    // the exact menu item, and keep that item out of the middle of a sentence.
    [Fact]
    public void RestoreFailed_LeadsWithTheDangerAndNamesTheRestoreAction()
    {
        var text = ModeFlipView.ResultText(Result(ModeFlipOutcome.RestoreFailed, 57, false));

        Assert.StartsWith("Scrolling may be dead right now.", text, StringComparison.Ordinal);
        Assert.Contains(ModeFlipView.RestoreItemLabel, text, StringComparison.Ordinal);
        Assert.DoesNotContain("is back in scroll mode", text, StringComparison.Ordinal);

        // The percent it did read is reported rather than dropped, but after the
        // fix, so it cannot displace it.
        Assert.Contains("57%", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf(ModeFlipView.RestoreItemLabel, StringComparison.Ordinal)
                < text.IndexOf("57%", StringComparison.Ordinal),
            "the battery level is shown before the recovery action");
    }

    // A reading that never arrived is unknown, not a fault, and above all not a
    // failed restore: this text must not send a user with a working wheel off
    // to recover it.
    [Fact]
    public void BatteryUnreadable_WithVerifiedRestore_DoesNotReadAsAFailedRestore()
    {
        var text = ModeFlipView.ResultText(Result(ModeFlipOutcome.BatteryUnreadable, -1, true));

        Assert.Contains("is back in scroll mode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("may be dead", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not confirm", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ModeFlipView.RestoreItemLabel, text, StringComparison.Ordinal);
    }

    // Same outcome, unverified restore: now the warning and the fix are
    // mandatory. Without this the flag would be free to be ignored on every
    // branch except RestoreFailed.
    [Fact]
    public void BatteryUnreadable_WithoutVerifiedRestore_WarnsAndOffersTheFix()
    {
        var text = ModeFlipView.ResultText(Result(ModeFlipOutcome.BatteryUnreadable, -1, false));

        Assert.Contains("may be dead", text, StringComparison.Ordinal);
        Assert.Contains(ModeFlipView.RestoreItemLabel, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ok_ShowsThePercentWhenThereIsOne_AndTheRestoreOnItsOwn()
    {
        var read = ModeFlipView.ResultText(Result(ModeFlipOutcome.Ok, 57, true));
        Assert.Contains("57%", read, StringComparison.Ordinal);
        Assert.Contains("is back in scroll mode", read, StringComparison.Ordinal);

        // RestoreModeB() also yields Ok, with nothing read. A percent sentinel
        // must not surface as a battery level.
        var restoreOnly = ModeFlipView.ResultText(Result(ModeFlipOutcome.Ok, -1, true));
        Assert.DoesNotContain("%", restoreOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("-1", restoreOnly, StringComparison.Ordinal);
        Assert.Contains("is back in scroll mode", restoreOnly, StringComparison.Ordinal);
    }

    // Consent is only informed if all three costs are on the screen that asks.
    [Fact]
    public void OfferText_StatesEveryCostBeforeTheUserAgrees()
    {
        var text = ModeFlipView.OfferText("0323");

        Assert.Contains("0323", text, StringComparison.Ordinal);
        // The pause, and that it covers pointing as well as scrolling.
        Assert.Contains("10-20 seconds", text, StringComparison.Ordinal);
        Assert.Contains("pointer", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scrolling stop", text, StringComparison.Ordinal);
        // Exactly one elevation, said as one.
        Assert.Contains("one administrator prompt", text, StringComparison.Ordinal);
        // The guarantee that makes the cost acceptable.
        Assert.Contains("always puts the mouse back", text, StringComparison.Ordinal);

        // An empty pid must not render as an empty pair of brackets.
        Assert.DoesNotContain("()", ModeFlipView.OfferText(""), StringComparison.Ordinal);
    }

    [Fact]
    public void StartupRestoreOffer_SaysWhyItIsAskingAndWhatOneClickDoes()
    {
        var text = ModeFlipView.StartupRestoreOffer();

        Assert.StartsWith("Scrolling may be dead right now.", text, StringComparison.Ordinal);
        Assert.Contains("did not finish", text, StringComparison.Ordinal);
        Assert.Contains("one click", text, StringComparison.Ordinal);
        Assert.Contains("administrator", text, StringComparison.OrdinalIgnoreCase);
    }

    // A passive-looking label gets clicked by someone who did not agree to lose
    // their pointer; the cost belongs in the label itself.
    [Fact]
    public void MenuItemLabel_ReadsAsAnActionWithACost()
    {
        var label = ModeFlipView.MenuItemLabel();

        Assert.StartsWith("Read battery", label, StringComparison.Ordinal);
        Assert.Contains("10-20 seconds", label, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", label, StringComparison.Ordinal);
    }

    // Plain ASCII, and none of the vocabulary that lives in ModeFlip's comments:
    // the user is not told about registry values, pnputil, or the internal
    // Mode A / Mode B names.
    [Fact]
    public void EveryString_IsPlainAsciiAndFreeOfInternalJargon()
    {
        foreach (var text in AllUserText())
        {
            foreach (var c in text)
            {
                Assert.True(
                    c == '\n' || (c >= 0x20 && c < 0x7F),
                    $"non-ascii U+{(int)c:X4} in: {text}");
            }

            foreach (var jargon in new[]
                     {
                         "LowerFilters", "pnputil", "col02", "BTHENUM", "REG_MULTI_SZ",
                         "registry", "devnode", "sentinel", "HID",
                     })
            {
                Assert.DoesNotContain(jargon, text, StringComparison.OrdinalIgnoreCase);
            }

            // Word-bounded so "scroll mode again" is not a false hit.
            Assert.False(
                Regex.IsMatch(text, @"\bMode [AB]\b", RegexOptions.IgnoreCase),
                $"bare internal mode name in: {text}");
        }
    }

    static IEnumerable<string> AllUserText()
    {
        yield return ModeFlipView.MenuItemLabel();
        yield return ModeFlipView.OfferText("0323");
        yield return ModeFlipView.OfferText("");
        yield return ModeFlipView.StartupRestoreOffer();

        foreach (var outcome in AllOutcomes())
        {
            yield return ModeFlipView.ResultText(Result(outcome, 57, true));
            yield return ModeFlipView.ResultText(Result(outcome, -1, false));
        }
    }
}
