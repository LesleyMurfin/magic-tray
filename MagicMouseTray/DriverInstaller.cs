// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace MagicMouseTray;

// Pulls a user-selected package and installs it.
// 0323: zip LesleyMurfin/magic-mouse-v3-windows-fix and run THAT repo's
// KMDF one-click under v2-kmdf-driver. Never PATH-A Install-MagicMousePatch.ps1.
// Never vendor Driver.c / INF / .sys / install scripts. No bind-filter generation.
internal static class DriverInstaller
{
    internal const string ZipUrlFormat =
        "https://github.com/{0}/{1}/archive/refs/heads/{2}.zip";

    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
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
        if (!pkg.Published)
            throw new InvalidOperationException(pkg.MissingReason ?? "No published package for this device.");
        if (DriverPackageCatalog.IsForbiddenSource(pkg))
            throw new InvalidOperationException("Refusing magic-tray/driver/ as a package source.");

        Logger.Log($"DRIVER_PULL repo={pkg.Owner}/{pkg.Repo} ref={pkg.GitRef} kind={pkg.Kind} path={pkg.PathInRepo} pid={devicePid}");

        return pkg.Kind switch
        {
            InstallKind.KmdfPull => await PullRepoAndRunTheirInstallerAsync(pkg, devicePid, ct),
            InstallKind.InfPnputil => await PullInfAndPnputilAsync(pkg, devicePid, ct),
            InstallKind.KeyboardSdpPatch => RunKeyboardSdpPatch(devicePid),
            _ => throw new InvalidOperationException($"Unknown install kind {pkg.Kind}."),
        };
    }

    static async Task<string> DownloadZipAsync(DriverPackage pkg, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheRoot);
        var zipPath = Path.Combine(CacheRoot, $"{pkg.Repo}-{pkg.GitRef}-{pkg.Id}.zip");
        var extractDir = Path.Combine(CacheRoot, $"{pkg.Repo}-{pkg.GitRef}-{pkg.Id}");
        var url = ZipUrl(pkg);

        await using (var net = await Http.GetStreamAsync(url, ct))
        await using (var file = File.Create(zipPath))
            await net.CopyToAsync(file, ct);

        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, extractDir);
        return extractDir;
    }

    static async Task<string> PullRepoAndRunTheirInstallerAsync(DriverPackage pkg, string devicePid, CancellationToken ct)
    {
        if (!devicePid.Equals("0323", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("KMDF MagicMouseDriver is 0323-only. Will not retarget 030D.");

        var extractDir = await DownloadZipAsync(pkg, ct);
        var script = FindKmdfOneClick(extractDir, pkg.PathInRepo)
            ?? throw new InvalidOperationException(
                $"Pulled {pkg.Owner}/{pkg.Repo} but v2-kmdf-driver has no KMDF one-click " +
                "(sign/install / SYSTEM task). Will not run PATH-A Install-MagicMousePatch.ps1. " +
                "Will not generate bind-filter.ps1. Publish the KMDF installer in that folder.");

        if (DriverPackageCatalog.IsLocalVendorPath(script))
            throw new InvalidOperationException("Refusing local magic-tray/driver script.");
        if (IsPathAInstaller(script))
            throw new InvalidOperationException("Refusing PATH-A Install-MagicMousePatch.ps1.");

        Logger.Log($"DRIVER_KMDF_ONECLICK script={script} pid={devicePid}");
        RunElevated(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
            workingDirectory: Path.GetDirectoryName(script),
            windowStyle: ProcessWindowStyle.Normal);
        return script;
    }

    // PATH-A patched applewirelessmouse.sys — SHIP-BLOCKER. Never run or fall back.
    internal static bool IsPathAInstaller(string path)
    {
        var n = path.Replace('\\', '/');
        return n.Contains("v1-binary-patch", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Install-MagicMousePatch", StringComparison.OrdinalIgnoreCase);
    }

    // KMDF one-click under v2-kmdf-driver only. Null if they have not published it.
    internal static string? FindKmdfOneClick(string extractRoot, string pathInRepo)
    {
        if (!Directory.Exists(extractRoot))
            return null;

        if (!string.IsNullOrEmpty(pathInRepo) && !IsPathAInstaller(pathInRepo))
        {
            var rel = pathInRepo.Replace('/', Path.DirectorySeparatorChar);
            foreach (var file in Directory.EnumerateFiles(extractRoot, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(rel, StringComparison.OrdinalIgnoreCase)
                    && file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                    && !IsPathAInstaller(file))
                    return file;
            }

            var dir = FindPackageDir(extractRoot, pathInRepo);
            if (dir != null)
            {
                var inDir = FirstKmdfScriptUnder(dir);
                if (inDir != null) return inDir;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(extractRoot, "v2-kmdf-driver", SearchOption.AllDirectories))
        {
            var found = FirstKmdfScriptUnder(dir);
            if (found != null) return found;
        }

        return null;
    }

    static string? FirstKmdfScriptUnder(string dir)
    {
        foreach (var pattern in new[] { "*Install*.ps1", "*OneClick*.ps1", "*Sign*.ps1", "*Easy*.ps1" })
        {
            foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).Contains("Uninstall", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsPathAInstaller(file))
                    continue;
                return file;
            }
        }
        return null;
    }

    static async Task<string> PullInfAndPnputilAsync(DriverPackage pkg, string devicePid, CancellationToken ct)
    {
        var extractDir = await DownloadZipAsync(pkg, ct);
        var packageDir = FindPackageDir(extractDir, pkg.PathInRepo)
            ?? throw new InvalidOperationException($"Pulled {pkg.Owner}/{pkg.Repo} but '{pkg.PathInRepo}' was not in the zip.");

        var inf = Directory.EnumerateFiles(packageDir, "*.inf", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException($"No INF in {pkg.Owner}/{pkg.Repo}/{pkg.PathInRepo}.");

        if (DriverPackageCatalog.IsLocalVendorPath(inf))
            throw new InvalidOperationException("Refusing local magic-tray/driver INF.");

        Logger.Log($"DRIVER_INSTALL inf={inf} pid={devicePid}");
        RunElevated("pnputil.exe", $"/add-driver \"{inf}\" /install");
        return inf;
    }

    static string RunKeyboardSdpPatch(string devicePid)
    {
        var script = FindKeyboardPatchScript()
            ?? throw new InvalidOperationException(
                "scripts/kbd-patch-cachedservices.ps1 not found next to the tray. Keyboard Best is the PATH-C SDP patch, not Keymagic2.");

        var mac = TryDiscoverKeyboardMac(devicePid);
        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
        if (!string.IsNullOrEmpty(mac))
            args += $" -Mac \"{mac}\"";

        Logger.Log($"DRIVER_SDP script={script} mac={mac ?? "(script default)"} pid={devicePid}");
        RunElevated("powershell.exe", args);
        return script;
    }

    internal static string? FindKeyboardPatchScript()
    {
        var names = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "scripts", "kbd-patch-cachedservices.ps1"),
            Path.Combine(AppContext.BaseDirectory, "kbd-patch-cachedservices.ps1"),
        };
        foreach (var p in names)
            if (File.Exists(p)) return p;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "kbd-patch-cachedservices.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // BTHENUM instance tails with the BT MAC, e.g. ...&E806884B0741_C00000000
    internal static string? TryDiscoverKeyboardMac(string pid)
    {
        foreach (var path in HidNative.EnumerateHidPaths())
        {
            if (!path.Contains("pid&" + pid, StringComparison.OrdinalIgnoreCase)
                && !path.Contains("pid_" + pid, StringComparison.OrdinalIgnoreCase))
                continue;
            var m = Regex.Match(path, @"([0-9A-Fa-f]{12})_C00000000", RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    internal static string? FindPackageDir(string extractRoot, string pathInRepo)
    {
        if (string.IsNullOrEmpty(pathInRepo))
            return extractRoot;
        var rel = pathInRepo.Replace('/', Path.DirectorySeparatorChar);
        foreach (var dir in Directory.EnumerateDirectories(extractRoot, "*", SearchOption.AllDirectories))
        {
            if (dir.EndsWith(rel, StringComparison.OrdinalIgnoreCase))
                return dir;
        }
        return null;
    }

    static void RunElevated(
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = windowStyle,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        using var p = Process.Start(psi);
        if (p is null)
            throw new InvalidOperationException("Could not start elevated process (UAC cancelled?).");
        if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(5)).TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new InvalidOperationException($"{fileName} timed out.");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited {p.ExitCode}.");
    }
}
