// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// Packages the tray may SELECT. 0323 pulls LesleyMurfin/magic-mouse-v3-windows-fix
// and runs that repo's installer — never copied into magic-tray/driver/.
internal enum DriverPackageId
{
    Best,
    MagicMouseDriver,
    AppleWirelessMouse,
    KeyboardSdpPatch,
}

internal enum InstallKind
{
    KmdfPull,             // zip magic-mouse-v3-windows-fix and run THAT repo's sign+install
    InfPnputil,           // tealtadpole Boot Camp INF (030D / 0310 / 0269)
    KeyboardSdpPatch,     // scripts/kbd-patch-cachedservices.ps1 — no kernel
}

internal sealed record DriverPackage(
    DriverPackageId Id,
    string MenuLabel,
    string Owner,
    string Repo,
    string GitRef,
    string PathInRepo,
    InstallKind Kind,
    string[] Pids,
    bool Published,
    string? MissingReason)
{
    internal string RepoUrl => string.IsNullOrEmpty(Repo)
        ? ""
        : $"https://github.com/{Owner}/{Repo}";
}

internal static class DriverPackageCatalog
{
    internal const string KmdfOwner = "LesleyMurfin";
    internal const string KmdfRepo = "magic-mouse-v3-windows-fix";
    internal const string KmdfRef = "main";
    // Published easy sign+install in that repo. Do not copy this script here.
    internal const string KmdfPath = "v1-binary-patch/installer/Install-MagicMousePatch.ps1";
    internal const string KmdfRepoUrl = "https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix";

    internal const string TealOwner = "tealtadpole";
    internal const string TealRepo = "MagicMouse2DriversWin11x64";
    internal const string TealRef = "master";

    static readonly DriverPackage Mouse0323 = new(
        DriverPackageId.Best, "Best for this mouse",
        KmdfOwner, KmdfRepo, KmdfRef, KmdfPath,
        InstallKind.KmdfPull,
        ["0323"], Published: true, MissingReason: null);

    static readonly DriverPackage Mouse0323Named = Mouse0323 with
    {
        Id = DriverPackageId.MagicMouseDriver,
        MenuLabel = "MagicMouseDriver (KMDF)",
    };

    static readonly DriverPackage MouseStock = new(
        DriverPackageId.Best, "Best for this mouse",
        TealOwner, TealRepo, TealRef, "AppleWirelessMouse",
        InstallKind.InfPnputil,
        ["030d", "0310", "0269"], Published: true, MissingReason: null);

    static readonly DriverPackage MouseStockNamed = MouseStock with
    {
        Id = DriverPackageId.AppleWirelessMouse,
        MenuLabel = "AppleWirelessMouse (Boot Camp INF)",
    };

    static readonly DriverPackage KeyboardSdp = new(
        DriverPackageId.Best, "Best for this keyboard",
        "", "", "", "scripts/kbd-patch-cachedservices.ps1",
        InstallKind.KeyboardSdpPatch,
        [], Published: true, MissingReason: null);

    static readonly DriverPackage KeyboardSdpNamed = KeyboardSdp with
    {
        Id = DriverPackageId.KeyboardSdpPatch,
        MenuLabel = "Battery SDP patch (PATH-C)",
    };

    internal static DriverPackage BestForPid(string pid)
    {
        pid = pid.ToLowerInvariant();
        if (pid == "0323") return Mouse0323;
        if (pid is "030d" or "0310" or "0269") return MouseStock;
        return KeyboardSdp with { Pids = [pid] };
    }

    internal static IReadOnlyList<DriverPackage> ChoicesFor(DeviceKind kind, string pid)
    {
        pid = pid.ToLowerInvariant();
        if (kind == DeviceKind.MagicMouseV3 || pid == "0323")
            return [Mouse0323, Mouse0323Named];
        if (kind == DeviceKind.MagicMouseV1 || kind == DeviceKind.MagicMouseV2
            || pid is "030d" or "0310" or "0269")
            return [MouseStock, MouseStockNamed];
        if (kind == DeviceKind.MagicKeyboard)
            return [KeyboardSdp, KeyboardSdpNamed];
        return [];
    }

    // magic-tray must not be a driver package source. KMDF home is allowed as a pull.
    internal static bool IsForbiddenSource(DriverPackage pkg) =>
        pkg.Repo.Contains("magic-tray", StringComparison.OrdinalIgnoreCase)
        || IsLocalVendorPath(pkg.PathInRepo);

    internal static bool IsLocalVendorPath(string path)
    {
        var n = path.Replace('\\', '/');
        return n.Contains("magic-tray/driver", StringComparison.OrdinalIgnoreCase);
    }
}
