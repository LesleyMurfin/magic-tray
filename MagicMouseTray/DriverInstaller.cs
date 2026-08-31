// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace MagicMouseTray;

// Pulls a user-selected public package and installs it.
// Never LesleyMurfin/*, never magic-tray/driver/, never leftover mm-dev / MM-Dev-Cycle.
internal static class DriverInstaller
{
    internal const string ZipUrlFormat =
        "https://github.com/{0}/{1}/archive/refs/heads/{2}.zip";

    internal const string BrigadierExeUrl =
        "https://github.com/timsutton/brigadier/releases/download/0.2.6/brigadier.exe";

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
        if (!pkg.Published || string.IsNullOrEmpty(pkg.Repo))
            throw new InvalidOperationException(pkg.MissingReason ?? "No published driver repo for this device.");
        if (DriverPackageCatalog.IsForbiddenSource(pkg))
            throw new InvalidOperationException("Refusing LesleyMurfin / magic-tray package source.");
        if (DriverPackageCatalog.IsLocalVendorPath(pkg.PathInRepo))
            throw new InvalidOperationException("Refusing to install from magic-tray/driver/.");

        Logger.Log($"DRIVER_PULL repo={pkg.Owner}/{pkg.Repo} ref={pkg.GitRef} kind={pkg.Kind} path={pkg.PathInRepo} pid={devicePid}");

        return pkg.Kind switch
        {
            InstallKind.InfPnputil => await PullInfAndPnputilAsync(pkg, devicePid, ct),
            InstallKind.OfficialSysBind => await PullSysAndBindAsync(pkg, devicePid, ct),
            InstallKind.BrigadierKeyboard => await PullBrigadierKeyboardAsync(pkg, devicePid, ct),
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

    static async Task<string> PullSysAndBindAsync(DriverPackage pkg, string devicePid, CancellationToken ct)
    {
        var extractDir = await DownloadZipAsync(pkg, ct);
        var packageDir = FindPackageDir(extractDir, pkg.PathInRepo)
            ?? throw new InvalidOperationException($"Pulled {pkg.Owner}/{pkg.Repo} but '{pkg.PathInRepo}' was not in the zip.");

        var sys = Directory.EnumerateFiles(packageDir, "applewirelessmouse.sys", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException($"No applewirelessmouse.sys in {pkg.Owner}/{pkg.Repo}/{pkg.PathInRepo}.");

        Logger.Log($"DRIVER_BIND sys={sys} pid={devicePid}");
        RunElevatedBind(sys, devicePid);
        return sys;
    }

    static async Task<string> PullBrigadierKeyboardAsync(DriverPackage pkg, string devicePid, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheRoot);
        var exePath = Path.Combine(CacheRoot, "brigadier.exe");
        if (!File.Exists(exePath))
        {
            Logger.Log($"DRIVER_PULL brigadier url={BrigadierExeUrl}");
            await using var net = await Http.GetStreamAsync(BrigadierExeUrl, ct);
            await using var file = File.Create(exePath);
            await net.CopyToAsync(file, ct);
        }

        var outDir = Path.Combine(CacheRoot, "bootcamp-esd");
        Directory.CreateDirectory(outDir);
        Logger.Log($"DRIVER_BRIGADIER model={DriverPackageCatalog.BrigadierModel} out={outDir}");
        RunElevated(exePath, $"-m {DriverPackageCatalog.BrigadierModel} -o \"{outDir}\"", TimeSpan.FromMinutes(30));

        var inf = Directory.EnumerateFiles(outDir, "Keymagic2.inf", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Brigadier finished but AppleKeyboardMagic2/Keymagic2.inf was not in the extract.");

        Logger.Log($"DRIVER_INSTALL inf={inf} pid={devicePid}");
        RunElevated("pnputil.exe", $"/add-driver \"{inf}\" /install");

        var keymagic64 = Directory.EnumerateFiles(outDir, "Keymagic64.inf", SearchOption.AllDirectories).FirstOrDefault();
        if (keymagic64 is not null)
        {
            Logger.Log($"DRIVER_INSTALL inf={keymagic64} pid={devicePid}");
            RunElevated("pnputil.exe", $"/add-driver \"{keymagic64}\" /install");
        }

        return inf;
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

    static void RunElevatedBind(string sysPath, string pid)
    {
        var script = Path.Combine(CacheRoot, "bind-applewirelessmouse.ps1");
        Directory.CreateDirectory(CacheRoot);
        File.WriteAllText(script, BindScript);
        RunElevated("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -SysPath \"{sysPath}\" -DevicePid \"{pid}\"");
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

    // Binds WHQL applewirelessmouse.sys to the selected PID only. Replaces LowerFilters
    // (does not append MagicMouseDriver). User-selected — not a silent rebind.
    const string BindScript = """
        param([Parameter(Mandatory=$true)][string]$SysPath, [Parameter(Mandatory=$true)][string]$DevicePid)
        $ErrorActionPreference = 'Stop'
        $pidToken = "PID&$DevicePid"
        $dev = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
            $_.InstanceId -match 'BTHENUM' -and $_.InstanceId -match [regex]::Escape($pidToken) -and $_.Class -eq 'HIDClass'
        } | Select-Object -First 1
        if (-not $dev) { Write-Error "No paired HID device for PID $DevicePid — will not install."; exit 2 }
        $dest = "$env:SystemRoot\System32\drivers\applewirelessmouse.sys"
        Copy-Item -LiteralPath $SysPath -Destination $dest -Force
        $svc = Get-Service -Name applewirelessmouse -ErrorAction SilentlyContinue
        if (-not $svc) {
            & sc.exe create applewirelessmouse type= kernel start= demand error= ignore binPath= System32\drivers\applewirelessmouse.sys DisplayName= "Apple Wireless Mouse"
            if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed ($LASTEXITCODE)" }
        }
        $reg = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($dev.InstanceId)"
        if (-not (Test-Path $reg)) { throw "Device registry path missing: $reg" }
        Set-ItemProperty -Path $reg -Name LowerFilters -Value @('applewirelessmouse') -Type MultiString
        try {
            Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction Stop
            Start-Sleep -Seconds 2
            Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction Stop
        } catch {
            Write-Host "Device restart failed; reboot to finish bind."
        }
        """;
}
