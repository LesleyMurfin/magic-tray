// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace MagicMouseTray;

// Pulls a user-selected package and installs it.
// KMDF is downloaded from LesleyMurfin/magic-mouse-v3-windows-fix — never from
// magic-tray/driver/. No leftover mm-dev / install-driver dual-filter scripts.
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
            InstallKind.KmdfPull => await PullKmdfAndBindAsync(pkg, devicePid, ct),
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

    static async Task<string> PullKmdfAndBindAsync(DriverPackage pkg, string devicePid, CancellationToken ct)
    {
        if (!devicePid.Equals("0323", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("KMDF MagicMouseDriver is 0323-only. Will not retarget 030D.");

        var extractDir = await DownloadZipAsync(pkg, ct);
        var packageDir = FindPackageDir(extractDir, pkg.PathInRepo)
            ?? throw new InvalidOperationException($"Pulled {pkg.Owner}/{pkg.Repo} but '{pkg.PathInRepo}' was not in the zip.");

        var inf = Directory.EnumerateFiles(packageDir, "*.inf", SearchOption.AllDirectories).FirstOrDefault();
        if (inf is null)
            throw new InvalidOperationException(
                $"No INF in {pkg.Owner}/{pkg.Repo}/{pkg.PathInRepo}. KMDF ships from that repo — magic-tray will not vendor Driver.c / INF / .sys. Publish MagicMouseDriver.inf there.");

        if (DriverPackageCatalog.IsLocalVendorPath(inf))
            throw new InvalidOperationException("Refusing local magic-tray/driver INF.");

        Logger.Log($"DRIVER_INSTALL inf={inf} pid={devicePid}");
        RunElevated("pnputil.exe", $"/add-driver \"{inf}\" /install");
        RunElevatedBindFilter(devicePid, DriverHealthChecker.KmdfFilterName);
        return inf;
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

    static void RunElevatedBindFilter(string pid, string filterName)
    {
        var script = Path.Combine(CacheRoot, "bind-filter.ps1");
        Directory.CreateDirectory(CacheRoot);
        File.WriteAllText(script, BindFilterScript);
        RunElevated("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -DevicePid \"{pid}\" -FilterName \"{filterName}\"");
    }

    static void RunElevated(string fileName, string arguments, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
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

    // Sole LowerFilters on the selected PID. 0323-only callers pass MagicMouseDriver.
    const string BindFilterScript = """
        param([Parameter(Mandatory=$true)][string]$DevicePid, [Parameter(Mandatory=$true)][string]$FilterName)
        $ErrorActionPreference = 'Stop'
        $pidToken = "PID&$DevicePid"
        $dev = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
            $_.InstanceId -match 'BTHENUM' -and $_.InstanceId -match [regex]::Escape($pidToken) -and $_.Class -eq 'HIDClass'
        } | Select-Object -First 1
        if (-not $dev) { Write-Error "No paired HID device for PID $DevicePid — will not bind."; exit 2 }
        $reg = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($dev.InstanceId)"
        if (-not (Test-Path $reg)) { throw "Device registry path missing: $reg" }
        Set-ItemProperty -Path $reg -Name LowerFilters -Value @($FilterName) -Type MultiString
        try {
            Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction Stop
            Start-Sleep -Seconds 2
            Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction Stop
        } catch {
            Write-Host "Device restart failed; reboot to finish bind."
        }
        """;
}
