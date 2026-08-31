// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DriverPackageCatalogTests
{
    [Fact]
    public void Best_0323_IsSbagirici_NotLesleyMurfin()
    {
        var best = DriverPackageCatalog.BestForPid("0323");
        Assert.Equal(DriverPackageId.Best, best.Id);
        Assert.Equal("sbagirici", best.Owner);
        Assert.Equal("apple-magic-mouse-scroll-fix-windows", best.Repo);
        Assert.Equal("driver", best.PathInRepo);
        Assert.Equal(InstallKind.OfficialSysBind, best.Kind);
        Assert.True(best.Published);
        Assert.False(DriverPackageCatalog.IsForbiddenSource(best));
        Assert.DoesNotContain("LesleyMurfin", best.Owner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("magic-tray", best.Repo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("magic-mouse-v3-windows-fix", best.Repo, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("030d")]
    [InlineData("0310")]
    [InlineData("0269")]
    public void Best_030D_0310_0269_IsTealtadpole(string pid)
    {
        var best = DriverPackageCatalog.BestForPid(pid);
        Assert.Equal("tealtadpole", best.Owner);
        Assert.Equal("MagicMouse2DriversWin11x64", best.Repo);
        Assert.Equal("AppleWirelessMouse", best.PathInRepo);
        Assert.Equal(InstallKind.InfPnputil, best.Kind);
        Assert.True(best.Published);
        Assert.Equal("https://github.com/tealtadpole/MagicMouse2DriversWin11x64/archive/refs/heads/master.zip",
            DriverInstaller.ZipUrl(best));
    }

    [Fact]
    public void Best_Keyboard_IsBrigadierKeymagic2()
    {
        var choices = DriverPackageCatalog.ChoicesFor(DeviceKind.MagicKeyboard, "024f");
        Assert.Equal(2, choices.Count);
        Assert.All(choices, c =>
        {
            Assert.Equal("timsutton", c.Owner);
            Assert.Equal("brigadier", c.Repo);
            Assert.Equal(InstallKind.BrigadierKeyboard, c.Kind);
            Assert.True(c.Published);
        });
        Assert.Equal("AppleKeyboardMagic2", choices[0].PathInRepo);
        Assert.Equal("MacBookAir9,1", DriverPackageCatalog.BrigadierModel);
    }

    [Fact]
    public void Choices_0323_ArePublished_AndNotChrischip()
    {
        var choices = DriverPackageCatalog.ChoicesFor(DeviceKind.MagicMouseV3, "0323");
        Assert.All(choices, c =>
        {
            Assert.True(c.Published);
            Assert.Equal("sbagirici", c.Owner);
            Assert.NotEqual("chrischip", c.Owner, StringComparer.OrdinalIgnoreCase);
            Assert.False(DriverPackageCatalog.IsForbiddenSource(c));
        });
    }

    [Fact]
    public void Catalog_NeverPointsAtLesleyMurfin()
    {
        foreach (var pid in new[] { "0323", "030d", "0269", "0310" })
            Assert.False(DriverPackageCatalog.IsForbiddenSource(DriverPackageCatalog.BestForPid(pid)));
        foreach (var c in DriverPackageCatalog.ChoicesFor(DeviceKind.MagicKeyboard, "0239"))
            Assert.False(DriverPackageCatalog.IsForbiddenSource(c));
    }

    [Fact]
    public void Forbidden_LesleyMurfinAndLocalVendor()
    {
        Assert.True(DriverPackageCatalog.IsForbiddenOwnerRepo("LesleyMurfin", "magic-mouse-v3-windows-fix"));
        Assert.True(DriverPackageCatalog.IsForbiddenOwnerRepo("anyone", "magic-tray"));
        Assert.True(DriverPackageCatalog.IsLocalVendorPath(@"C:\src\magic-tray\driver\MagicMouseDriver.inf"));
        Assert.False(DriverPackageCatalog.IsLocalVendorPath("AppleWirelessMouse"));
        Assert.False(DriverPackageCatalog.IsLocalVendorPath("driver"));
    }

    [Fact]
    public void PullAndInstall_EmptyPid_ThrowsBeforeNetwork()
    {
        var pkg = DriverPackageCatalog.BestForPid("0323");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DriverInstaller.PullAndInstallAsync(pkg, "").GetAwaiter().GetResult());
        Assert.Contains("No device PID", ex.Message);
    }

    [Fact]
    public void PullAndInstall_LesleyMurfin_ThrowsBeforeNetwork()
    {
        var banned = new DriverPackage(
            DriverPackageId.Best, "banned",
            "LesleyMurfin", "magic-mouse-v3-windows-fix", "main", "v2-kmdf-driver",
            InstallKind.InfPnputil, ["0323"], Published: true, MissingReason: null);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DriverInstaller.PullAndInstallAsync(banned, "0323").GetAwaiter().GetResult());
        Assert.Contains("LesleyMurfin", ex.Message);
    }

    [Fact]
    public void FindPackageDir_MatchesRepoSubfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-pkg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pkg = Path.Combine(root, "MagicMouse2DriversWin11x64-master", "AppleWirelessMouse");
            Directory.CreateDirectory(pkg);
            Assert.Equal(pkg, DriverInstaller.FindPackageDir(root, "AppleWirelessMouse"));
            Assert.Null(DriverInstaller.FindPackageDir(root, "v2-kmdf-driver"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
