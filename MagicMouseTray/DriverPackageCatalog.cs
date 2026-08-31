// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// Packages the tray may SELECT. Install bits are pulled from GitHub — never
// from magic-tray/driver/. Names match LesleyMurfin/magic-mouse-v3-windows-fix.
internal enum DriverPackageId
{
    Best,
    V1,
    V2,
    V3,
    Kmdf,
}

internal sealed record DriverPackage(
    DriverPackageId Id,
    string MenuLabel,
    string Owner,
    string Repo,
    string GitRef,
    string PathInRepo,
    string[] Pids,
    bool Published,
    string? MissingReason);

internal static class DriverPackageCatalog
{
    internal const string KmdfOwner = "LesleyMurfin";
    internal const string KmdfRepo = "magic-mouse-v3-windows-fix";
    internal const string KmdfRef = "main";
    internal const string KmdfRepoUrl = "https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix";

    // Verified 2026-08-31: only this LesleyMurfin repo owns Magic Mouse KMDF / v3 packages.
    // v1-binary-patch and v2-kmdf-driver are successive 0323 packages, not 030D/0269 mice.
    static readonly DriverPackage MouseV1 = new(
        DriverPackageId.V1, "v1",
        KmdfOwner, KmdfRepo, KmdfRef, "v1-binary-patch",
        ["0323"], Published: true, MissingReason: null);

    static readonly DriverPackage MouseV2 = new(
        DriverPackageId.V2, "v2",
        KmdfOwner, KmdfRepo, KmdfRef, "v2-kmdf-driver",
        ["0323"], Published: true, MissingReason: null);

    static readonly DriverPackage MouseV3 = new(
        DriverPackageId.V3, "v3",
        KmdfOwner, KmdfRepo, KmdfRef, "v2-kmdf-driver",
        ["0323"], Published: true, MissingReason: null);

    static readonly DriverPackage MouseKmdf = new(
        DriverPackageId.Kmdf, "KMDF",
        KmdfOwner, KmdfRepo, KmdfRef, "v2-kmdf-driver",
        ["0323"], Published: true, MissingReason: null);

    static readonly DriverPackage MouseBest = new(
        DriverPackageId.Best, "Best for this mouse",
        KmdfOwner, KmdfRepo, KmdfRef, "v2-kmdf-driver",
        ["0323"], Published: true, MissingReason: null);

    static readonly DriverPackage HardwareV1Missing = new(
        DriverPackageId.V1, "v1",
        KmdfOwner, "", KmdfRef, "",
        ["030d", "0310"], Published: false,
        "No LesleyMurfin GitHub repo for Magic Mouse v1 (030D). magic-mouse-v3-windows-fix is 0323-only.");

    static readonly DriverPackage HardwareV2Missing = new(
        DriverPackageId.V2, "v2",
        KmdfOwner, "", KmdfRef, "",
        ["0269"], Published: false,
        "No LesleyMurfin GitHub repo for Magic Mouse v2 (0269). magic-mouse-v3-windows-fix is 0323-only.");

    static readonly DriverPackage KeyboardMissing = new(
        DriverPackageId.Best, "Best for this keyboard",
        KmdfOwner, "", KmdfRef, "",
        [], Published: false,
        "No LesleyMurfin GitHub repo for keyboard drivers (apple-kb-monitor and apple-peripherals were not found).");

    internal static DriverPackage BestForPid(string pid)
    {
        pid = pid.ToLowerInvariant();
        if (pid == "0323") return MouseBest;
        if (pid is "030d" or "0310") return HardwareV1Missing;
        if (pid == "0269") return HardwareV2Missing;
        return KeyboardMissing with { Pids = [pid] };
    }

    internal static IReadOnlyList<DriverPackage> ChoicesFor(DeviceKind kind, string pid)
    {
        pid = pid.ToLowerInvariant();
        if (kind == DeviceKind.MagicMouseV3 || pid == "0323")
            return [MouseBest, MouseKmdf, MouseV3, MouseV2, MouseV1];
        if (kind == DeviceKind.MagicMouseV1 || pid is "030d" or "0310")
            return [HardwareV1Missing];
        if (kind == DeviceKind.MagicMouseV2 || pid == "0269")
            return [HardwareV2Missing];
        if (kind == DeviceKind.MagicKeyboard)
            return [KeyboardMissing];
        return [];
    }

    internal static bool IsLocalVendorPath(string path) =>
        path.Replace('\\', '/').Contains("/driver/", StringComparison.OrdinalIgnoreCase)
        && (path.Contains("MagicMouseDriver", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("Driver.c", StringComparison.OrdinalIgnoreCase));
}
