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
        var label = TrayMenu.RowLabel("Magic Mouse 2024", 54, "KMDF", "");
        Assert.Contains("Magic Mouse 2024", label);
        Assert.Contains("54%", label);
        Assert.Contains("KMDF", label);
        Assert.DoesNotContain("Mode", label);
        Assert.DoesNotContain("recycle", label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH-A", label, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DeviceKind.MagicMouseV3, DriverStatus.NotBound, 40, "Recommended: KMDF (MagicMouseDriver)")]
    [InlineData(DeviceKind.MagicMouseV3, DriverStatus.StockKmdf, 54, null)]
    [InlineData(DeviceKind.MagicMouseV3, DriverStatus.PathAPatched, 54, null)]
    [InlineData(DeviceKind.MagicMouseV3, DriverStatus.PatchedKmdf, 54, null)]
    [InlineData(DeviceKind.MagicMouseV1, DriverStatus.NotBound, 40, "Recommended: Boot Camp")]
    [InlineData(DeviceKind.MagicMouseV2, DriverStatus.NotInstalled, 40, null)]
    [InlineData(DeviceKind.MagicMouseV1, DriverStatus.Ok, 40, null)]
    [InlineData(DeviceKind.MagicKeyboard, DriverStatus.Ok, -2, "Recommended: SDP battery patch")]
    [InlineData(DeviceKind.MagicKeyboard, DriverStatus.Ok, 40, null)]
    public void RecommendedLabel_PerKind(DeviceKind kind, DriverStatus status, int pct, string? expected)
    {
        Assert.Equal(expected, TrayMenu.RecommendedLabel(kind, status, pct));
        if (expected != null)
        {
            Assert.DoesNotContain("PATH-A", expected, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("applewirelessmouse", expected, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tealtadpole", expected, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Mode", expected);
            Assert.DoesNotContain("recycle", expected, StringComparison.OrdinalIgnoreCase);
        }
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
    public void PathAChoice_ModeA_MenuStaysPatchedAppleWithScrollBattery()
    {
        var status = DriverHealthChecker.Classify(
            "0323", "HidBth", appleFilterPackagePresent: true, kmdfPackagePresent: false,
            lastingChoice: Config.Driver0323PathA);
        Assert.Equal(DriverStatus.PathAPatched, status);
        Assert.True(TrayMenu.ShowPathAModeSwitch(DeviceKind.MagicMouseV3, "0323", status));
        Assert.Equal(TrayMenu.V3RadioPatchedApple, TrayMenu.V3CheckedDriverRadio(status));
        Assert.Equal("Patched Apple", TrayMenu.V3Badge(status));
        Assert.Null(TrayMenu.RecommendedLabel(DeviceKind.MagicMouseV3, status, 54));
        Assert.False(TrayMenu.ShowBatteryReads(true, status));
        Assert.False(TrayMenu.ShowFixScroll(DeviceKind.MagicMouseV3, status));
    }

    [Fact]
    public void PathAModeRadios_ScrollBattery_NoPathA()
    {
        Assert.Equal("Scroll", TrayMenu.V3ModeRadioScroll);
        Assert.Equal("Battery", TrayMenu.V3ModeRadioBattery);
        Assert.DoesNotContain("PATH-A", TrayMenu.V3ModeRadioScroll, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH-A", TrayMenu.V3ModeRadioBattery, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal("No reading", TrayMenu.BatteryText(-1));
        var label = TrayMenu.RowLabel(name, -1, null, "");
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
        var rec = TrayMenu.RecommendedLabel(DeviceKind.MagicMouseV1, DriverStatus.NotBound, 40);
        Assert.Equal("Recommended: Boot Camp", rec);
        AssertNoForbiddenV1V2Copy(rec!);
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
    public void EnabledOnThisPc_ExactLabel_DefaultOn_NoForbiddenCopy()
    {
        Assert.Equal("Enabled on this PC", TrayMenu.EnabledOnThisPc);
        Assert.DoesNotContain("PATH-A", TrayMenu.EnabledOnThisPc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Disconnect", TrayMenu.EnabledOnThisPc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ignore", TrayMenu.EnabledOnThisPc, StringComparison.OrdinalIgnoreCase);
        Assert.True(TrayMenu.ShouldShowHealthRow([], "030d", DriverStatus.NotInstalled));
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
        Assert.Null(TrayMenu.RecommendedLabel(kind, DriverStatus.NotBound, 40));
        Assert.Null(TrayMenu.RecommendedLabel(kind, DriverStatus.Ok, 40));
        Assert.False(TrayMenu.ShouldShowHealthRow([], pid, DriverStatus.Ok));
        Assert.False(TrayMenu.ShouldShowHealthRow([], pid, DriverStatus.NotBound));
        Assert.Equal(kind == DeviceKind.MagicTrackpadV1,
            TrayMenu.ShowTrackpadV1BootCamp(kind, pid));
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
            "https://github.com/LesleyMurfin/magic-tray/blob/ship/magic-tray-ready/docs/ALERTS.md",
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

    static void AssertNoForbiddenV1V2Copy(string text)
    {
        Assert.DoesNotContain("PATH-A", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("applewirelessmouse", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KMDF", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tealtadpole", text, StringComparison.OrdinalIgnoreCase);
    }
}
