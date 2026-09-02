// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO;

namespace MagicMouseTray;

// Enabled on this PC V2: stop/start the existing Windows device for this PID.
// No new driver. No unpair. No KMDF / PathA / Stock scripts.
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

    internal static bool MatchesInstance(string instanceId, string pid)
    {
        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(pid))
            return false;
        pid = pid.ToLowerInvariant();
        if (pid.Length != 4)
            return false;
        var low = instanceId.ToLowerInvariant();
        if (low.Contains("pid_" + pid, StringComparison.Ordinal) ||
            low.Contains("pid&" + pid, StringComparison.Ordinal))
        {
            if (low.Contains("bthenum", StringComparison.Ordinal) &&
                !low.Contains("_vid&000205ac_", StringComparison.Ordinal) &&
                !low.Contains("_vid&0001004c_", StringComparison.Ordinal) &&
                !low.Contains("vid_05ac", StringComparison.Ordinal))
                return false;
            return true;
        }
        return false;
    }

    internal static string BuildScript(string pid, bool enable)
    {
        pid = pid.ToLowerInvariant();
        var verb = enable ? "enable-device" : "disable-device";
        var template = """
$ErrorActionPreference = 'Continue'
$targetPid = '__PID__'
$verb = '__VERB__'
$pidA = 'PID_' + $targetPid
$pidB = 'PID&' + $targetPid
$vidNeedles = @('_VID&000205ac_', '_VID&0001004c_', 'VID_05AC')
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

Add-Matching 'BTHENUM'
Add-Matching 'HID'
Add-Matching 'USB'
foreach ($id in $ids) {
    & pnputil.exe /__VERB__ $id | Out-Host
}
""";
        var script = template
            .Replace("__PID__", pid, StringComparison.Ordinal)
            .Replace("__VERB__", verb, StringComparison.Ordinal);
        foreach (var name in ForbiddenNames)
        {
            if (script.Contains(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"DeviceEnable refuses {name}.");
        }
        return script;
    }

    internal static void Apply(string pid, bool enable)
    {
        if (string.IsNullOrEmpty(pid) || pid.Length > 4)
            throw new InvalidOperationException("DeviceEnable needs a 4-hex PID.");
        pid = pid.ToLowerInvariant();
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
