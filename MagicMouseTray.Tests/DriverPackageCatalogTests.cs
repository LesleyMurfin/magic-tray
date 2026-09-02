// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DriverPackageCatalogTests
{
    [Fact]
    public void Catalog_IsSoleUrlSource_ExpectedConstants()
    {
        Assert.Equal("https://github.com/tealtadpole/MagicMouse2DriversWin11x64",
            DriverPackageCatalog.TealtadpolePageUrl);
        Assert.Equal(
            "https://raw.githubusercontent.com/tealtadpole/MagicMouse2DriversWin11x64/master/AppleWirelessMouse/AppleWirelessMouse.inf",
            DriverPackageCatalog.TealtadpoleRawInfUrl);
        Assert.Equal("https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix",
            DriverPackageCatalog.V3RepoUrl);
        Assert.Equal("v2-kmdf-driver/Install-KMDF.cmd",
            DriverPackageCatalog.KmdfInstallCmdRelativePath);
        Assert.Equal("v2-kmdf-driver/Uninstall-KMDF.cmd",
            DriverPackageCatalog.KmdfUninstallCmdRelativePath);
        Assert.Equal("scripts/kbd-patch-cachedservices.ps1",
            DriverPackageCatalog.KeyboardPatchScriptPath);
        Assert.Equal("MagicMouseDriver", DriverPackageCatalog.PatchedKmdfServiceName);
        Assert.Equal("MagicMouseDriver.sys", DriverPackageCatalog.PatchedKmdfSysFileName);
        Assert.Equal("main", DriverPackageCatalog.V3RepoRef);
        Assert.Equal("v1-binary-patch/installer/Install-MagicMousePatch.ps1",
            DriverPackageCatalog.PathAInstallScriptRelativePath);
        Assert.Equal("v1-binary-patch/installer/Uninstall-MagicMousePatch.ps1",
            DriverPackageCatalog.PathAUninstallScriptRelativePath);
    }

    [Fact]
    public void Catalog_0323_NeverPathA()
    {
        Assert.DoesNotContain("Install-MagicMousePatch", DriverPackageCatalog.KmdfInstallCmdRelativePath);
        Assert.DoesNotContain("v1-binary-patch", DriverPackageCatalog.KmdfInstallCmdRelativePath);
        Assert.DoesNotContain("applewirelessmouse.sys", DriverPackageCatalog.KmdfInstallCmdRelativePath);
        Assert.DoesNotContain("Install-MagicMousePatch", DriverPackageCatalog.V3RepoUrl);
        Assert.Equal("Install-KMDF.cmd", DriverPackageCatalog.KmdfOneClickName);
        Assert.Equal("Uninstall-KMDF.cmd", DriverPackageCatalog.KmdfUninstallCmdName);
        Assert.DoesNotContain("FLIP:NoFilter", DriverPackageCatalog.KmdfUninstallCmdRelativePath);
        Assert.Equal(
            "https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/archive/refs/heads/main.zip",
            DriverPackageCatalog.V3ZipUrl);
    }

    [Fact]
    public void Catalog_PathA_ScriptPath_IsBinaryPatch_NotKmdf()
    {
        Assert.Contains("Install-MagicMousePatch", DriverPackageCatalog.PathAInstallScriptRelativePath);
        Assert.Contains("v1-binary-patch", DriverPackageCatalog.PathAInstallScriptRelativePath);
        Assert.DoesNotContain("Install-MagicMousePatch", DriverPackageCatalog.KmdfInstallCmdRelativePath);
        Assert.DoesNotContain("v1-binary-patch", DriverPackageCatalog.KmdfInstallCmdRelativePath);
        Assert.DoesNotContain("Install-KMDF", DriverPackageCatalog.PathAInstallScriptRelativePath);
        Assert.Equal("Install-MagicMousePatch.ps1", DriverPackageCatalog.PathAInstallScriptName);
        Assert.Equal("Uninstall-MagicMousePatch.ps1", DriverPackageCatalog.PathAUninstallScriptName);
        Assert.DoesNotContain("Uninstall-KMDF", DriverPackageCatalog.PathAUninstallScriptRelativePath);
    }

    [Fact]
    public void IsPathAInstaller_RecognizesPatchScript_NotKmdf()
    {
        Assert.True(DriverInstaller.IsPathAInstaller("v1-binary-patch/installer/Install-MagicMousePatch.ps1"));
        Assert.True(DriverInstaller.IsPathAInstaller(@"C:\cache\v1-binary-patch\installer\Install-MagicMousePatch.ps1"));
        Assert.False(DriverInstaller.IsPathAInstaller("v2-kmdf-driver/Install-KMDF.cmd"));
        Assert.True(DriverInstaller.IsKmdfOneClick("v2-kmdf-driver/Install-KMDF.cmd"));
        Assert.False(DriverInstaller.IsKmdfOneClick("v2-kmdf-driver/Install-KMDF.ps1"));
        Assert.False(DriverInstaller.IsKmdfOneClick("v1-binary-patch/installer/Install-MagicMousePatch.ps1"));
        Assert.True(DriverInstaller.IsKmdfUninstall("v2-kmdf-driver/Uninstall-KMDF.cmd"));
        Assert.False(DriverInstaller.IsKmdfUninstall("v2-kmdf-driver/Install-KMDF.cmd"));
        Assert.False(DriverInstaller.IsKmdfOneClick("v2-kmdf-driver/Uninstall-KMDF.cmd"));
        Assert.False(DriverInstaller.IsKmdfUninstall("v1-binary-patch/installer/Uninstall-MagicMousePatch.ps1"));
    }

    [Fact]
    public void FindKmdfOneClick_UsesV2Script_IgnoresPathA()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-up-" + Guid.NewGuid().ToString("N"));
        try
        {
            var v2 = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v2-kmdf-driver");
            var pathA = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v1-binary-patch", "installer");
            Directory.CreateDirectory(v2);
            Directory.CreateDirectory(pathA);
            var kmdf = Path.Combine(v2, "Install-KMDF.cmd");
            File.WriteAllText(kmdf, "@echo off");
            File.WriteAllText(Path.Combine(v2, "Install-KMDF.ps1"), "# not the entry point");
            File.WriteAllText(Path.Combine(pathA, "Install-MagicMousePatch.ps1"), "# PATH-A forbidden");

            Assert.Equal(kmdf, DriverInstaller.FindKmdfOneClick(root, "v2-kmdf-driver/Install-KMDF.cmd"));
            Assert.Equal(kmdf, DriverInstaller.FindKmdfOneClick(root, "v2-kmdf-driver"));
            Assert.Equal(kmdf, DriverInstaller.FindKmdfOneClick(
                root, "v1-binary-patch/installer/Install-MagicMousePatch.ps1"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindKmdfOneClick_PathAOnly_ReturnsNull_DoesNotFallBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-patha-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pathA = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v1-binary-patch", "installer");
            Directory.CreateDirectory(pathA);
            Directory.CreateDirectory(Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v2-kmdf-driver"));
            var patch = Path.Combine(pathA, "Install-MagicMousePatch.ps1");
            File.WriteAllText(patch, "# PATH-A");

            Assert.Null(DriverInstaller.FindKmdfOneClick(root, "v2-kmdf-driver"));
            Assert.Null(DriverInstaller.FindKmdfOneClick(root, ""));
            Assert.Null(DriverInstaller.FindKmdfOneClick(
                root, "v1-binary-patch/installer/Install-MagicMousePatch.ps1"));
            Assert.Equal(patch, DriverInstaller.FindPathAInstaller(
                root, DriverPackageCatalog.PathAInstallScriptRelativePath));
            Assert.True(DriverInstaller.IsPathAInstaller(patch));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindPathAInstaller_UsesPatchScript_IgnoresKmdf()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-patha-offer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var v2 = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v2-kmdf-driver");
            var pathA = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v1-binary-patch", "installer");
            Directory.CreateDirectory(v2);
            Directory.CreateDirectory(pathA);
            var kmdf = Path.Combine(v2, "Install-KMDF.cmd");
            var patch = Path.Combine(pathA, "Install-MagicMousePatch.ps1");
            var uninstall = Path.Combine(pathA, "Uninstall-MagicMousePatch.ps1");
            File.WriteAllText(kmdf, "@echo off");
            File.WriteAllText(patch, "# PathA offer");
            File.WriteAllText(uninstall, "# PathA uninstall");

            Assert.Equal(patch, DriverInstaller.FindPathAInstaller(
                root, DriverPackageCatalog.PathAInstallScriptRelativePath));
            Assert.Equal(patch, DriverInstaller.FindPathAInstaller(root, "v2-kmdf-driver/Install-KMDF.cmd"));
            Assert.Equal(patch, DriverInstaller.FindPathAInstaller(root, ""));
            Assert.NotEqual(kmdf, DriverInstaller.FindPathAInstaller(
                root, DriverPackageCatalog.PathAInstallScriptRelativePath));
            Assert.Equal(uninstall, DriverInstaller.FindPathAUninstaller(root));
            Assert.NotEqual(patch, DriverInstaller.FindPathAUninstaller(root));
            Assert.True(DriverInstaller.IsPathAInstaller(patch));
            Assert.Equal(kmdf, DriverInstaller.FindKmdfOneClick(
                root, DriverPackageCatalog.PathAInstallScriptRelativePath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindPathAInstaller_KmdfOnly_ReturnsNull_DoesNotFallBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-kmdf-only-" + Guid.NewGuid().ToString("N"));
        try
        {
            var v2 = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v2-kmdf-driver");
            Directory.CreateDirectory(v2);
            File.WriteAllText(Path.Combine(v2, "Install-KMDF.cmd"), "@echo off");

            Assert.Null(DriverInstaller.FindPathAInstaller(root, DriverPackageCatalog.PathAInstallScriptRelativePath));
            Assert.Null(DriverInstaller.FindPathAInstaller(root, "v2-kmdf-driver/Install-KMDF.cmd"));
            Assert.Null(DriverInstaller.FindPathAUninstaller(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindKmdfOneClick_EmptyV2_ReturnsNull_DoesNotInventBindScript()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v2-kmdf-driver"));
            Assert.Null(DriverInstaller.FindKmdfOneClick(root, "v2-kmdf-driver/Install-KMDF.cmd"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParseMacFromInstance_ReadsBthenumTail()
    {
        Assert.Equal("e806884b0741",
            DriverInstaller.ParseMacFromInstance("9&73b8b28&0&E806884B0741_C00000000"));
        Assert.Null(DriverInstaller.ParseMacFromInstance("no-mac-here"));
    }

    [Fact]
    public void FindKmdfUninstaller_LooksUpUninstallKmdfCmd()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-stock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var v2 = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v2-kmdf-driver");
            var pathA = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v1-binary-patch", "installer");
            Directory.CreateDirectory(v2);
            Directory.CreateDirectory(pathA);
            var kmdfInstall = Path.Combine(v2, "Install-KMDF.cmd");
            var kmdfUninstall = Path.Combine(v2, "Uninstall-KMDF.cmd");
            var pathAUninstall = Path.Combine(pathA, "Uninstall-MagicMousePatch.ps1");
            File.WriteAllText(kmdfInstall, "@echo off");
            File.WriteAllText(kmdfUninstall, "@echo off");
            File.WriteAllText(pathAUninstall, "# PathA uninstall");

            Assert.Equal(kmdfUninstall, DriverInstaller.FindKmdfUninstaller(root));
            Assert.Equal(pathAUninstall, DriverInstaller.FindPathAUninstaller(root));
            Assert.NotEqual(kmdfInstall, DriverInstaller.FindKmdfUninstaller(root));
            Assert.Equal(kmdfInstall, DriverInstaller.FindKmdfOneClick(
                root, DriverPackageCatalog.KmdfInstallCmdRelativePath));
            Assert.True(DriverInstaller.IsKmdfUninstall(kmdfUninstall));
            Assert.False(DriverInstaller.IsKmdfUninstall(kmdfInstall));
            Assert.False(DriverInstaller.IsKmdfOneClick(kmdfUninstall));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindKmdfUninstaller_PathAOnly_ReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-stock-patha-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pathA = Path.Combine(root, "magic-mouse-v3-windows-fix-main", "v1-binary-patch", "installer");
            Directory.CreateDirectory(pathA);
            File.WriteAllText(Path.Combine(pathA, "Uninstall-MagicMousePatch.ps1"), "# PathA");

            Assert.Null(DriverInstaller.FindKmdfUninstaller(root));
            Assert.NotNull(DriverInstaller.FindPathAUninstaller(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void V1V2BootCamp_StillOpensTealtadpolePage()
    {
        Assert.Equal(DriverPackageCatalog.TealtadpolePageUrl, DriverInstaller.V1V2BootCampPageUrl);
        Assert.Contains("tealtadpole", DriverInstaller.V1V2BootCampPageUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Install-KMDF", DriverInstaller.V1V2BootCampPageUrl);
    }

    [Fact]
    public void TrackpadV1BootCamp_UrlIsAppleWirelessTrackpadFolder()
    {
        Assert.Equal(
            "https://github.com/tealtadpole/MagicMouse2DriversWin11x64/tree/master/AppleWirelessTrackpad",
            DriverPackageCatalog.TealtadpoleTrackpadPageUrl);
        Assert.Equal(
            DriverPackageCatalog.TealtadpoleTrackpadPageUrl,
            DriverInstaller.TrackpadV1BootCampPageUrl);
        Assert.Contains("AppleWirelessTrackpad", DriverInstaller.TrackpadV1BootCampPageUrl, StringComparison.Ordinal);
        Assert.Contains("AppleWirelessTrackpad", DriverPackageCatalog.TealtadpoleTrackpadRawInfUrl, StringComparison.Ordinal);
        Assert.Equal("AppleWirelessTrackpad/AppleWirelessTrackpad.inf",
            DriverPackageCatalog.TealtadpoleTrackpadInfPathInRepo);
        Assert.Equal("applewtp", DriverPackageCatalog.AppleTrackpadFilterServiceName);

        Assert.NotEqual(DriverPackageCatalog.TealtadpoleRawInfUrl, DriverPackageCatalog.TealtadpoleTrackpadRawInfUrl);
        Assert.DoesNotContain("AppleWirelessMouse", DriverInstaller.TrackpadV1BootCampPageUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("AppleWirelessMouse", DriverPackageCatalog.TealtadpoleTrackpadRawInfUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackpadV1BootCamp_Plan_030E_NeverKmdfPathAOrPnputil()
    {
        Assert.True(DriverInstaller.IsTrackpadV1BootCampPid("030e"));
        Assert.True(DriverInstaller.IsTrackpadV1BootCampPid("030E"));
        var plan = DriverInstaller.PlanTrackpadV1BootCamp("030E");
        Assert.Equal("030e", plan.Pid);
        Assert.Equal(DriverInstaller.TrackpadV1BootCampPageUrl, plan.Url);
        Assert.Contains("AppleWirelessTrackpad", plan.Url, StringComparison.Ordinal);

        Assert.Contains("applewtp", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("Boot Camp", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("030E", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("Test Mode is not required", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("Cancel aborts", plan.Prompt, StringComparison.Ordinal);

        foreach (var forbidden in DriverInstaller.TrackpadV1BootCampForbiddenNames)
        {
            Assert.DoesNotContain(forbidden, plan.Url, StringComparison.OrdinalIgnoreCase);
            if (forbidden == "pnputil")
                continue;
            Assert.DoesNotContain(forbidden, plan.Prompt, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("Install-KMDF", plan.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Install-KMDF", plan.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Install-MagicMousePatch", plan.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Install-MagicMousePatch", plan.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Uninstall-KMDF", plan.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PathA", plan.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PathA", plan.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TrackpadPtp", plan.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TrackpadPtp", plan.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not run pnputil", plan.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0265")]
    [InlineData("0324")]
    [InlineData("0323")]
    [InlineData("030d")]
    [InlineData("")]
    public void TrackpadV1BootCamp_NotOfferedForNon030E(string pid)
    {
        Assert.False(DriverInstaller.IsTrackpadV1BootCampPid(pid));
        var ex = Assert.Throws<InvalidOperationException>(
            () => DriverInstaller.PlanTrackpadV1BootCamp(pid));
        Assert.Contains("030E", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0265", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0324", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Install-KMDF.cmd", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Install-MagicMousePatch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("pnputil", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeviceKind.MagicTrackpadV1, "030e", true)]
    [InlineData(DeviceKind.MagicTrackpadV1, "030E", true)]
    [InlineData(DeviceKind.MagicTrackpadV2, "0265", false)]
    [InlineData(DeviceKind.MagicTrackpadV3, "0324", false)]
    [InlineData(DeviceKind.MagicMouseV3, "0323", false)]
    [InlineData(DeviceKind.MagicMouseV1, "030d", false)]
    [InlineData(DeviceKind.MagicKeyboard, "0239", false)]
    public void TrackpadV1BootCamp_TrayOffer_OnlyMagicTrackpadV1(DeviceKind kind, string pid, bool expected)
    {
        Assert.Equal(expected, TrayMenu.ShowTrackpadV1BootCamp(kind, pid));
        Assert.False(TrayMenu.IsV3(kind, pid) && expected);
    }

    [Theory]
    [InlineData("030d")]
    [InlineData("030D")]
    [InlineData("0269")]
    [InlineData("0310")]
    public void V1V2StockRestore_IsPidScoped_NeverRuns0323Scripts(string pid)
    {
        Assert.True(DriverInstaller.IsV1V2StockPid(pid));
        var plan = DriverInstaller.PlanV1V2StockRestore(pid);
        Assert.Equal(pid.ToLowerInvariant(), plan.Pid);
        Assert.Empty(plan.ExternalScripts);
        Assert.Contains("HidBth", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("Apple filter", plan.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Test Mode is not required", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("Cancel aborts", plan.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("0323", plan.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("KMDF", plan.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FLIP", plan.Prompt, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(pid.ToLowerInvariant(), plan.UnbindScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("applewirelessmouse", plan.UnbindScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_PID&", plan.UnbindScript, StringComparison.Ordinal);
        Assert.DoesNotContain("0323", plan.UnbindScript, StringComparison.Ordinal);
        foreach (var forbidden in DriverInstaller.V1V2StockForbiddenNames)
            Assert.DoesNotContain(forbidden, plan.UnbindScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Install-KMDF.cmd", plan.UnbindScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Uninstall-KMDF.cmd", plan.UnbindScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Install-MagicMousePatch", plan.UnbindScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Uninstall-MagicMousePatch", plan.UnbindScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FLIP:NoFilter", plan.UnbindScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OfferV3StockRestore", plan.UnbindScript, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0323")]
    [InlineData("0239")]
    [InlineData("")]
    public void V1V2StockRestore_RejectsNonV1V2Pid(string pid)
    {
        Assert.False(DriverInstaller.IsV1V2StockPid(pid));
        var ex = Assert.Throws<InvalidOperationException>(() => DriverInstaller.PlanV1V2StockRestore(pid));
        Assert.Contains("030d", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Install-KMDF.cmd", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Uninstall-KMDF.cmd", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Install-MagicMousePatch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Uninstall-MagicMousePatch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FLIP:NoFilter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V1V2StockRestore_ExecuteRefuses0323Scripts()
    {
        var basePlan = DriverInstaller.PlanV1V2StockRestore("030d");
        var withScripts = basePlan with { ExternalScripts = ["Uninstall-KMDF.cmd"] };
        var scriptsEx = Assert.Throws<InvalidOperationException>(
            () => DriverInstaller.ExecuteV1V2StockRestore(withScripts));
        Assert.Contains("0323", scriptsEx.Message, StringComparison.Ordinal);

        var withFlip = basePlan with { UnbindScript = "FLIP:NoFilter" };
        var flipEx = Assert.Throws<InvalidOperationException>(
            () => DriverInstaller.ExecuteV1V2StockRestore(withFlip));
        Assert.Contains("FLIP:NoFilter", flipEx.Message, StringComparison.Ordinal);

        var withKmdf = basePlan with { UnbindScript = "Uninstall-KMDF.cmd" };
        var kmdfEx = Assert.Throws<InvalidOperationException>(
            () => DriverInstaller.ExecuteV1V2StockRestore(withKmdf));
        Assert.Contains("Uninstall-KMDF.cmd", kmdfEx.Message, StringComparison.Ordinal);
    }
}
