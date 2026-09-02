// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO;

namespace MagicMouseTray;

// Enabled on this PC: stop/start the Windows device for this catalog PID
// so a Mac can take the Bluetooth link. No unpair. No driver installers.
// VID needles come from KnownMice / KnownKeyboards — adding a device to
// those tables is what makes enable/disable work for it.
internal static class DeviceEnable
{
    internal static readonly string[] ForbiddenNames =
    [
        "Install-KMDF",
        "Uninstall-KMDF",
        "Install-MagicMousePatch",
        "Uninstall-MagicMousePatch",
        "FLIP:NoFilter",
    ];

    internal static string[] VidNeedlesForPid(string pid)
    {
        if (string.IsNullOrEmpty(pid) || pid.Length != 4)
            return [];
        pid = pid.ToLowerInvariant();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in MouseBatteryDevice.KnownMice)
            if (m.PidPattern.EndsWith(pid, StringComparison.OrdinalIgnoreCase))
                set.Add(m.VidPattern);
        foreach (var k in KeyboardBatteryDevice.KnownKeyboards)
            if (k.PidPattern.EndsWith(pid, StringComparison.OrdinalIgnoreCase))
                set.Add(k.VidPattern);
        return [.. set];
    }

    internal static bool MatchesInstance(string instanceId, string pid)
    {
        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(pid))
            return false;
        pid = pid.ToLowerInvariant();
        if (pid.Length != 4)
            return false;
        var needles = VidNeedlesForPid(pid);
        if (needles.Length == 0)
            return false;
        var low = instanceId.ToLowerInvariant();
        if (!low.Contains("pid_" + pid, StringComparison.Ordinal) &&
            !low.Contains("pid&" + pid, StringComparison.Ordinal))
            return false;
        foreach (var vid in needles)
        {
            if (!string.IsNullOrEmpty(vid) &&
                low.Contains(vid.ToLowerInvariant(), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static string BuildScript(string pid, bool enable)
    {
        pid = pid.ToLowerInvariant();
        var needles = VidNeedlesForPid(pid);
        if (needles.Length == 0)
            throw new InvalidOperationException($"No catalog VID for pid={pid}.");
        var verb = enable ? "enable-device" : "disable-device";
        var vidLiteral = string.Join(", ", needles.Select(v =>
            "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'"));
        // "$id" is required: HID/BTHENUM instance IDs contain '&'. Unquoted,
        // PowerShell treats '&' as the call operator and pnputil never runs.
        var template = """
$ErrorActionPreference = 'Continue'
$targetPid = '__PID__'
$verb = '__VERB__'
$pidA = 'PID_' + $targetPid
$pidB = 'PID&' + $targetPid
$vidNeedles = @(__VIDS__)
$ids = New-Object System.Collections.Generic.List[string]

function Test-Vid([string]$n) {
    $low = $n.ToLowerInvariant()
    foreach ($v in $vidNeedles) {
        if ($low.Contains($v.ToLowerInvariant())) { return $true }
    }
    return $false
}

function Add-Matching([string]$enumerator) {
    $root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Enum\' + $enumerator)
    if (-not $root) { return }
    foreach ($sub in $root.GetSubKeyNames()) {
        $low = $sub.ToLowerInvariant()
        if (-not ($low.Contains($pidA.ToLowerInvariant()) -or $low.Contains($pidB.ToLowerInvariant()))) { continue }
        if (-not (Test-Vid $sub)) { continue }
        $dev = $root.OpenSubKey($sub)
        if (-not $dev) { continue }
        foreach ($inst in $dev.GetSubKeyNames()) {
            [void]$ids.Add($enumerator + '\' + $sub + '\' + $inst)
        }
        $dev.Dispose()
    }
    $root.Dispose()
}

$enumRoot = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Enum')
if (-not $enumRoot) {
    Write-Host 'DEVICE_ENABLE no Enum'
    exit 1
}
foreach ($enumerator in $enumRoot.GetSubKeyNames()) {
    Add-Matching $enumerator
}
$enumRoot.Dispose()

$rank = { if ($_.StartsWith('BTHENUM\', [StringComparison]::OrdinalIgnoreCase)) { 1 } else { 0 } }
if ($verb -eq 'disable-device') {
    $ids = [System.Collections.Generic.List[string]]($ids | Sort-Object $rank)
} else {
    $ids = [System.Collections.Generic.List[string]]($ids | Sort-Object $rank -Descending)
}

if ($ids.Count -eq 0) {
    Write-Host "DEVICE_ENABLE no instances pid=$targetPid"
    exit 1
}

$failed = 0
foreach ($id in $ids) {
    Write-Host "DEVICE_ENABLE $verb $id"
    & pnputil.exe "/$verb" "$id"
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
if ($failed -ne 0) { exit 1 }
exit 0
""";
        var script = template
            .Replace("__PID__", pid, StringComparison.Ordinal)
            .Replace("__VERB__", verb, StringComparison.Ordinal)
            .Replace("__VIDS__", vidLiteral, StringComparison.Ordinal);
        foreach (var name in ForbiddenNames)
        {
            if (script.Contains(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"DeviceEnable refuses {name}.");
        }
        return script;
    }

    internal static void Apply(string pid, bool enable)
    {
        if (string.IsNullOrEmpty(pid) || pid.Length != 4)
            throw new InvalidOperationException("DeviceEnable needs a 4-hex PID.");
        pid = pid.ToLowerInvariant();
        if (VidNeedlesForPid(pid).Length == 0)
            throw new InvalidOperationException($"No catalog VID for pid={pid}.");
        var temp = Path.Combine(Path.GetTempPath(), $"mm-enable-{pid}.ps1");
        File.WriteAllText(temp, BuildScript(pid, enable));
        Logger.Log($"DEVICE_ENABLE pid={pid} val={enable.ToString().ToLowerInvariant()}");
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{temp}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Path.GetTempPath(),
        };
        using var p = Process.Start(psi);
        if (p is null)
            throw new InvalidOperationException("Could not start elevated process (UAC cancelled?).");
        if (!p.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new InvalidOperationException("pnputil enable/disable timed out.");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"pnputil exited {p.ExitCode}.");
    }
}
