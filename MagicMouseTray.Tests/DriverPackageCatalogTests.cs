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

    [Fact]
    public void KeyboardSdpPatch_PromptStatesEveryCostBeforeTheUserAgrees()
    {
        var text = DriverInstaller.KeyboardSdpPatchPrompt("e806884b0741");

        Assert.Contains("scripts/kbd-patch-cachedservices.ps1 -Mac e806884b0741", text,
            StringComparison.Ordinal);
        Assert.Contains("Bluetooth SDP cache entry", text, StringComparison.Ordinal);
        Assert.Contains("battery", text, StringComparison.Ordinal);
        Assert.Contains("one administrator approval", text, StringComparison.Ordinal);
        Assert.Contains("changes nothing else", text, StringComparison.Ordinal);
        Assert.Contains("Re-pairing the keyboard can undo it", text, StringComparison.Ordinal);
        Assert.Contains("Cancel aborts", text, StringComparison.Ordinal);
    }

    // The exit codes the script defines, in the words they turn into here.
    [Fact]
    public void KeyboardSdpPatch_FailureNamesTheOutcomeAndTheLog()
    {
        var noRecord = DriverInstaller.KeyboardSdpFailureMessage(
            DriverInstaller.KeyboardPatchExitNoRecord, @"C:\logs\kbd-patch.log", null);
        Assert.Contains("No Bluetooth record for this keyboard carries the battery report",
            noRecord, StringComparison.Ordinal);
        Assert.Contains("so nothing was changed", noRecord, StringComparison.Ordinal);
        Assert.Contains(@"Log: C:\logs\kbd-patch.log", noRecord, StringComparison.Ordinal);

        var unknownShape = DriverInstaller.KeyboardSdpFailureMessage(
            DriverInstaller.KeyboardPatchExitNotPatched,
            @"C:\logs\kbd-patch.log",
            "[CachedServices\\00010000] bytes around RID 0x47");
        Assert.Contains("could be unlocked", unknownShape, StringComparison.Ordinal);
        // the tail is quoted, because the elevated console is hidden and this
        // dialog is the only place the script's own words can appear
        Assert.Contains("bytes around RID 0x47", unknownShape, StringComparison.Ordinal);

        // An exit code this app does not know is reported as itself, never
        // dressed up as a cause it cannot prove - and never as an untouched
        // registry either: an unnamed code includes PowerShell's own 1, which
        // can land before the registry was opened or after one blob was
        // rewritten.
        var unknownCode = DriverInstaller.KeyboardSdpFailureMessage(7, @"C:\logs\x.log", null);
        Assert.Contains("exit code 7", unknownCode, StringComparison.Ordinal);
        Assert.DoesNotContain("No Bluetooth record", unknownCode, StringComparison.Ordinal);
        Assert.DoesNotContain("could be unlocked", unknownCode, StringComparison.Ordinal);
        Assert.Contains("before it could report", unknownCode, StringComparison.Ordinal);
        Assert.Contains("may already carry the unlock", unknownCode, StringComparison.Ordinal);
        Assert.Contains("backed up", unknownCode, StringComparison.Ordinal);
    }

    // A throw out of the elevated run - the 5-minute timeout kills the process
    // tree mid-loop, so it can stop after one record was rewritten and before
    // the next - leaves the state an unnamed exit code leaves, and has to say
    // as much: where the log is, that part of the cache may already carry the
    // unlock, and where the originals are. The exception's own words are kept
    // on top, never instead.
    [Fact]
    public void KeyboardSdpPatch_AThrownRunSaysAsMuchAsAnUnnamedExitCode()
    {
        var text = DriverInstaller.KeyboardSdpFailureMessage(
            "powershell.exe timed out.", @"C:\logs\kbd-patch.log", "SUMMARY patched=1 blocked=0");

        Assert.Contains("powershell.exe timed out.", text, StringComparison.Ordinal);
        Assert.Contains("may already carry the unlock", text, StringComparison.Ordinal);
        Assert.Contains("backed up", text, StringComparison.Ordinal);
        Assert.Contains(@"Log: C:\logs\kbd-patch.log", text, StringComparison.Ordinal);
        Assert.Contains("SUMMARY patched=1 blocked=0", text, StringComparison.Ordinal);
    }

    // Both halves of "it already worked": the state where nothing ran, and the
    // state where the run just finished. Each has to name the radio cycle,
    // because the registry write alone never produces a percent - hidbth
    // re-reads CachedServices only when Bluetooth cycles.
    [Fact]
    public void KeyboardSdpPatch_BothOutcomesNameTheBluetoothCycle()
    {
        var already = DriverInstaller.KeyboardSdpAlreadyAppliedMessage();
        Assert.Contains("already in this keyboard's Bluetooth record", already,
            StringComparison.Ordinal);
        Assert.Contains("nothing was run", already, StringComparison.Ordinal);
        Assert.Contains("turn Bluetooth off", already, StringComparison.Ordinal);
        Assert.Contains("Re-pairing the keyboard erases the unlock", already,
            StringComparison.Ordinal);

        var done = DriverInstaller.KeyboardSdpNextStepMessage();
        Assert.Contains("turn Bluetooth off and on again", done, StringComparison.Ordinal);
        Assert.Contains("Running this again later is safe", done, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyboardSdpPatch_LogTailIsTheLastLines_AndAMissingLogIsNotAnError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mt-kbd-patch-{Guid.NewGuid():N}.log");
        Assert.Null(DriverInstaller.ReadPatchLogTail(path));

        try
        {
            File.WriteAllLines(path, ["one", "two", "three", "four"]);
            var tail = DriverInstaller.ReadPatchLogTail(path, maxLines: 2);
            Assert.Equal("three\nfour", tail);
            Assert.Equal("one\ntwo\nthree\nfour", DriverInstaller.ReadPatchLogTail(path, maxLines: 99));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The gate that decides whether the elevated script runs at all. It asks a
    // stricter question than the verdict the status rows read: not "is the
    // unlock there" but "is there anything left for the script to do", so it
    // skips only a finished cache - something patched, nothing unpatched. A
    // half-patched cache still holds a record the script would patch; a cache
    // holding a candidate record neither fingerprint decides still holds one
    // the script would report as blocked with its bytes (exit 4), and skipping
    // would mean the user never gets that dump. An empty census is no evidence
    // at all, and refusing to run on it would turn "I cannot tell" into "you
    // cannot have it". Asserted on the predicate rather than through
    // OfferKeyboardSdpPatch because that method reads the live registry and
    // shows message boxes.
    [Fact]
    public void KeyboardSdpPatch_OnlyAFullyPatchedCacheSkipsTheRun()
    {
        Assert.True(DriverInstaller.SkipsKeyboardSdpPatch(
            new SdpCacheCensus(Candidates: 2, Patched: 2, Stock: 0)));
        // half-patched - a re-pair rewrote one subkey
        Assert.False(DriverInstaller.SkipsKeyboardSdpPatch(
            new SdpCacheCensus(Candidates: 2, Patched: 1, Stock: 1)));
        // one patched record and one candidate the reader cannot decide: the
        // status row reads Applied here, and the run must still happen
        Assert.False(DriverInstaller.SkipsKeyboardSdpPatch(
            new SdpCacheCensus(Candidates: 2, Patched: 1, Stock: 0)));
        Assert.False(DriverInstaller.SkipsKeyboardSdpPatch(
            new SdpCacheCensus(Candidates: 2, Patched: 0, Stock: 2)));
        // an unreadable hive, or no candidate record at all
        Assert.False(DriverInstaller.SkipsKeyboardSdpPatch(default));
    }

    // Exit 4 is the run that unlocked some records and was blocked on another,
    // so the dialog has to name both halves.
    [Fact]
    public void KeyboardSdpPatch_PartialExitSaysHalfTheCacheIsUnlocked()
    {
        var partial = DriverInstaller.KeyboardSdpFailureMessage(
            DriverInstaller.KeyboardPatchExitPartial,
            @"C:\logs\kbd-patch.log",
            "SUMMARY patched=1 blocked=1");

        // what happened: part unlocked, part not
        Assert.Contains("Part of this keyboard's Bluetooth cache was unlocked", partial,
            StringComparison.Ordinal);
        Assert.Contains("but not all of it", partial, StringComparison.Ordinal);
        // never the "nothing was changed" of exits 2 and 3
        Assert.DoesNotContain("nothing was changed", partial, StringComparison.Ordinal);
        // the step that can still produce a percent, and the way to report the rest
        Assert.Contains("Turn Bluetooth off and on again", partial, StringComparison.Ordinal);
        Assert.Contains("please report the log below", partial, StringComparison.Ordinal);
        // never the noun: the script's own $blocked counts a whole unreadable
        // cache subkey as well as a record, and its messages avoid the word for
        // exactly that reason
        Assert.DoesNotContain("blocked record", partial, StringComparison.Ordinal);
        Assert.Contains(@"Log: C:\logs\kbd-patch.log", partial, StringComparison.Ordinal);
        Assert.Contains("SUMMARY patched=1 blocked=1", partial, StringComparison.Ordinal);
    }

    // Exit 5 is the run that never saw a record: the cache subkey would not
    // open. Its remedy is this PC's registry permissions, so it must not send
    // the user off to report a descriptor shape it never read.
    [Fact]
    public void KeyboardSdpPatch_UnreadableCacheIsNotADescriptorProblem()
    {
        var unreadable = DriverInstaller.KeyboardSdpFailureMessage(
            DriverInstaller.KeyboardPatchExitCacheUnreadable,
            @"C:\logs\kbd-patch.log",
            "NOT PATCHED. 2 cache subkey(s) could not be read");

        Assert.Contains("could be read", unreadable, StringComparison.Ordinal);
        Assert.Contains("permissions problem with this PC's registry", unreadable,
            StringComparison.Ordinal);
        Assert.Contains("not a fault of the keyboard", unreadable, StringComparison.Ordinal);
        // never the descriptor errand exit 3 sends the user on, and never a
        // promise of bytes the run never got to read
        Assert.DoesNotContain("descriptor", unreadable, StringComparison.Ordinal);
        Assert.DoesNotContain("bytes", unreadable, StringComparison.Ordinal);
        Assert.Contains("NOT PATCHED. 2 cache subkey(s) could not be read", unreadable,
            StringComparison.Ordinal);

        // and the permissions wording stays out of every other code
        foreach (var other in new[]
                 {
                     DriverInstaller.KeyboardPatchExitNoRecord,
                     DriverInstaller.KeyboardPatchExitNotPatched,
                     DriverInstaller.KeyboardPatchExitPartial,
                     7,
                 })
        {
            var text = DriverInstaller.KeyboardSdpFailureMessage(other, @"C:\logs\x.log", null);
            Assert.DoesNotContain("permissions problem", text, StringComparison.Ordinal);
        }

        // exit 3 now means examined-and-refused only, and says so
        var notPatched = DriverInstaller.KeyboardSdpFailureMessage(
            DriverInstaller.KeyboardPatchExitNotPatched, @"C:\logs\x.log", null);
        Assert.Contains("descriptor shape the unlock does not recognise", notPatched,
            StringComparison.Ordinal);
        Assert.DoesNotContain("whole cache entry", notPatched, StringComparison.Ordinal);
    }

    // The elevated identity can fail to write the log at all, and then the
    // dialog's "Log: <path>" points at a file that does not exist. Say so, so
    // the user reports the missing log instead of hunting for it.
    [Fact]
    public void KeyboardSdpPatch_FailureSaysWhenNoLogWasReadable()
    {
        var noLog = DriverInstaller.KeyboardSdpFailureMessage(
            DriverInstaller.KeyboardPatchExitNotPatched, @"C:\logs\kbd-patch.log", null);

        Assert.Contains("no readable log at that path", noLog, StringComparison.Ordinal);

        var withLog = DriverInstaller.KeyboardSdpFailureMessage(
            DriverInstaller.KeyboardPatchExitNotPatched, @"C:\logs\kbd-patch.log",
            "SUMMARY patched=0 blocked=2");
        Assert.DoesNotContain("no readable log", withLog, StringComparison.Ordinal);
        Assert.Contains("SUMMARY patched=0 blocked=2", withLog, StringComparison.Ordinal);
    }

    [Fact]
    public void PathAOnKmdf_WarnsThatWhqlAppleInfCanOutrankTheTestSignedKmdfPackage()
    {
        var text = DriverInstaller.KmdfDisplacementWarning();

        // the ranking mechanism, not just "this is risky"
        Assert.Contains("Windows ranks a WHQL-signed package above the test-signed KMDF package",
            text, StringComparison.Ordinal);
        Assert.Contains("0323", text, StringComparison.Ordinal);
        // a real possibility, never a promise
        Assert.Contains("can move it off", text, StringComparison.Ordinal);
        Assert.DoesNotContain("will move", text, StringComparison.Ordinal);
        // the concrete loss and the way back
        Assert.Contains("tunable scroll", text, StringComparison.Ordinal);
        Assert.Contains("battery", text, StringComparison.Ordinal);
        Assert.Contains("pick KMDF again in the tray", text, StringComparison.Ordinal);

        // shown to a user who has KMDF to lose, and to a caller that did not
        // say; never to a user already off KMDF
        Assert.True(DriverInstaller.WarnsKmdfDisplacement(DriverStatus.PatchedKmdf));
        Assert.True(DriverInstaller.WarnsKmdfDisplacement(null));
        Assert.False(DriverInstaller.WarnsKmdfDisplacement(DriverStatus.PathAPatched));
        Assert.False(DriverInstaller.WarnsKmdfDisplacement(DriverStatus.StockKmdf));
    }

    [Fact]
    public void DeclinedUacPrompt_IsTheNativeErrorCode_NotEveryElevationFailure()
    {
        // 1223 ERROR_CANCELLED is what ShellExecute reports when the user
        // clicks No, and the only signal that may downgrade an offer from
        // Failed to Cancelled. The message is localised, so the code decides.
        Assert.True(DriverInstaller.IsUacDeclined(new System.ComponentModel.Win32Exception(1223)));
        Assert.True(DriverInstaller.IsUacDeclined(
            new System.ComponentModel.Win32Exception(1223, "Der Vorgang wurde durch den Benutzer abgebrochen.")));

        // Elevation that was granted and then went wrong is still a failure:
        // 2 ERROR_FILE_NOT_FOUND, 740 ERROR_ELEVATION_REQUIRED, and
        // RunElevated's own timeout / non-zero-exit / no-process throws.
        Assert.False(DriverInstaller.IsUacDeclined(new System.ComponentModel.Win32Exception(2)));
        Assert.False(DriverInstaller.IsUacDeclined(new System.ComponentModel.Win32Exception(740)));
        Assert.False(DriverInstaller.IsUacDeclined(
            new InvalidOperationException("Install-KMDF.cmd exited 1.")));
        Assert.False(DriverInstaller.IsUacDeclined(
            new InvalidOperationException("Could not start elevated process (UAC cancelled?).")));
    }

    [Fact]
    public void V3StockRestore_SecondDeclineNamesTheHalfThatAlreadyRan()
    {
        var text = DriverInstaller.V3StockPartialRestoreMessage();

        // Both halves by name: one ran, one did not.
        Assert.Contains(DriverPackageCatalog.KmdfUninstallCmdRelativePath, text,
            StringComparison.Ordinal);
        Assert.Contains(DriverPackageCatalog.PathAUninstallScriptRelativePath, text,
            StringComparison.Ordinal);
        Assert.Contains("was declined", text, StringComparison.Ordinal);
        // the state the machine is actually in, and the way out
        Assert.Contains("not on stock HidBth yet", text, StringComparison.Ordinal);
        Assert.Contains("Run Stock again", text, StringComparison.Ordinal);
    }

    // Step 2 failing for a non-UAC reason leaves the same half-changed machine
    // as the declined prompt, so the message must still name the half that ran
    // and must carry the thrown reason: a 15-minute timeout and "exited 1" are
    // different problems and the reason is the only thing separating them.
    [Fact]
    public void V3StockRestore_PostStepOneFailureCarriesTheReasonWithThePartialState()
    {
        // RunElevated's own non-zero-exit text: it throws
        // $"{Path.GetFileName(fileName)} exited {p.ExitCode}.", and step 2 of
        // the Stock restore runs through powershell.exe.
        const string reason = "powershell.exe exited 1.";
        var text = DriverInstaller.V3StockPartialRestoreMessage(reason);

        Assert.Contains(DriverPackageCatalog.KmdfUninstallCmdRelativePath, text,
            StringComparison.Ordinal);
        Assert.Contains(DriverPackageCatalog.PathAUninstallScriptRelativePath, text,
            StringComparison.Ordinal);
        Assert.Contains("not on stock HidBth yet", text, StringComparison.Ordinal);
        Assert.Contains(reason, text, StringComparison.Ordinal);
        // It did not claim a decline it never observed.
        Assert.DoesNotContain("was declined", text, StringComparison.Ordinal);
    }
}
