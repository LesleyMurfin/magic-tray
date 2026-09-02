// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// Single source for driver package URLs and names (#67). No other file
// may hardcode tealtadpole / v3-fix / kbd-patch paths.
internal static class DriverPackageCatalog
{
    // --- v1/v2 scroll: tealtadpole Boot Camp INF (open/documented, never silent rebind) ---
    internal const string TealtadpoleOwner = "tealtadpole";
    internal const string TealtadpoleRepo = "MagicMouse2DriversWin11x64";
    internal const string TealtadpoleRef = "master";
    internal const string TealtadpolePageUrl =
        "https://github.com/tealtadpole/MagicMouse2DriversWin11x64";
    internal const string TealtadpoleRawInfUrl =
        "https://raw.githubusercontent.com/tealtadpole/MagicMouse2DriversWin11x64/master/AppleWirelessMouse/AppleWirelessMouse.inf";
    internal const string TealtadpoleInfPathInRepo = "AppleWirelessMouse/AppleWirelessMouse.inf";
    internal const string AppleFilterServiceName = "applewirelessmouse";

    // --- v1 trackpad: tealtadpole Boot Camp INF (open GitHub, never pnputil) ---
    internal const string TealtadpoleTrackpadPageUrl =
        "https://github.com/tealtadpole/MagicMouse2DriversWin11x64/tree/master/AppleWirelessTrackpad";
    internal const string TealtadpoleTrackpadRawInfUrl =
        "https://raw.githubusercontent.com/tealtadpole/MagicMouse2DriversWin11x64/master/AppleWirelessTrackpad/AppleWirelessTrackpad.inf";
    internal const string TealtadpoleTrackpadInfPathInRepo =
        "AppleWirelessTrackpad/AppleWirelessTrackpad.inf";
    internal const string AppleTrackpadFilterServiceName = "applewtp";

    // --- 0323 scroll: LesleyMurfin/magic-mouse-v3-windows-fix (KMDF, PathA, Stock) ---
    internal const string V3RepoOwner = "LesleyMurfin";
    internal const string V3RepoName = "magic-mouse-v3-windows-fix";
    internal const string V3RepoRef = "main";
    internal const string V3RepoUrl =
        "https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix";
    internal const string KmdfInstallCmdRelativePath = "v2-kmdf-driver/Install-KMDF.cmd";
    internal const string KmdfOneClickName = "Install-KMDF.cmd";
    internal const string KmdfUninstallCmdRelativePath = "v2-kmdf-driver/Uninstall-KMDF.cmd";
    internal const string KmdfUninstallCmdName = "Uninstall-KMDF.cmd";
    internal const string PatchedKmdfServiceName = "MagicMouseDriver";
    internal const string PatchedKmdfSysFileName = "MagicMouseDriver.sys";
    internal const string StockHidServiceName = "HidBth";
    internal const string PathAInstallScriptRelativePath =
        "v1-binary-patch/installer/Install-MagicMousePatch.ps1";
    internal const string PathAInstallScriptName = "Install-MagicMousePatch.ps1";
    internal const string PathAUninstallScriptRelativePath =
        "v1-binary-patch/installer/Uninstall-MagicMousePatch.ps1";
    internal const string PathAUninstallScriptName = "Uninstall-MagicMousePatch.ps1";

    // --- Keyboard battery: PATH-C SDP patch (user-initiated, elevated) ---
    internal const string KeyboardPatchScriptPath = "scripts/kbd-patch-cachedservices.ps1";
    internal const string KeyboardPatchScriptName = "kbd-patch-cachedservices.ps1";

    internal static string V3ZipUrl =>
        $"https://github.com/{V3RepoOwner}/{V3RepoName}/archive/refs/heads/{V3RepoRef}.zip";
}
