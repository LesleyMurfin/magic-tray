// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DriverPackageCatalogTests
{
    [Fact]
    public void KmdfAndBest_For0323_PullFromMagicMouseV3WindowsFix_NotMagicTray()
    {
        var best = DriverPackageCatalog.BestForPid("0323");
        Assert.Equal(DriverPackageId.Best, best.Id);
        Assert.Equal("LesleyMurfin", best.Owner);
        Assert.Equal("magic-mouse-v3-windows-fix", best.Repo);
        Assert.Equal("v2-kmdf-driver", best.PathInRepo);
        Assert.Equal("https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix", DriverPackageCatalog.KmdfRepoUrl);
        Assert.DoesNotContain("magic-tray", best.Repo, StringComparison.OrdinalIgnoreCase);
        Assert.False(DriverPackageCatalog.IsLocalVendorPath(best.PathInRepo));
    }

    [Fact]
    public void Choices_0323_AreBestKmdfV3V2V1_FromSameRepo()
    {
        var choices = DriverPackageCatalog.ChoicesFor(DeviceKind.MagicMouseV3, "0323");
        Assert.Equal(new[] { DriverPackageId.Best, DriverPackageId.Kmdf, DriverPackageId.V3, DriverPackageId.V2, DriverPackageId.V1 },
            choices.Select(c => c.Id));
        Assert.All(choices, c =>
        {
            Assert.Equal("magic-mouse-v3-windows-fix", c.Repo);
            Assert.True(c.Published);
        });
        Assert.Equal("v1-binary-patch", choices.Single(c => c.Id == DriverPackageId.V1).PathInRepo);
        Assert.Equal("v2-kmdf-driver", choices.Single(c => c.Id == DriverPackageId.Kmdf).PathInRepo);
    }

    [Fact]
    public void Hardware030D_HasNoLesleyMurfinRepo()
    {
        var best = DriverPackageCatalog.BestForPid("030d");
        Assert.False(best.Published);
        Assert.Contains("0323-only", best.MissingReason);
    }

    [Fact]
    public void Keyboard_HasNoLesleyMurfinRepo()
    {
        var choices = DriverPackageCatalog.ChoicesFor(DeviceKind.MagicKeyboard, "0239");
        Assert.Single(choices);
        Assert.False(choices[0].Published);
        Assert.Contains("apple-kb-monitor", choices[0].MissingReason);
    }

    [Fact]
    public void ZipUrl_PointsAtGitHubArchive_NotLocalDriver()
    {
        var pkg = DriverPackageCatalog.BestForPid("0323");
        var url = DriverInstaller.ZipUrl(pkg);
        Assert.Equal("https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/archive/refs/heads/main.zip", url);
        Assert.DoesNotContain("magic-tray", url);
    }

    [Fact]
    public void IsLocalVendorPath_RejectsMagicTrayDriverTree()
    {
        Assert.True(DriverPackageCatalog.IsLocalVendorPath(@"C:\src\magic-tray\driver\MagicMouseDriver.inf"));
        Assert.False(DriverPackageCatalog.IsLocalVendorPath("v2-kmdf-driver"));
        Assert.False(DriverPackageCatalog.IsLocalVendorPath(
            @"C:\Users\x\AppData\Local\MagicMouseTray\driver-cache\magic-mouse-v3-windows-fix-main\v2-kmdf-driver\MagicMouseDriver.inf"));
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
    public void PullAndInstall_Unpublished_ThrowsBeforeNetwork()
    {
        var missing = DriverPackageCatalog.BestForPid("030d");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DriverInstaller.PullAndInstallAsync(missing, "030d").GetAwaiter().GetResult());
        Assert.Contains("0323-only", ex.Message);
    }

    [Fact]
    public void FindPackageDir_MatchesRepoSubfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-pkg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pkg = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v2-kmdf-driver");
            Directory.CreateDirectory(pkg);
            Assert.Equal(pkg, DriverInstaller.FindPackageDir(root, "v2-kmdf-driver"));
            Assert.Null(DriverInstaller.FindPackageDir(root, "v1-binary-patch"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
