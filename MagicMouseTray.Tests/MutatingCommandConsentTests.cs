// SPDX-License-Identifier: MIT
using System.Linq;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

/// <summary>
/// Every tray command that changes the device or its driver states its cost
/// and asks first. "Restore scroll mode" did not.
/// </summary>
/// <remarks>
/// The tray already holds that line everywhere else it matters: the battery
/// flip shows <see cref="ModeFlipView.OfferText"/> before it elevates, the
/// startup recovery shows <see cref="ModeFlipView.StartupRestoreOffer"/>, and
/// the two Windows device commands show their own prompts. The restore item
/// sitting one row under the flip ran straight into an elevated registry write
/// and a device restart on a single click - the same cost as the flip, with
/// none of the consent, and with nothing on screen saying the pointer was
/// about to stop.
///
/// These are the commands the menu asks about itself, and the menu is built
/// out of exactly these entries: the flip item takes its label and its dialog
/// from <see cref="TrayMenu.ReadBatteryCommand"/>, the restore from
/// <see cref="TrayMenu.RestoreScrollModeCommand"/>, and the tray's own
/// ApplyWindowsDeviceState from the two Windows device entries - each of them
/// through <see cref="TrayMenu.Consented"/>, which is the gate itself and is
/// pinned below. The driver radios and "Fix battery
/// reads" are not here because their consent is raised one level down, in
/// DriverInstaller.Offer*.
///
/// So a command added later with no confirmation text fails here AND is
/// refused at runtime rather than running unasked - the gate is the rule, not
/// a habit.
/// </remarks>
public class MutatingCommandConsentTests
{
    // The 0323 Magic Mouse on the patched Apple driver: the only device that
    // has both the flip and the restore, so it is the device whose command set
    // is complete.
    const string Pid = "0323";

    // Asked with a recorded setting to put back, which is the branch where
    // every one of these commands really does elevate - the branch these
    // tests are about. The no-sentinel restore wording is pinned on its own
    // below.
    static TrayDeviceCommand Command(string id)
    {
        var matches = TrayMenu.MutatingCommands(Pid, hasRecordedSetting: true)
            .Where(c => string.Equals(c.Id, id, StringComparison.Ordinal))
            .ToList();
        return Assert.Single(matches);
    }

    [Fact]
    public void MutatingCommands_EveryOneOfThemAsksBeforeItChangesAnything()
    {
        var commands = TrayMenu.MutatingCommands(Pid, hasRecordedSetting: true);
        Assert.NotEmpty(commands);

        foreach (var command in commands)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(command.Label),
                $"command {command.Id} has no menu label");
            Assert.False(
                string.IsNullOrWhiteSpace(command.ConfirmText),
                $"command {command.Id} changes the device with no confirmation");

            var confirm = command.ConfirmText!;

            // A consent prompt asks; it does not announce something already
            // under way.
            Assert.Contains("?", confirm, StringComparison.Ordinal);

            // Each of these runs elevated, and the cost is stated before the
            // user agrees to it, never after Windows has raised the prompt.
            Assert.Contains("administrator", confirm, StringComparison.OrdinalIgnoreCase);

            // The row label is not consent: a dialog that only repeats the
            // item the user just clicked tells them nothing new.
            Assert.NotEqual(command.Label, confirm);
            Assert.True(
                confirm.Length > TrayMenu.MenuTextMaxChars,
                $"command {command.Id} confirmation is menu-row length, not a stated cost");
        }

        // One command may not borrow another's consent: the user must be told
        // what THIS click does.
        Assert.Equal(
            commands.Count,
            commands.Select(c => c.ConfirmText).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            commands.Count,
            commands.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void MutatingCommands_CoverTheCommandsTheMenuActuallyRuns()
    {
        var ids = TrayMenu.MutatingCommands(Pid, hasRecordedSetting: true).Select(c => c.Id).ToList();

        Assert.Contains(TrayMenu.CommandReadBattery, ids);
        Assert.Contains(TrayMenu.CommandRestoreScrollMode, ids);
        Assert.Contains(TrayMenu.CommandStartInWindows, ids);
        Assert.Contains(TrayMenu.CommandStopInWindows, ids);

        // Each entry carries the label of a real menu item, so the table
        // cannot describe commands the menu does not have - or miss the
        // wording when one of them is renamed.
        Assert.Equal(ModeFlipView.MenuItemLabel(), Command(TrayMenu.CommandReadBattery).Label);
        Assert.Equal(ModeFlipView.RestoreItemLabel, Command(TrayMenu.CommandRestoreScrollMode).Label);
        Assert.Equal(TrayMenu.StartInWindows, Command(TrayMenu.CommandStartInWindows).Label);
        Assert.Equal(TrayMenu.StopInWindows, Command(TrayMenu.CommandStopInWindows).Label);
    }

    [Fact]
    public void MutatingCommands_RestoreScrollMode_IsGatedExactlyLikeTheFlip()
    {
        var flip = Command(TrayMenu.CommandReadBattery);
        var restore = Command(TrayMenu.CommandRestoreScrollMode);

        Assert.Equal(ModeFlipView.OfferText(Pid), flip.ConfirmText);
        Assert.Equal(ModeFlipView.RestoreOfferText(hasRecordedSetting: true), restore.ConfirmText);

        // Same gate, different sentence: the restore takes no reading, so the
        // flip's offer would describe a cost this command does not pay and
        // promise a percentage it never produces.
        Assert.NotEqual(flip.ConfirmText, restore.ConfirmText);

        // Nor is it the startup offer: that one is shown because scrolling may
        // ALREADY be dead, which is a different thing to tell a user who is
        // about to stop a working mouse on purpose.
        Assert.NotEqual(ModeFlipView.StartupRestoreOffer(), restore.ConfirmText);
    }

    [Fact]
    public void MutatingCommands_WindowsDeviceCommands_KeepThePromptsTheyAlreadyHad()
    {
        Assert.Equal(TrayMenu.StartInWindowsPrompt, Command(TrayMenu.CommandStartInWindows).ConfirmText);
        Assert.Equal(TrayMenu.StopInWindowsPrompt, Command(TrayMenu.CommandStopInWindows).ConfirmText);
    }

    [Fact]
    public void MutatingCommands_LeaveTheTrayOnlySwitchAlone()
    {
        var commands = TrayMenu.MutatingCommands(Pid, hasRecordedSetting: true);

        // "Show in Magic Tray" writes enabled_<pid> in config.ini and nothing
        // else: no elevation, no PnP change, nothing that can fail. Gating it
        // would teach the user to click through dialogs that never mattered,
        // which is how the ones that do matter stop being read.
        Assert.DoesNotContain(
            commands,
            c => string.Equals(c.Label, TrayMenu.ShowInTray, StringComparison.Ordinal));

        // Nor is the already-live answer a command: it is what the tray says
        // INSTEAD of asking, because nothing runs in that case.
        Assert.DoesNotContain(
            commands,
            c => string.Equals(c.ConfirmText, TrayMenu.AlreadyStartedInWindows, StringComparison.Ordinal));
    }

    [Fact]
    public void RestoreOfferText_WithARecordedSetting_NamesEveryCostBeforeConsent()
    {
        var text = ModeFlipView.RestoreOfferText(hasRecordedSetting: true);

        Assert.Contains("?", text, StringComparison.Ordinal);

        // The three things this click actually costs, in the order the user
        // pays them: one approval, one setting written, one device restart.
        Assert.Contains("one administrator", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("setting", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart", text, StringComparison.OrdinalIgnoreCase);

        // What it is for, in this file's vocabulary, and what refusing does.
        Assert.Contains("scroll mode", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cancel", text, StringComparison.Ordinal);

        // It restores; it does not read. Promising a battery level here would
        // be the flip's offer on the wrong command.
        Assert.DoesNotContain("battery level", text, StringComparison.OrdinalIgnoreCase);

        // And it may not claim the end state in advance: only a verified
        // restore is allowed to say the mouse is back (ModeFlipView rule 2),
        // and this text is shown before anything has run at all.
        Assert.DoesNotContain("is back in scroll mode", text, StringComparison.OrdinalIgnoreCase);

        // A dialog, not a row: the cost does not fit in a menu item and must
        // never be clamped into one.
        Assert.True(text.Length > TrayMenu.MenuTextMaxChars);
        Assert.NotEqual(ModeFlipView.RestoreItemLabel, text);
    }

    [Fact]
    public void RestoreOfferText_WithNothingRecorded_ChargesNoneOfThoseCosts()
    {
        var text = ModeFlipView.RestoreOfferText(hasRecordedSetting: false);

        // ModeFlip.RestoreModeBCore returns on this path after looking at the
        // mouse: no elevation, no registry write, no device restart. The
        // offer that leads into it may not charge for them - a user whose
        // wheel may already be dead would be talked out of a free look.
        Assert.Contains("?", text, StringComparison.Ordinal);
        Assert.Contains("No administrator approval", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no setting is written", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not restarted", text, StringComparison.OrdinalIgnoreCase);

        // Still an offer in this file's vocabulary, and refusing is still free.
        Assert.Contains("scroll mode", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cancel", text, StringComparison.Ordinal);

        // Rule 2 holds on this branch too: nothing has run, so nothing may be
        // claimed about the end state.
        Assert.DoesNotContain("is back in scroll mode", text, StringComparison.OrdinalIgnoreCase);

        // And it is not the recorded-setting offer, which states three costs
        // this click does not spend.
        Assert.NotEqual(ModeFlipView.RestoreOfferText(hasRecordedSetting: true), text);
    }

    [Fact]
    public void Consented_AsksWithThisCommandsOwnTextAndOnlyYesGetsThrough()
    {
        var command = Command(TrayMenu.CommandRestoreScrollMode);

        string? shown = null;
        Assert.True(TrayMenu.Consented(command, t => { shown = t; return true; }));
        Assert.Equal(command.ConfirmText, shown);

        // Anything that is not an approval is a refusal, and the elevated
        // action the click handler runs sits behind this false.
        shown = null;
        Assert.False(TrayMenu.Consented(command, t => { shown = t; return false; }));
        Assert.Equal(command.ConfirmText, shown);
    }

    [Fact]
    public void Consented_ACommandWithNoConsentTextIsNeverAskedAndNeverRuns()
    {
        var asked = false;
        bool Ask(string _) { asked = true; return true; }

        Assert.False(TrayMenu.Consented(new TrayDeviceCommand("unworded", "Do it", null), Ask));
        Assert.False(asked);

        // Blank is the same defect wearing a string: there is nothing on
        // screen for the user to agree to.
        Assert.False(TrayMenu.Consented(new TrayDeviceCommand("unworded", "Do it", "   "), Ask));
        Assert.False(asked);
    }
}
