// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class TrayMenuTests
{
    [Fact]
    public void ProductName_IsMagicTray()
    {
        Assert.Equal("Magic Tray", TrayMenu.ProductName);
    }

    [Theory]
    [InlineData("HidBth", "Bound: HidBth")]
    [InlineData("applewirelessmouse", "Bound: applewirelessmouse")]
    [InlineData("MagicMouseDriver", "Bound: MagicMouseDriver")]
    [InlineData(null, "Bound: (none)")]
    [InlineData("", "Bound: (none)")]
    public void BoundLabel_ShowsNameOrNone(string? name, string expected)
    {
        Assert.Equal(expected, TrayMenu.BoundLabel(name));
    }

    [Theory]
    [InlineData(DeviceKind.MagicMouseV1, "030d", DriverStatus.NotBound, false)]
    [InlineData(DeviceKind.MagicMouseV2, "0269", DriverStatus.NotInstalled, false)]
    [InlineData(DeviceKind.MagicMouseV1, "030d", DriverStatus.Ok, false)]
    [InlineData(DeviceKind.MagicMouseV3, "0323", DriverStatus.NotBound, false)]
    [InlineData(DeviceKind.MagicMouseV3, "0323", DriverStatus.StockKmdf, false)]
    [InlineData(DeviceKind.MagicMouseV3, "0323", DriverStatus.PathAPatched, false)]
    [InlineData(DeviceKind.MagicKeyboard, "0239", DriverStatus.NotBound, false)]
    public void ShowFixScroll_FalseOnceV1V2RadiosExist(DeviceKind kind, string pid, DriverStatus status, bool expected)
    {
        Assert.Equal(expected, TrayMenu.ShowFixScroll(kind, status));
        Assert.Equal(kind == DeviceKind.MagicMouseV3, TrayMenu.IsV3(kind, pid));
    }

    [Theory]
    [InlineData(DeviceKind.MagicKeyboard, -2, true)]
    [InlineData(DeviceKind.MagicKeyboard, -1, false)]
    [InlineData(DeviceKind.MagicKeyboard, 40, false)]
    [InlineData(DeviceKind.MagicMouseV3, -2, false)]
    public void ShowFixKeyboard_OnlyOnBlockedSentinel(DeviceKind kind, int pct, bool expected)
    {
        Assert.Equal(expected, TrayMenu.ShowFixKeyboard(kind, pct));
    }

    [Fact]
    public void ShowBatteryReads_OnlyWhenV3Patched()
    {
        Assert.True(TrayMenu.ShowBatteryReads(true, DriverStatus.PatchedKmdf));
        Assert.False(TrayMenu.ShowBatteryReads(true, DriverStatus.StockKmdf));
        Assert.False(TrayMenu.ShowBatteryReads(true, DriverStatus.NotBound));
        Assert.False(TrayMenu.ShowBatteryReads(true, DriverStatus.PathAPatched));
        Assert.False(TrayMenu.ShowBatteryReads(false, DriverStatus.PatchedKmdf));
    }

    [Theory]
    [InlineData(DriverStatus.PatchedKmdf, "KMDF")]
    [InlineData(DriverStatus.PathAPatched, "Patched Apple")]
    [InlineData(DriverStatus.StockKmdf, "Stock")]
    [InlineData(DriverStatus.NotBound, "Not bound")]
    [InlineData(DriverStatus.Ok, "Not bound")]
    [InlineData(null, "Not bound")]
    public void V3Badge_MapsPerDeviceStatus(DriverStatus? status, string expected)
    {
        Assert.Equal(expected, TrayMenu.V3Badge(status));
    }

    [Fact]
    public void IconAttention_0323NotBoundIsFalseNegative()
    {
        Assert.False(TrayMenu.IconAttention("0323", DriverStatus.NotBound));
        Assert.False(TrayMenu.IconAttention("0323", DriverStatus.StockKmdf));
        Assert.False(TrayMenu.IconAttention("0323", DriverStatus.PatchedKmdf));
        Assert.True(TrayMenu.IconAttention("0323", DriverStatus.UnknownAppleMouse));
        Assert.True(TrayMenu.IconAttention("030d", DriverStatus.NotBound));
        Assert.False(TrayMenu.IconAttention("030d", DriverStatus.NotInstalled));
        Assert.False(TrayMenu.IconAttention("030d", DriverStatus.Ok));
    }

    [Fact]
    public void RowLabel_IncludesNameBatteryAndBadge()
    {
        var label = TrayMenu.RowLabel("Magic Mouse 2024", 54, "KMDF", "", null);
        Assert.Contains("Magic Mouse 2024", label);
        Assert.Contains("54%", label);
        Assert.Contains("KMDF", label);
        Assert.DoesNotContain("Mode", label);
        Assert.DoesNotContain("recycle", label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH-A", label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShowFixScroll_StillFalseFor0323()
    {
        Assert.False(TrayMenu.ShowFixScroll(DeviceKind.MagicMouseV3, DriverStatus.NotBound));
        Assert.False(TrayMenu.ShowFixScroll(DeviceKind.MagicMouseV3, DriverStatus.StockKmdf));
        Assert.False(TrayMenu.ShowFixScroll(DeviceKind.MagicMouseV3, DriverStatus.PatchedKmdf));
        Assert.False(TrayMenu.ShowFixScroll(DeviceKind.MagicMouseV3, DriverStatus.PathAPatched));
    }

    [Fact]
    public void V3DriverRadios_ExactCopy_NoPathA()
    {
        Assert.Equal(new[] { "KMDF", "Patched Apple driver", "Stock Windows" }, TrayMenu.V3DriverRadioLabels);
        Assert.Equal("KMDF", TrayMenu.V3RadioKmdf);
        Assert.Equal("Patched Apple driver", TrayMenu.V3RadioPatchedApple);
        Assert.Equal("Stock Windows", TrayMenu.V3RadioStockWindows);
        foreach (var label in TrayMenu.V3DriverRadioLabels)
            Assert.DoesNotContain("PATH-A", label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH-A", TrayMenu.V3Badge(DriverStatus.PathAPatched)!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DriverStatus.PatchedKmdf, "KMDF")]
    [InlineData(DriverStatus.PathAPatched, "Patched Apple driver")]
    [InlineData(DriverStatus.StockKmdf, "Stock Windows")]
    [InlineData(DriverStatus.NotBound, null)]
    [InlineData(null, null)]
    public void V3CheckedDriverRadio_FromClassify(DriverStatus? status, string? expected)
    {
        Assert.Equal(expected, TrayMenu.V3CheckedDriverRadio(status));
        if (expected != null)
            Assert.DoesNotContain("PATH-A", expected, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DeviceKind.MagicMouseV3, "0323", DriverStatus.PathAPatched, true)]
    [InlineData(DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf, false)]
    [InlineData(DeviceKind.MagicMouseV3, "0323", DriverStatus.StockKmdf, false)]
    [InlineData(DeviceKind.MagicMouseV3, "0323", DriverStatus.NotBound, false)]
    [InlineData(DeviceKind.MagicMouseV1, "030d", DriverStatus.PathAPatched, false)]
    [InlineData(DeviceKind.MagicKeyboard, "0239", DriverStatus.PathAPatched, false)]
    public void ShowPathAModeSwitch_OnlyV3PathAPatched(DeviceKind kind, string pid, DriverStatus status, bool expected)
    {
        Assert.Equal(expected, TrayMenu.ShowPathAModeSwitch(kind, pid, status));
    }

    [Fact]
    public void PathAChoice_PatchedAppleBadge_AndFlipGate()
    {
        var status = DriverHealthChecker.Classify(
            "0323", "HidBth", appleFilterPackagePresent: true, kmdfPackagePresent: false,
            lastingChoice: Config.Driver0323PathA);
        Assert.Equal(DriverStatus.PathAPatched, status);
        Assert.True(TrayMenu.ShowPathAModeSwitch(DeviceKind.MagicMouseV3, "0323", status));
        Assert.Equal(TrayMenu.V3RadioPatchedApple, TrayMenu.V3CheckedDriverRadio(status));
        Assert.Equal("Patched Apple", TrayMenu.V3Badge(status));
        Assert.False(TrayMenu.ShowBatteryReads(true, status));
        Assert.False(TrayMenu.ShowFixScroll(DeviceKind.MagicMouseV3, status));
    }

    [Theory]
    [InlineData("030d", DriverStatus.Ok, true)]
    [InlineData("030d", DriverStatus.NotBound, true)]
    [InlineData("0323", DriverStatus.PathAPatched, true)]
    [InlineData("0323", DriverStatus.PatchedKmdf, true)]
    [InlineData("0323", DriverStatus.StockKmdf, true)]
    [InlineData("abcd", DriverStatus.UnknownAppleMouse, true)]
    [InlineData("030d", DriverStatus.UnknownAppleMouse, true)]
    [InlineData("030d", DriverStatus.NotInstalled, true)]
    [InlineData("030d", DriverStatus.Error, false)]
    [InlineData("0239", DriverStatus.Ok, false)]
    public void ShouldShowHealthRow_WhenNotAlreadyShown(string pid, DriverStatus status, bool expected)
    {
        Assert.Equal(expected, TrayMenu.ShouldShowHealthRow([], pid, status));
    }

    [Fact]
    public void ShouldShowHealthRow_FalseWhenPidAlreadyShown()
    {
        Assert.False(TrayMenu.ShouldShowHealthRow(["030d"], "030D", DriverStatus.Ok));
    }

    [Fact]
    public void HealthRow_UsesNoReadingSentinel()
    {
        Assert.True(MouseBatteryDevice.TryKnownMouse("030d", out var name, out var kind));
        Assert.Equal("Magic Mouse v1", name);
        Assert.Equal(DeviceKind.MagicMouseV1, kind);
        Assert.Equal("No reading", TrayMenu.BatteryText(-1, null));
        var label = TrayMenu.RowLabel(name, -1, null, "", null);
        Assert.Contains("Magic Mouse v1", label);
        Assert.Contains("No reading", label);
    }

    [Theory]
    [InlineData(DriverStatus.Ok, "Boot Camp")]
    [InlineData(DriverStatus.NotInstalled, "Stock")]
    [InlineData(DriverStatus.NotBound, "Not bound")]
    [InlineData(DriverStatus.Error, "Error")]
    [InlineData(null, "Not bound")]
    public void V1V2Badge_MapsPerDeviceStatus(DriverStatus? status, string expected)
    {
        Assert.Equal(expected, TrayMenu.V1V2Badge(status));
        AssertNoForbiddenV1V2Copy(TrayMenu.V1V2Badge(status)!);
    }

    [Fact]
    public void V1V2DriverRadios_ExactCopy_NoForbiddenTerms()
    {
        Assert.Equal(new[] { "Boot Camp", "Stock Windows" }, TrayMenu.V1V2DriverRadioLabels);
        Assert.Equal("Boot Camp", TrayMenu.V1V2RadioBootCamp);
        Assert.Equal("Stock Windows", TrayMenu.V1V2RadioStockWindows);
        foreach (var label in TrayMenu.V1V2DriverRadioLabels)
            AssertNoForbiddenV1V2Copy(label);
        AssertNoForbiddenV1V2Copy(TrayMenu.V1V2Badge(DriverStatus.Ok)!);
        AssertNoForbiddenV1V2Copy(TrayMenu.V1V2Badge(DriverStatus.NotInstalled)!);
        AssertNoForbiddenV1V2Copy(TrayMenu.V1V2Badge(DriverStatus.NotBound)!);
    }

    [Theory]
    [InlineData(DriverStatus.Ok, "Boot Camp")]
    [InlineData(DriverStatus.NotInstalled, "Stock Windows")]
    [InlineData(DriverStatus.NotBound, null)]
    [InlineData(null, null)]
    public void V1V2CheckedDriverRadio_FromClassify(DriverStatus? status, string? expected)
    {
        Assert.Equal(expected, TrayMenu.V1V2CheckedDriverRadio(status));
        if (expected != null)
            AssertNoForbiddenV1V2Copy(expected);
    }

    [Theory]
    [InlineData(DriverStatus.Ok, true)]
    [InlineData(DriverStatus.NotBound, true)]
    [InlineData(DriverStatus.NotInstalled, false)]
    [InlineData(null, true)]
    public void V1V2StockRadioEnabled_ClickableUnlessAlreadyStock(DriverStatus? status, bool expected)
    {
        Assert.Equal(expected, TrayMenu.V1V2StockRadioEnabled(status));
        Assert.Equal(status != DriverStatus.Ok, TrayMenu.V1V2BootCampRadioEnabled(status));
    }

    [Fact]
    public void V3DriverRadios_UnchangedByV1V2StockClick()
    {
        Assert.Equal(new[] { "KMDF", "Patched Apple driver", "Stock Windows" }, TrayMenu.V3DriverRadioLabels);
        Assert.Equal("KMDF", TrayMenu.V3CheckedDriverRadio(DriverStatus.PatchedKmdf));
        Assert.Equal("Patched Apple driver", TrayMenu.V3CheckedDriverRadio(DriverStatus.PathAPatched));
        Assert.Equal("Stock Windows", TrayMenu.V3CheckedDriverRadio(DriverStatus.StockKmdf));
        Assert.Null(TrayMenu.V3CheckedDriverRadio(DriverStatus.Ok));
        Assert.True(TrayMenu.ShowPathAModeSwitch(DeviceKind.MagicMouseV3, "0323", DriverStatus.PathAPatched));
        Assert.False(TrayMenu.V1V2StockRadioEnabled(DriverStatus.NotInstalled));
        Assert.True(TrayMenu.V1V2StockRadioEnabled(DriverStatus.Ok));
    }

    [Fact]
    public void ShowInTray_AndTheWindowsActions_SayWhichStateTheyChange()
    {
        // One checkbox labelled "Enabled on this PC" used to drive BOTH the
        // tray's own enabled_<pid> config and an elevated pnputil call, and a
        // user could not tell which it meant. The tray-side switch must not
        // claim to change the PC, and the two elevated commands must name
        // Windows outright.
        Assert.Equal("Show in Magic Tray", TrayMenu.ShowInTray);
        Assert.DoesNotContain("PC", TrayMenu.ShowInTray, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows", TrayMenu.ShowInTray, StringComparison.Ordinal);
        Assert.DoesNotContain("PATH-A", TrayMenu.ShowInTray, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Disconnect", TrayMenu.ShowInTray, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ignore", TrayMenu.ShowInTray, StringComparison.OrdinalIgnoreCase);

        foreach (var label in new[] { TrayMenu.StartInWindows, TrayMenu.StopInWindows })
            Assert.Contains("Windows", label, StringComparison.Ordinal);

        // Consent is asked only for the two that really are elevated, and it
        // says so before anything runs.
        Assert.Contains("administrator approval", TrayMenu.StartInWindowsPrompt, StringComparison.Ordinal);
        Assert.Contains("administrator approval", TrayMenu.StopInWindowsPrompt, StringComparison.Ordinal);

        // The already-live answer promises the opposite: nothing ran at all.
        // It is shown instead of a consent prompt, so it must not imply one.
        Assert.Contains("nothing to change", TrayMenu.AlreadyStartedInWindows, StringComparison.Ordinal);
        Assert.Contains("Nothing was run", TrayMenu.AlreadyStartedInWindows, StringComparison.Ordinal);

        // A UAC prompt the user never saw was blamed for a pnputil exit code
        // (DEVICE_ENABLE pid=030d exit=1). No tray-owned copy mentions UAC;
        // the only sentence that may is DeviceEnable's own UacDeclined detail.
        foreach (var text in new[]
        {
            TrayMenu.ShowInTray, TrayMenu.StartInWindows, TrayMenu.StopInWindows,
            TrayMenu.StartInWindowsPrompt, TrayMenu.StopInWindowsPrompt,
            TrayMenu.AlreadyStartedInWindows,
        })
        {
            Assert.DoesNotContain("UAC", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cancel", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(DeviceKind.MagicTrackpadV1, "030e")]
    [InlineData(DeviceKind.MagicTrackpadV2, "0265")]
    [InlineData(DeviceKind.MagicTrackpadV3, "0324")]
    public void TrackpadKinds_AreNotMouseDriverRadios(DeviceKind kind, string pid)
    {
        Assert.False(TrayMenu.IsV1V2Mouse(kind));
        Assert.False(TrayMenu.IsV3(kind, pid));
        Assert.False(TrayMenu.ShowPathAModeSwitch(kind, pid, DriverStatus.PathAPatched));
        Assert.False(TrayMenu.ShowFixScroll(kind, DriverStatus.NotBound));
        Assert.False(TrayMenu.ShouldShowHealthRow([], pid, DriverStatus.Ok));
        Assert.False(TrayMenu.ShouldShowHealthRow([], pid, DriverStatus.NotBound));
        Assert.Equal(kind == DeviceKind.MagicTrackpadV1,
            TrayMenu.ShowTrackpadV1BootCamp(kind, pid));
    }

    [Fact]
    public void DriverChoicesLabel_ReadsAsAChoiceNotAReading()
    {
        Assert.Equal("What each driver would give", TrayMenu.DriverChoicesLabel);
        // The observed capability rows are "<Capability>: <state>" and their
        // vocabulary judges ("working", "not working"). This label collapses
        // PREDICTIONS about drivers the user is not on, so it must carry neither
        // that shape nor that vocabulary or the two blocks read as one.
        Assert.DoesNotContain(":", TrayMenu.DriverChoicesLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("working", TrayMenu.DriverChoicesLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unknown", TrayMenu.DriverChoicesLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH-A", TrayMenu.DriverChoicesLabel, StringComparison.OrdinalIgnoreCase);
    }

    // Every string TrayApp puts in a ToolStripItem.Text passes through
    // TrayMenu.MenuText. The cap exists because of this session's
    // screenshots: DriverAdvisor's advice paragraphs reached .Text and drew as
    // unbroken rows across the whole 1568px screen, overlapping the rest of
    // the UI. These are the properties that stop that from coming back.
    [Fact]
    public void MenuText_ClampsAParagraphToOneShortLine()
    {
        // The keyboard row from the screenshots, ~190 characters on one line.
        const string paragraph =
            "The driver bound to this device has not been read yet, so what works cannot be "
            + "confirmed. Recommended: Stock Windows - Windows' own keyboard driver is the only "
            + "choice. There is no scroll driver for a keyboard.";

        var clamped = TrayMenu.MenuText(paragraph);

        Assert.True(
            clamped.Length <= TrayMenu.MenuTextMaxChars,
            $"clamped to {clamped.Length} chars, cap is {TrayMenu.MenuTextMaxChars}");
        Assert.EndsWith("...", clamped, StringComparison.Ordinal);
        Assert.StartsWith("The driver bound to this device", clamped, StringComparison.Ordinal);
        // Cut on a word boundary, so the row reads as a shortened phrase.
        Assert.DoesNotContain(" ...", clamped, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuText_FlattensBreaksAndKeepsColumnSpacing()
    {
        // A line break in a menu item is what turns one row into a ragged one.
        Assert.Equal("one two", TrayMenu.MenuText("one\r\ntwo"));
        Assert.Equal("one two", TrayMenu.MenuText("  one\ttwo  "));
        Assert.Equal(string.Empty, TrayMenu.MenuText(null));
        Assert.Equal(string.Empty, TrayMenu.MenuText("   "));

        // Runs of plain spaces are column separators in the device rows and
        // the threshold labels. Collapsing them would silently reformat every
        // row in the menu, so MenuText must leave them exactly as they are.
        Assert.Equal("10%  then time alerts", TrayMenu.MenuText(TrayMenu.GlobalThresholdLabel(10)));
        Assert.Equal(
            "Magic Mouse    54%",
            TrayMenu.MenuText(TrayMenu.RowLabel("Magic Mouse", 54, null, "", null)));
    }

    [Fact]
    public void MenuText_LeavesEveryShortFormTheMenuShowsUntouched()
    {
        (DeviceKind Kind, string Pid)[] devices =
        [
            (DeviceKind.MagicMouseV3, "0323"),
            (DeviceKind.MagicMouseV1, "030d"),
            (DeviceKind.MagicMouseV2, "0269"),
            (DeviceKind.MagicKeyboard, "0239"),
            (DeviceKind.MagicTrackpadV1, "030e"),
            (DeviceKind.LogitechMouse, "c52b"),
        ];
        DriverStatus?[] statuses = [null, .. Enum.GetValues<DriverStatus>().Cast<DriverStatus?>()];

        Assert.Equal(DriverAdviceView.OptionsHeader(), TrayMenu.MenuText(DriverAdviceView.OptionsHeader()));

        foreach (var (kind, pid) in devices)
        {
            foreach (var row in DriverAdviceView.OptionRowsShort(kind, pid))
                Assert.Equal(row, TrayMenu.MenuText(row));

            foreach (var status in statuses)
            {
                // The short forms are what the menu draws: if one ever grows
                // past the cap it would be silently truncated, so it must fit
                // untouched. This is the half of the guard that fails when the
                // copy side regresses rather than the layout side.
                var line = DriverAdviceView.AdviceShort(kind, pid, status);
                Assert.Equal(line, TrayMenu.MenuText(line));

                // And the paragraph is exactly what must never reach an item.
                var full = DriverAdviceView.AdviceFull(kind, pid, status);
                if (full.Length > TrayMenu.MenuTextMaxChars)
                    Assert.NotEqual(full, TrayMenu.MenuText(full));
            }
        }
    }

    // The advice row is clickable because the paragraph had to go somewhere.
    // This is what the click shows, and it is the only place the long
    // per-driver sentences are allowed to appear.
    [Fact]
    public void AdviceDialogText_CarriesTheParagraphAndEveryFullDriverRow()
    {
        const DeviceKind kind = DeviceKind.MagicMouseV3;
        const string pid = "0323";

        var text = TrayMenu.AdviceDialogText(kind, pid, DriverStatus.StockKmdf);

        Assert.StartsWith(
            DriverAdviceView.AdviceFull(kind, pid, DriverStatus.StockKmdf), text,
            StringComparison.Ordinal);
        Assert.Contains(DriverAdviceView.OptionsHeader(), text, StringComparison.Ordinal);
        foreach (var row in DriverAdviceView.OptionRows(kind, pid))
            Assert.Contains(row, text, StringComparison.Ordinal);

        // A dialog wraps, a menu item does not: this text is the one string in
        // the app that is deliberately far longer than the menu cap.
        Assert.True(text.Length > TrayMenu.MenuTextMaxChars);

        // A device this app binds no driver for has no choices to predict, so
        // the dialog is the advice and nothing invented.
        var noChoices = TrayMenu.AdviceDialogText(DeviceKind.LogitechMouse, "c52b", DriverStatus.Ok);
        Assert.Equal(
            DriverAdviceView.AdviceFull(DeviceKind.LogitechMouse, "c52b", DriverStatus.Ok),
            noChoices);
    }

    // The devices whose driver had to be READ instead of classified.
    // DriverHealthChecker skips these PIDs, so this is the set whose rows take
    // their driver from StockDriverReader; a mouse must never be in it, or the
    // menu would have two competing answers for one device.
    [Theory]
    [InlineData(DeviceKind.MagicKeyboard, "0239", true)]
    [InlineData(DeviceKind.MagicKeyboard, null, true)]
    [InlineData(DeviceKind.MagicTrackpadV1, "030e", true)]
    [InlineData(DeviceKind.MagicTrackpadV2, "0265", true)]
    [InlineData(DeviceKind.MagicTrackpadV3, "0324", true)]
    [InlineData(DeviceKind.LogitechMouse, "c52b", true)]
    [InlineData(DeviceKind.MagicMouseV1, "030d", false)]
    [InlineData(DeviceKind.MagicMouseV2, "0269", false)]
    [InlineData(DeviceKind.MagicMouseV3, "0323", false)]
    // A 0323 that arrived with some other kind is still the v3 mouse.
    [InlineData(DeviceKind.MagicKeyboard, "0323", false)]
    public void ShowsStockDriverStory_CoversTheDevicesWithNoDriverSubmenu(
        DeviceKind kind, string? pid, bool expected)
    {
        Assert.Equal(expected, TrayMenu.ShowsStockDriverStory(kind, pid));
    }

    // Measured on the reference PC, 2026-09-16: Magic Keyboard 0239 runs
    // Microsoft's own stack - kbdhid from keyboard.inf on COL01, every node
    // Status OK - and its battery percent exists only because this repo's SDP
    // patch put a Feature cap on COL02.
    static readonly StockDriverInfo Keyboard0239 = new(
        Service: "kbdhid",
        InfPath: "keyboard.inf",
        Provider: "Microsoft",
        Version: "10.0.26100.8972",
        Stack: new[] { "kbdclass", "kbdhid", "HidBth" },
        AllNodesOk: true);

    // DriverHealthChecker never reads a keyboard, so status is null here and
    // the dialog used to open on "The driver bound to this device has not been
    // read yet, so what works cannot be confirmed." - which was false: the
    // driver is perfectly readable. With the reading in hand the dialog names
    // it instead.
    [Fact]
    public void AdviceDialogText_WithAReadDriver_NamesIt_AndNeverSaysNotReadYet()
    {
        const DeviceKind kind = DeviceKind.MagicKeyboard;
        const string pid = "0239";

        var text = TrayMenu.AdviceDialogText(
            kind, pid, status: null, Keyboard0239, SdpPatchState.Applied);

        Assert.StartsWith(
            DriverAdviceView.AdviceFull(kind, pid, null, Keyboard0239, SdpPatchState.Applied),
            text, StringComparison.Ordinal);
        Assert.Contains("kbdhid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("not been read yet", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not read yet", text, StringComparison.OrdinalIgnoreCase);

        // The predictions still follow the reading, in their own block.
        foreach (var row in DriverAdviceView.OptionRows(kind, pid))
            Assert.Contains(row, text, StringComparison.Ordinal);
    }

    // The menu row is the short form and the dialog is the long one. Whatever
    // the reading says, the row still has to fit a ToolStripItem: the whole
    // point of the split is that no advice text reaches .Text unclamped.
    [Fact]
    public void ReadDriverRow_StillFitsTheMenu_WhileTheDialogDoesNot()
    {
        foreach (var sdp in new SdpPatchState?[]
                 { null, SdpPatchState.Applied, SdpPatchState.NotApplied, SdpPatchState.Unknown })
        {
            var row = DriverAdviceView.AdviceShort(
                DeviceKind.MagicKeyboard, "0239", null, Keyboard0239, sdp);
            Assert.Equal(row, TrayMenu.MenuText(row));

            var dialog = TrayMenu.AdviceDialogText(
                DeviceKind.MagicKeyboard, "0239", null, Keyboard0239, sdp);
            Assert.True(dialog.Length > TrayMenu.MenuTextMaxChars);
        }
    }

    // A mouse has a DriverStatus and a bound filter name of its own, so the
    // stock reading must not reach its text at all - the tray hands every row
    // the same pair and this is what keeps the mouse rows byte-identical.
    [Fact]
    public void MouseRows_IgnoreTheStockReading()
    {
        foreach (var status in new DriverStatus?[]
                 { null, DriverStatus.PatchedKmdf, DriverStatus.StockKmdf, DriverStatus.NotBound })
        {
            Assert.Equal(
                TrayMenu.AdviceDialogText(DeviceKind.MagicMouseV3, "0323", status),
                TrayMenu.AdviceDialogText(
                    DeviceKind.MagicMouseV3, "0323", status, Keyboard0239, SdpPatchState.Applied));
        }
    }

    [Theory]
    [InlineData("https://magictray.app/v3.html", true)]
    [InlineData("http://example.test/page", true)]
    [InlineData(@"C:\pkg\mm-auto-f1-watcher.ps1", false)]
    [InlineData("mm-auto-f1-watcher.ps1", false)]
    [InlineData("file:///C:/pkg/install.cmd", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHelpUrl_OnlyHttpTargetsAreOpenable(string? target, bool expected)
    {
        // A ConfigFact may name a script that ships in a driver package. The
        // tray opens documentation and runs nothing, so anything that is not an
        // http(s) page must fail this gate rather than reach a shell execute.
        Assert.Equal(expected, TrayMenu.IsHelpUrl(target));
    }

    [Fact]
    public void ConfigSectionIsFault_OnlyBlockingBorrowsTheFaultColour()
    {
        var ok = new ConfigFact("a", "Test Mode is on", ConfigSeverity.Ok, "d", null, null);
        var advisory = new ConfigFact("b", "Package is not installed", ConfigSeverity.Advisory, "d", null, null);
        var blocking = new ConfigFact("c", "Test Mode is off", ConfigSeverity.Blocking, "d", null, null);

        Assert.False(TrayMenu.ConfigSectionIsFault([]));
        Assert.False(TrayMenu.ConfigSectionIsFault([ok]));
        // An Advisory - including the "we could not read it" kind - must never
        // compete with a measured device fault for the user's eye.
        Assert.False(TrayMenu.ConfigSectionIsFault([ok, advisory]));
        Assert.True(TrayMenu.ConfigSectionIsFault([advisory, blocking]));
    }


    [Fact]
    public void ThresholdChoices_Are10_5_1_GoingDown()
    {
        Assert.Equal(new[] { 10, 5, 1 }, Config.ThresholdChoices);
        Assert.DoesNotContain(15, Config.ThresholdChoices);
        Assert.DoesNotContain(20, Config.ThresholdChoices);
        Assert.DoesNotContain(25, Config.ThresholdChoices);
    }

    [Theory]
    [InlineData(10, "10%  then time alerts")]
    [InlineData(5, "5%  then time alerts")]
    [InlineData(1, "1%  then time alerts")]
    public void GlobalThresholdLabel_PercentThenTimeAlerts(int pct, string expected)
    {
        Assert.Equal(expected, TrayMenu.GlobalThresholdLabel(pct));
        Assert.DoesNotContain("~2 days", TrayMenu.GlobalThresholdLabel(pct));
        Assert.DoesNotContain("~24h", TrayMenu.GlobalThresholdLabel(pct));
    }

    [Fact]
    public void DeviceThresholdLabel_UnknownHours_IsBarePercent()
    {
        Assert.Equal("10%", TrayMenu.DeviceThresholdLabel(10, -1));
        Assert.Equal("5%", TrayMenu.DeviceThresholdLabel(5, 0));
        Assert.Equal("1%", TrayMenu.DeviceThresholdLabel(1, -1));
        Assert.DoesNotContain("~2 days", TrayMenu.DeviceThresholdLabel(10, -1));
        Assert.DoesNotContain("~24h", TrayMenu.DeviceThresholdLabel(10, -1));
    }

    [Theory]
    [InlineData(10, 8, "10%  (~8h)")]
    [InlineData(5, 20, "5%  (~20h)")]
    [InlineData(1, 24, "1%  (~1d)")]
    [InlineData(10, 48, "10%  (~2d)")]
    [InlineData(5, 36, "5%  (~2d)")]
    public void DeviceThresholdLabel_KnownHours_AppendsEta(int pct, double hours, string expected)
    {
        Assert.Equal(expected, TrayMenu.DeviceThresholdLabel(pct, hours));
    }

    /// <summary>
    /// Help menu labels and URLs stay exact: the bug and feature entries and the
    /// issues fallback must never point users at a dead or wrong page.
    /// </summary>
    [Fact]
    public void HelpUrls_AndLabels_AreExact()
    {
        Assert.Equal("Help/Documentation", TrayMenu.HelpMenuLabel);
        Assert.Equal("How alerts work", TrayMenu.HowAlertsWorkLabel);
        Assert.Equal("Repository", TrayMenu.RepositoryLabel);
        Assert.Equal("Report a bug", TrayMenu.ReportBugLabel);
        Assert.Equal("Request a feature", TrayMenu.RequestFeatureLabel);
        Assert.Equal("https://github.com/LesleyMurfin/magic-tray", TrayMenu.RepoUrl);
        Assert.Equal("https://github.com/LesleyMurfin/magic-tray/issues", TrayMenu.IssuesUrl);
        Assert.Equal(
            "https://github.com/LesleyMurfin/magic-tray/blob/main/docs/ALERTS.md",
            TrayMenu.AlertsDocUrl);
        Assert.Equal("https://github.com/LesleyMurfin/magic-tray/releases", TrayMenu.ReleasesUrl);
    }

    [Fact]
    public void FindLocalAlertsDoc_WalksUpToDocs()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-help-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "bin", "Debug");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        var doc = Path.Combine(root, "docs", "ALERTS.md");
        File.WriteAllText(doc, "# alerts");
        try
        {
            Assert.Equal(doc, TrayMenu.FindLocalAlertsDoc(nested));
            Assert.Null(TrayMenu.FindLocalAlertsDoc(Path.GetTempPath()));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    static ConfigFact Fact(string id, string title, ConfigSeverity severity) =>
        new(id, title, severity, title + " detail", null, null);

    [Fact]
    public void MergeConfigFacts_KeepsOnePackageRowPerDeviceState()
    {
        var v3 = new[]
        {
            Fact(SystemConfigChecker.TestModeFactId, "Test Mode is off", ConfigSeverity.Advisory),
            Fact(SystemConfigChecker.DriverPackageFactId,
                "Magic Mouse 2024 KMDF package is installed", ConfigSeverity.Ok),
        };
        var v1 = new[]
        {
            Fact(SystemConfigChecker.TestModeFactId, "Test Mode is off", ConfigSeverity.Blocking),
            Fact(SystemConfigChecker.DriverPackageFactId,
                "Boot Camp scroll driver is not installed", ConfigSeverity.Advisory),
        };

        var merged = TrayMenu.MergeConfigFacts(new[] { (IReadOnlyList<ConfigFact>)v3, v1 });

        // One machine-wide Test Mode row, the more severe reading kept, and
        // one honest package row per device state.
        Assert.Equal(3, merged.Count);
        Assert.Single(merged, f => f.Id == SystemConfigChecker.TestModeFactId);
        Assert.Equal(ConfigSeverity.Blocking,
            merged.First(f => f.Id == SystemConfigChecker.TestModeFactId).Severity);
        Assert.Equal(
            new[] { "Magic Mouse 2024 KMDF package is installed", "Boot Camp scroll driver is not installed" },
            merged.Where(f => f.Id == SystemConfigChecker.DriverPackageFactId).Select(f => f.Title));
    }

    [Fact]
    public void MergeConfigFacts_SamePackageStateTwiceIsOneRow()
    {
        var device = new[]
        {
            Fact(SystemConfigChecker.MemoryIntegrityFactId, "Memory integrity is off", ConfigSeverity.Ok),
            Fact(SystemConfigChecker.DriverPackageFactId,
                "Magic Mouse 2024 KMDF package is installed", ConfigSeverity.Ok),
        };

        var merged = TrayMenu.MergeConfigFacts(new[] { (IReadOnlyList<ConfigFact>)device, device });

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void MergeConfigFacts_MoreSevereMachineFactKeepsFirstSeenPosition()
    {
        var first = new[]
        {
            Fact(SystemConfigChecker.TestModeFactId, "Test Mode is off", ConfigSeverity.Advisory),
            Fact(SystemConfigChecker.DriverPackageFactId, "package is installed", ConfigSeverity.Ok),
        };
        var second = new[]
        {
            Fact(SystemConfigChecker.TestModeFactId, "Test Mode is off", ConfigSeverity.Blocking),
        };

        var merged = TrayMenu.MergeConfigFacts(new[] { (IReadOnlyList<ConfigFact>)first, second });

        // Position is first-seen, which is the order ConfigFactView.Ordered
        // sorts on top of; only the reading was replaced.
        Assert.Equal(SystemConfigChecker.TestModeFactId, merged[0].Id);
        Assert.Equal(ConfigSeverity.Blocking, merged[0].Severity);
        Assert.Equal(SystemConfigChecker.DriverPackageFactId, merged[1].Id);
    }

    [Fact]
    public void MergeConfigFacts_MilderReadingNeverDemotesMachineFact()
    {
        var blocking = new[]
        {
            Fact(SystemConfigChecker.MemoryIntegrityFactId, "Memory integrity is on", ConfigSeverity.Blocking),
        };
        var advisory = new[]
        {
            Fact(SystemConfigChecker.MemoryIntegrityFactId, "Memory integrity is on", ConfigSeverity.Advisory),
        };

        var merged = TrayMenu.MergeConfigFacts(new[] { (IReadOnlyList<ConfigFact>)blocking, advisory });

        Assert.Single(merged);
        Assert.Equal(ConfigSeverity.Blocking, merged[0].Severity);
    }

    // The strings measured live on the reference PC with all three devices
    // reporting (TRAY_UPDATE devices=3).
    const string Mouse2024 = "Magic Mouse 2024: 33%";
    const string Keyboard2011 = "Apple Wireless Keyboard (2011): 100%";
    const string MouseV1 = "Magic Mouse v1: 97%";
    const string Interval = " \u00b7 30m";

    static IReadOnlyList<TrayMenu.TooltipEntry> ThreeRealDevices() => new[]
    {
        new TrayMenu.TooltipEntry(Mouse2024, 33),
        new TrayMenu.TooltipEntry(Keyboard2011, 100),
        new TrayMenu.TooltipEntry(MouseV1, 97),
    };

    [Fact]
    public void Tooltip_ThreeRealDevicesAllFitTheMeasuredPlatformLimit()
    {
        var tip = TrayMenu.ComposeTooltip(ThreeRealDevices(), Interval);

        // This is the case that was silently losing the third device.
        Assert.Equal($"{Mouse2024} | {Keyboard2011} | {MouseV1}{Interval}", tip);
        Assert.True(tip.Length <= TrayMenu.TooltipMaxLength);
        Assert.DoesNotContain("more", tip);
    }

    [Fact]
    public void Tooltip_UnderPressureDropsWholeEntriesAndKeepsTheLowestBattery()
    {
        // 63 was the old clamp, and it is where the live string was cut in
        // half: three devices render to 88 characters.
        var tip = TrayMenu.ComposeTooltip(ThreeRealDevices(), Interval, 63);

        Assert.True(tip.Length <= 63, tip);
        Assert.Contains(Mouse2024, tip);            // the lowest reading survives
        Assert.Contains("+1 more", tip);            // and the drop is stated
        Assert.DoesNotContain(Keyboard2011, tip);   // the fullest battery went
        // No half entry anywhere: every segment is one the caller supplied.
        foreach (var part in tip.Split(Interval)[0].Split(" | "))
            Assert.True(part == Mouse2024 || part == MouseV1 || part == "+1 more", part);
    }

    [Fact]
    public void Tooltip_NeverEndsInADanglingSeparatorAtAnyWidth()
    {
        for (var max = 1; max <= 130; max++)
        {
            var tip = TrayMenu.ComposeTooltip(ThreeRealDevices(), Interval, max);
            Assert.True(tip.Length <= max, $"max={max} len={tip.Length} tip={tip}");
            Assert.DoesNotContain(" | |", tip);
            Assert.False(tip.EndsWith("|") || tip.EndsWith("| ") || tip.EndsWith(" | "),
                $"max={max} tip={tip}");
        }
    }

    [Fact]
    public void Tooltip_ExactBoundaryKeepsEverythingAndOneShortDrops()
    {
        var entries = new[]
        {
            new TrayMenu.TooltipEntry(Mouse2024, 33),
            new TrayMenu.TooltipEntry(MouseV1, 97),
            new TrayMenu.TooltipEntry("Keyboard: 5%", 5),
        };
        var full = $"{Mouse2024} | {MouseV1} | Keyboard: 5%{Interval}";
        Assert.Equal(64, full.Length);

        Assert.Equal(full, TrayMenu.ComposeTooltip(entries, Interval, 64));

        // One character short of the whole string: the interval is what goes,
        // and all three devices are still there.
        var tight = TrayMenu.ComposeTooltip(entries, Interval, 63);
        Assert.Equal($"{Mouse2024} | {MouseV1} | Keyboard: 5%", tight);
        Assert.DoesNotContain("more", tight);

        // Narrower still, and now a device has to go - the 5% is not it.
        var narrow = TrayMenu.ComposeTooltip(entries, Interval, 50);
        Assert.True(narrow.Length <= 50, narrow);
        Assert.Contains("Keyboard: 5%", narrow);
        Assert.Contains("+1 more", narrow);
        Assert.DoesNotContain(MouseV1, narrow);
    }

    [Fact]
    public void Tooltip_GivesUpTheIntervalBeforeItGivesUpADevice()
    {
        var entries = new[]
        {
            new TrayMenu.TooltipEntry(Mouse2024, 33),
            new TrayMenu.TooltipEntry("Keyboard: 5%", 5),
        };

        // Wide enough for both devices only if the interval goes.
        var tip = TrayMenu.ComposeTooltip(entries, Interval, 36);

        Assert.Equal($"{Mouse2024} | Keyboard: 5%", tip);
    }

    [Fact]
    public void Tooltip_ASentinelIsNotALowBatteryAndGoesFirst()
    {
        var entries = new[]
        {
            new TrayMenu.TooltipEntry("Magic Keyboard: blocked", -2),
            new TrayMenu.TooltipEntry("Magic Mouse 2024: 8%", 8),
        };

        var tip = TrayMenu.ComposeTooltip(entries, Interval, 30);

        Assert.Contains("Magic Mouse 2024: 8%", tip);
        Assert.DoesNotContain("blocked", tip);
        Assert.Contains("+1 more", tip);
    }

    [Fact]
    public void ClipTooltip_MarksTheCutInsteadOfLookingBroken()
    {
        Assert.Equal("abc", TrayMenu.ClipTooltip("abc", 3));
        Assert.Equal("ab...", TrayMenu.ClipTooltip("abcdefgh", 5));
        Assert.Equal(TrayMenu.TooltipMaxLength,
            TrayMenu.ClipTooltip(new string('x', 400)).Length);
    }

    static void AssertNoForbiddenV1V2Copy(string text)
    {
        Assert.DoesNotContain("PATH-A", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("applewirelessmouse", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KMDF", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tealtadpole", text, StringComparison.OrdinalIgnoreCase);
    }
}
