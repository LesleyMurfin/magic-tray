// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// Public GitHub / Apple Boot Camp packages the tray may SELECT.
// Never LesleyMurfin/*, never Magic Utilities binaries, never magic-tray/driver/.
internal enum DriverPackageId
{
    Best,
    AppleWirelessMouse,
    AppleKeyboardMagic2,
}

internal enum InstallKind
{
    InfPnputil,           // zip has INF + CAT + SYS (tealtadpole / Rain9333 dump)
    OfficialSysBind,      // zip has WHQL AppleWirelessMouse.sys; bind selected PID
    BrigadierKeyboard,    // fetch Boot Camp ESD via brigadier; Keymagic2.inf
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
    internal string RepoUrl => $"https://github.com/{Owner}/{Repo}";
}

internal static class DriverPackageCatalog
{
    // Verified 2026-08-31 against Lesley's mapping + GitHub contents.
    // tealtadpole and Rain9333 are the same Boot Camp dump (identical INF/CAT/SYS SHAs).
    // INF DriverVer 08/08/2019,6.1.7700.0 lists 030D / 0310 / 0269 only — not 0323.
    // Rain9333 is not strictly better (same bits). Her tray cites tealtadpole for Win11.
    internal const string TealOwner = "tealtadpole";
    internal const string TealRepo = "MagicMouse2DriversWin11x64";
    internal const string TealRef = "master";

    // Same AppleWirelessMouse.sys blob as tealtadpole/Rain9333 (WHQL). No INF; binds 0323.
    internal const string SbagiriciOwner = "sbagirici";
    internal const string SbagiriciRepo = "apple-magic-mouse-scroll-fix-windows";
    internal const string SbagiriciRef = "master";

    internal const string BrigadierOwner = "timsutton";
    internal const string BrigadierRepo = "brigadier";
    internal const string BrigadierRef = "main";
    internal const string BrigadierModel = "MacBookAir9,1";

    static readonly DriverPackage Mouse0323 = new(
        DriverPackageId.Best, "Best for this mouse",
        SbagiriciOwner, SbagiriciRepo, SbagiriciRef, "driver",
        InstallKind.OfficialSysBind,
        ["0323"], Published: true, MissingReason: null);

    static readonly DriverPackage Mouse0323Named = Mouse0323 with
    {
        Id = DriverPackageId.AppleWirelessMouse,
        MenuLabel = "AppleWirelessMouse (WHQL)",
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

    static readonly DriverPackage KeyboardBest = new(
        DriverPackageId.Best, "Best for this keyboard",
        BrigadierOwner, BrigadierRepo, BrigadierRef, "AppleKeyboardMagic2",
        InstallKind.BrigadierKeyboard,
        [], Published: true, MissingReason: null);

    static readonly DriverPackage KeyboardNamed = KeyboardBest with
    {
        Id = DriverPackageId.AppleKeyboardMagic2,
        MenuLabel = "Keymagic2 (Boot Camp)",
    };

    internal static DriverPackage BestForPid(string pid)
    {
        pid = pid.ToLowerInvariant();
        if (pid == "0323") return Mouse0323;
        if (pid is "030d" or "0310" or "0269") return MouseStock;
        return KeyboardBest with { Pids = [pid] };
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
            return [KeyboardBest, KeyboardNamed];
        return [];
    }

    internal static bool IsForbiddenSource(DriverPackage pkg) =>
        IsForbiddenOwnerRepo(pkg.Owner, pkg.Repo);

    internal static bool IsForbiddenOwnerRepo(string owner, string repo)
    {
        if (owner.Equals("LesleyMurfin", StringComparison.OrdinalIgnoreCase))
            return true;
        return repo.Contains("magic-tray", StringComparison.OrdinalIgnoreCase)
            || repo.Contains("magic-mouse-v3-windows-fix", StringComparison.OrdinalIgnoreCase)
            || repo.Contains("apple-kb-monitor", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLocalVendorPath(string path)
    {
        var n = path.Replace('\\', '/');
        return n.Contains("magic-tray/driver", StringComparison.OrdinalIgnoreCase);
    }
}
