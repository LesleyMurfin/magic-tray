// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DriverPackageCatalogTests
{
    [Fact]
    public void Best_0323_PullsKmdfHomeAndRunsTheirInstaller_NotVendoredHere()
    {
        var best = DriverPackageCatalog.BestForPid("0323");
        Assert.Equal(DriverPackageId.Best, best.Id);
        Assert.Equal("LesleyMurfin", best.Owner);
        Assert.Equal("magic-mouse-v3-windows-fix", best.Repo);
        Assert.Equal("v1-binary-patch/installer/Install-MagicMousePatch.ps1", best.PathInRepo);
        Assert.Equal(InstallKind.KmdfPull, best.Kind);
        Assert.True(best.Published);
        Assert.False(DriverPackageCatalog.IsForbiddenSource(best));
        Assert.False(DriverPackageCatalog.IsLocalVendorPath(best.PathInRepo));
        Assert.Equal(
            "https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/archive/refs/heads/main.zip",
            DriverInstaller.ZipUrl(best));
        Assert.DoesNotContain("magic-tray", best.Repo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bind-filter", best.PathInRepo, StringComparison.OrdinalIgnoreCase);
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
    }

    [Fact]
    public void Best_Keyboard_IsSdpPatch_NotKeymagic2()
    {
        var choices = DriverPackageCatalog.ChoicesFor(DeviceKind.MagicKeyboard, "024f");
        Assert.Equal(2, choices.Count);
        Assert.All(choices, c =>
        {
            Assert.Equal(InstallKind.KeyboardSdpPatch, c.Kind);
            Assert.True(c.Published);
            Assert.Contains("kbd-patch-cachedservices.ps1", c.PathInRepo);
        });
        Assert.DoesNotContain(choices, c => c.Repo.Contains("brigadier", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Choices_0323_AreKmdfPull_NotChrischip_NotSbagirici()
    {
        var choices = DriverPackageCatalog.ChoicesFor(DeviceKind.MagicMouseV3, "0323");
        Assert.All(choices, c =>
        {
            Assert.True(c.Published);
            Assert.Equal("magic-mouse-v3-windows-fix", c.Repo);
            Assert.Equal(InstallKind.KmdfPull, c.Kind);
            Assert.Equal("v1-binary-patch/installer/Install-MagicMousePatch.ps1", c.PathInRepo);
            Assert.NotEqual("chrischip", c.Owner, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual("sbagirici", c.Owner, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Forbidden_IsMagicTrayVendor_NotKmdfHome()
    {
        Assert.False(DriverPackageCatalog.IsForbiddenSource(DriverPackageCatalog.BestForPid("0323")));
        Assert.True(DriverPackageCatalog.IsForbiddenSource(new DriverPackage(
            DriverPackageId.Best, "tray", "LesleyMurfin", "magic-tray", "main", "driver",
            InstallKind.InfPnputil, ["0323"], true, null)));
        Assert.True(DriverPackageCatalog.IsLocalVendorPath(@"C:\src\magic-tray\driver\MagicMouseDriver.inf"));
        Assert.False(DriverPackageCatalog.IsLocalVendorPath("v1-binary-patch/installer/Install-MagicMousePatch.ps1"));
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
    public void PullAndInstall_KmdfOn030D_ThrowsBeforeNetwork()
    {
        var kmdf = DriverPackageCatalog.BestForPid("0323");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DriverInstaller.PullAndInstallAsync(kmdf, "030d").GetAwaiter().GetResult());
        Assert.Contains("0323-only", ex.Message);
    }

    [Fact]
    public void FindPackageDir_MatchesTealSubfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-pkg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var teal = Path.Combine(root, "MagicMouse2DriversWin11x64-master", "AppleWirelessMouse");
            Directory.CreateDirectory(teal);
            Assert.Equal(teal, DriverInstaller.FindPackageDir(root, "AppleWirelessMouse"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindUpstreamInstaller_UsesPathInRepoWhenPresent()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-up-" + Guid.NewGuid().ToString("N"));
        try
        {
            var installerDir = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v1-binary-patch", "installer");
            Directory.CreateDirectory(installerDir);
            var published = Path.Combine(installerDir, "Install-MagicMousePatch.ps1");
            File.WriteAllText(published, "# upstream easy sign+install");
            Directory.CreateDirectory(Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v2-kmdf-driver"));

            var found = DriverInstaller.FindUpstreamInstaller(
                root, "v1-binary-patch/installer/Install-MagicMousePatch.ps1");
            Assert.Equal(published, found);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindUpstreamInstaller_PrefersV2WhenPathMissing_ThenPublishedEasyScript()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-up2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var v2 = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v2-kmdf-driver");
            var installerDir = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v1-binary-patch", "installer");
            Directory.CreateDirectory(v2);
            Directory.CreateDirectory(installerDir);
            File.WriteAllText(Path.Combine(installerDir, "Install-MagicMousePatch.ps1"), "# v1");
            File.WriteAllText(Path.Combine(v2, "Install-MagicMouseDriver.ps1"), "# future v2");

            var v2Hit = DriverInstaller.FindUpstreamInstaller(root, "does-not-exist.ps1");
            Assert.Equal(Path.Combine(v2, "Install-MagicMouseDriver.ps1"), v2Hit);

            Directory.Delete(v2, recursive: true);
            var v1Hit = DriverInstaller.FindUpstreamInstaller(root, "");
            Assert.Equal(Path.Combine(installerDir, "Install-MagicMousePatch.ps1"), v1Hit);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindUpstreamInstaller_Missing_ReturnsNull_DoesNotInventBindScript()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v2-kmdf-driver"));
            Assert.Null(DriverInstaller.FindUpstreamInstaller(root, "v2-kmdf-driver"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
