// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;

namespace MagicMouseTray;

// Pulls a selected package from GitHub and installs it. Never reads
// magic-tray/driver/ (that folder is not SSOT). Never runs leftover
// mm-dev / install-driver / MM-Dev-Cycle flip scripts.
internal static class DriverInstaller
{
    internal const string ZipUrlFormat =
        "https://github.com/{0}/{1}/archive/refs/heads/{2}.zip";

    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    static DriverInstaller()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("MagicMouseTray/1.0");
    }

    internal static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MagicMouseTray", "driver-cache");

    internal static string ZipUrl(DriverPackage pkg) =>
        string.Format(ZipUrlFormat, pkg.Owner, pkg.Repo, pkg.GitRef);

    internal static async Task<string> PullAndInstallAsync(DriverPackage pkg, string devicePid, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(devicePid))
            throw new InvalidOperationException("No device PID — will not install.");
        if (!pkg.Published || string.IsNullOrEmpty(pkg.Repo))
            throw new InvalidOperationException(pkg.MissingReason ?? "No published driver repo for this device.");
        if (DriverPackageCatalog.IsLocalVendorPath(pkg.PathInRepo))
            throw new InvalidOperationException("Refusing to install from magic-tray/driver/ — KMDF SSOT is LesleyMurfin/magic-mouse-v3-windows-fix.");

        var url = ZipUrl(pkg);
        Logger.Log($"DRIVER_PULL repo={pkg.Owner}/{pkg.Repo} ref={pkg.GitRef} path={pkg.PathInRepo} url={url} pid={devicePid}");

        Directory.CreateDirectory(CacheRoot);
        var zipPath = Path.Combine(CacheRoot, $"{pkg.Repo}-{pkg.GitRef}-{pkg.Id}.zip");
        var extractDir = Path.Combine(CacheRoot, $"{pkg.Repo}-{pkg.GitRef}-{pkg.Id}");

        await using (var net = await Http.GetStreamAsync(url, ct))
        await using (var file = File.Create(zipPath))
            await net.CopyToAsync(file, ct);

        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var packageDir = FindPackageDir(extractDir, pkg.PathInRepo);
        if (packageDir is null)
            throw new InvalidOperationException(
                $"Pulled {pkg.Owner}/{pkg.Repo} but '{pkg.PathInRepo}' was not in the zip.");

        var inf = Directory.EnumerateFiles(packageDir, "*.inf", SearchOption.AllDirectories).FirstOrDefault();
        if (inf is null)
            throw new InvalidOperationException(
                $"No INF in {pkg.Owner}/{pkg.Repo}/{pkg.PathInRepo}. The tray will not run leftover install-driver.ps1 / mm-dev.ps1. Publish an INF in that repo path.");

        if (DriverPackageCatalog.IsLocalVendorPath(inf))
            throw new InvalidOperationException("Refusing local magic-tray/driver INF.");

        Logger.Log($"DRIVER_INSTALL inf={inf} pid={devicePid}");
        RunElevatedPnputil(inf);
        return inf;
    }

    internal static string? FindPackageDir(string extractRoot, string pathInRepo)
    {
        var rel = pathInRepo.Replace('/', Path.DirectorySeparatorChar);
        foreach (var dir in Directory.EnumerateDirectories(extractRoot, "*", SearchOption.AllDirectories))
        {
            if (dir.EndsWith(rel, StringComparison.OrdinalIgnoreCase))
                return dir;
        }
        return null;
    }

    static void RunElevatedPnputil(string infPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pnputil.exe",
            Arguments = $"/add-driver \"{infPath}\" /install",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var p = Process.Start(psi);
        if (p is null)
            throw new InvalidOperationException("Could not start elevated pnputil (UAC cancelled?).");
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"pnputil exited {p.ExitCode}.");
    }
}
