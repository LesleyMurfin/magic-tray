// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO;

namespace MagicMouseTray;

// Two auto-fixes for "the wheel is dead", both elevated and both aimed at the
// live BTHENUM stack only.
//
// 1. BuildScript / Apply - the bound lower filter is not running, so the HID
//    stack loaded without scroll. Exactly one stack action:
//      pnputil /restart-device <BTHENUM instance>   for every live instance
//
// 2. BuildRemoveStaleFiltersScript / ApplyRemoveStaleFilters - TWO rival
//    versions of the same vendor filter are registered on one stack (a stale
//    name left on the device key next to the running one on the instance key).
//    A restart alone cannot fix that, because the stale name stays registered:
//    the stale name is removed from the LowerFilters value that carries it -
//    previous value logged verbatim first - and only then is the stack
//    restarted. It refuses outright if the working filter would not survive.
//
// There is deliberately no "sc start <filter>" here. A PnP device lower filter
// is loaded by PnP when the device stack is built, never by the SCM, so
// sc start on it legitimately fails with WIN32_EXIT_CODE 31 and proves nothing
// about signing or blocking. Restarting the device is what makes PnP load the
// bound filter; the service state is then only read back as verification.
//
// Deliberately NOT done here: no unpair, no /enable-device or /disable-device,
// no Bluetooth radio work, no driver installers, and nothing is ever aimed at a
// USB\ or HID\VID_ node - those are charge-cable phantoms (CM_PROB_PHANTOM),
// not the live Bluetooth stack. Only BTHENUM is enumerated.
//
// Same elevation shape as DeviceEnable: generated .ps1 in %TEMP%, ShellExecute
// with Verb=runas, and a status sidecar file because ExitCode is unavailable
// once UseShellExecute is true.
internal enum RepairOutcome
{
    Ok,
    NoInstances,
    FilterBlocked,
    Failed,
}

internal static class DeviceRepair
{
    internal const int NoInstancesExitCode = 2;
    internal const int FilterBlockedExitCode = 3;

    // Named after the ATTEMPT, not just the device. Both repairs can be in
    // flight for the same PID at once: TrayApp's StartRepairApply and
    // StartStaleFilterRemoval each launch straight into Task.Run, and
    // RunDriverActionAsync does not serialize driver actions - so a second
    // Apply (or a second ApplyRemoveStaleFilters) can overlap the first.
    // Under a PID-only name the poller in LaunchElevated cannot tell whose
    // report it read - it accepts the first recognised token at that path - so
    // it would map another elevated process's result onto this attempt, and
    // the File.Delete in LaunchElevated cannot close that hole because the
    // other attempt is free to write the path after the delete succeeds. Same
    // collision and the same fix as ModeFlip's cycle nonce (ModeFlip.cs:225-237).
    internal static string StatusSidecarPath(string pid, long nonce) =>
        Path.Combine(Path.GetTempPath(), $"mm-repair-{pid.ToLowerInvariant()}-{ValidateNonce(nonce)}.status");

    internal static string FiltersStatusSidecarPath(string pid, long nonce) =>
        Path.Combine(Path.GetTempPath(), $"mm-repair-filters-{pid.ToLowerInvariant()}-{ValidateNonce(nonce)}.status");

    // The bound filter name is whatever LowerFilters actually holds on the
    // device - e.g. "MagicMouseDriver204Scroll", a KMDF-family variant of the
    // catalog constant. Accept the KMDF and Apple families by case-insensitive
    // prefix, restrict the characters to what a service key can legally hold,
    // and reject everything else so no unreviewed name reaches an elevated
    // shell or breaks out of the single-quoted PowerShell literal.
    static string ValidateFilterServiceName(string filterServiceName)
    {
        if (string.IsNullOrEmpty(filterServiceName))
            throw new InvalidOperationException("DeviceRepair needs a filter service name.");
        var known =
            filterServiceName.StartsWith(DriverPackageCatalog.PatchedKmdfServiceName, StringComparison.OrdinalIgnoreCase)
            || filterServiceName.StartsWith(DriverPackageCatalog.AppleFilterServiceName, StringComparison.OrdinalIgnoreCase);
        if (!known)
            throw new InvalidOperationException($"DeviceRepair refuses service '{filterServiceName}'.");
        foreach (var c in filterServiceName)
        {
            var ok = c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or '-' or '.';
            if (!ok)
                throw new InvalidOperationException($"DeviceRepair refuses service '{filterServiceName}'.");
        }
        return filterServiceName;
    }

    static string ValidatePid(string pid)
    {
        if (string.IsNullOrEmpty(pid) || pid.Length != 4)
            throw new InvalidOperationException("DeviceRepair needs a 4-hex PID.");
        foreach (var c in pid)
        {
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
                throw new InvalidOperationException($"DeviceRepair needs a 4-hex PID, got '{pid}'.");
        }
        return pid.ToLowerInvariant();
    }

    // The nonce crosses the elevation boundary as part of a file name on both
    // sides, so it is rendered as plain digits and nothing else.
    static string ValidateNonce(long nonce)
    {
        if (nonce <= 0)
            throw new InvalidOperationException($"DeviceRepair needs a positive attempt nonce, got {nonce}.");
        return nonce.ToString();
    }

    // The names to unregister. Every one has to pass the same family + charset
    // gate as the filter that stays, must not be the filter that stays, and
    // must not repeat - a duplicate would mean the caller built the list from
    // something other than the live candidate set.
    static string[] ValidateRemoveList(string keepService, string[]? removeServices)
    {
        if (removeServices is null || removeServices.Length == 0)
            throw new InvalidOperationException("DeviceRepair needs at least one filter name to remove.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(removeServices.Length);
        foreach (var raw in removeServices)
        {
            var name = ValidateFilterServiceName(raw);
            if (name.Equals(keepService, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"DeviceRepair refuses to remove the filter it must keep '{name}'.");
            if (!seen.Add(name))
                throw new InvalidOperationException($"DeviceRepair refuses duplicate remove entry '{name}'.");
            names.Add(name);
        }
        return [.. names];
    }

    static string PowerShellArrayLiteral(IEnumerable<string> values) =>
        string.Join(", ", values.Select(v => "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'"));

    internal static string BuildScript(string pid, long nonce, string filterServiceName)
    {
        pid = ValidatePid(pid);
        var svc = ValidateFilterServiceName(filterServiceName);
        var needles = DeviceEnable.VidNeedlesForPid(pid);
        if (needles.Length == 0)
            throw new InvalidOperationException($"No catalog VID for pid={pid}.");
        var vidLiteral = string.Join(", ", needles.Select(v =>
            "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'"));
        // "$id" is required: BTHENUM instance IDs contain '&'. Unquoted,
        // PowerShell treats '&' as the call operator and pnputil never runs.
        var template = """
$ErrorActionPreference = 'Continue'
$targetPid = '__PID__'
$nonce = '__NONCE__'
$svc = '__SVC__'
$pidA = 'PID_' + $targetPid
$pidB = 'PID&' + $targetPid
$vidNeedles = @(__VIDS__)
$ids = New-Object System.Collections.Generic.List[string]
$statusFile = Join-Path $env:TEMP ('mm-repair-' + $targetPid + '-' + $nonce + '.status')
'running' | Set-Content -LiteralPath $statusFile -Encoding ASCII

function Test-Vid([string]$n) {
    $low = $n.ToLowerInvariant()
    foreach ($v in $vidNeedles) {
        if ($low.Contains($v.ToLowerInvariant())) { return $true }
    }
    return $false
}

function Test-SkipPath([string]$full) {
    $low = $full.ToLowerInvariant()
    if ($low.StartsWith('usb\')) { return $true }
    if ($low.StartsWith('hid\vid_')) { return $true }
    return $false
}

$root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Enum\BTHENUM')
if ($root) {
    foreach ($sub in $root.GetSubKeyNames()) {
        $low = $sub.ToLowerInvariant()
        if (-not ($low.Contains($pidA.ToLowerInvariant()) -or $low.Contains($pidB.ToLowerInvariant()))) { continue }
        if (-not (Test-Vid $sub)) { continue }
        $dev = $root.OpenSubKey($sub)
        if (-not $dev) { continue }
        foreach ($inst in $dev.GetSubKeyNames()) {
            $full = 'BTHENUM\' + $sub + '\' + $inst
            if (Test-SkipPath $full) { continue }
            [void]$ids.Add($full)
        }
        $dev.Dispose()
    }
    $root.Dispose()
}

if ($ids.Count -eq 0) {
    Write-Host "DEVICE_REPAIR no BTHENUM instances pid=$targetPid"
    'no-instances' | Set-Content -LiteralPath $statusFile -Encoding ASCII
    exit 2
}

# The only stack action. PnP rebuilds the device stack and loads the bound
# lower filter; nothing here starts a service, because a device lower filter
# is not startable through the SCM (sc start returns WIN32_EXIT_CODE 31).
$failed = 0
foreach ($id in $ids) {
    Write-Host "DEVICE_REPAIR restart $id"
    & pnputil.exe /restart-device "$id"
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
if ($failed -ne 0) {
    Write-Host "DEVICE_REPAIR restart failures=$failed"
    'failed' | Set-Content -LiteralPath $statusFile -Encoding ASCII
    exit 1
}

# PnP loads the filter asynchronously, so poll instead of reading once.
$deadline = (Get-Date).AddSeconds(10)
$queryOut = ''
$running = $false
while ($true) {
    $queryOut = (& sc.exe query "$svc" 2>&1 | Out-String)
    if ($queryOut -match 'RUNNING') { $running = $true; break }
    if ((Get-Date) -ge $deadline) { break }
    Start-Sleep -Milliseconds 500
}
Write-Host "DEVICE_REPAIR sc query svc=$svc running=$running"
Write-Host $queryOut
if ($running) {
    'ok' | Set-Content -LiteralPath $statusFile -Encoding ASCII
    exit 0
}

# Still not running. Only now is signing policy relevant: a self-signed KMDF
# filter cannot load without test signing, and HVCI blocks it regardless.
$startOptions = ''
try {
    $startOptions = [string](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name SystemStartOptions -ErrorAction Stop).SystemStartOptions
} catch {
    $startOptions = ''
}
$hvci = 'unavailable'
$hvciOn = $false
try {
    $dg = Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction Stop
    $svcRunning = @($dg.SecurityServicesRunning)
    $hvci = ($svcRunning -join ',')
    if ($svcRunning -contains 2) { $hvciOn = $true }
} catch {
    $hvci = 'unavailable'
}
$testSigning = $startOptions.ToUpperInvariant().Contains('TESTSIGNING')
Write-Host "DEVICE_REPAIR startoptions=$startOptions"
Write-Host "DEVICE_REPAIR testsigning=$testSigning hvci=$hvci hvcion=$hvciOn"
if ((-not $testSigning) -or $hvciOn) {
    Write-Host "DEVICE_REPAIR filter blocked svc=$svc"
    'filter-blocked' | Set-Content -LiteralPath $statusFile -Encoding ASCII
    exit 3
}
Write-Host "DEVICE_REPAIR filter not running svc=$svc with testsigning on and hvci off"
'failed' | Set-Content -LiteralPath $statusFile -Encoding ASCII
exit 1
""";
        var script = template
            .Replace("__PID__", pid, StringComparison.Ordinal)
            .Replace("__NONCE__", ValidateNonce(nonce), StringComparison.Ordinal)
            .Replace("__SVC__", svc, StringComparison.Ordinal)
            .Replace("__VIDS__", vidLiteral, StringComparison.Ordinal);
        foreach (var name in DeviceEnable.ForbiddenNames)
        {
            if (script.Contains(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"DeviceRepair refuses {name}.");
        }
        return script;
    }

    // Second repair shape: TWO rival versions of the same vendor lower filter
    // are registered on one BTHENUM stack - e.g. a stale "MagicMouseDriver"
    // left on the DEVICE key by an older install next to the running
    // "MagicMouseDriver204Scroll" on the INSTANCE key. Windows applies both
    // values to the same stack, so PnP is told to load a filter whose service
    // is Stopped. Restarting alone cannot fix that: the stale name stays
    // registered. The only repair is to unregister the stale name and rebuild
    // the stack.
    //
    // Bounds of the generated script, all enforced in the script itself and
    // not just here: it touches nothing but LowerFilters values under
    // Enum\BTHENUM for this PID, drops only the names passed in (never mouhid,
    // never HidBth, never an unlisted vendor filter), refuses when keepService
    // would not survive anywhere on the stack, never writes an empty
    // MultiString, and prints every previous value verbatim before writing so
    // the edit is reversible by hand from the transcript. No unpair, no radio,
    // no FLIP, no /enable-device or /disable-device, no service key, no .sys.
    internal static string BuildRemoveStaleFiltersScript(
        string pid, long nonce, string keepService, string[] removeServices)
    {
        pid = ValidatePid(pid);
        var keep = ValidateFilterServiceName(keepService);
        var remove = ValidateRemoveList(keep, removeServices);
        var needles = DeviceEnable.VidNeedlesForPid(pid);
        if (needles.Length == 0)
            throw new InvalidOperationException($"No catalog VID for pid={pid}.");
        var vidLiteral = PowerShellArrayLiteral(needles);
        var removeLiteral = PowerShellArrayLiteral(remove);
        // "$id" is required: BTHENUM instance IDs contain '&'. Unquoted,
        // PowerShell treats '&' as the call operator and pnputil never runs.
        var template = """
$ErrorActionPreference = 'Continue'
$targetPid = '__PID__'
$nonce = '__NONCE__'
$keep = '__KEEP__'
$remove = @(__REMOVE__)
$pidA = 'PID_' + $targetPid
$pidB = 'PID&' + $targetPid
$vidNeedles = @(__VIDS__)
$enumPath = 'SYSTEM\CurrentControlSet\Enum\BTHENUM'
$statusFile = Join-Path $env:TEMP ('mm-repair-filters-' + $targetPid + '-' + $nonce + '.status')
'running' | Set-Content -LiteralPath $statusFile -Encoding ASCII

function Test-Vid([string]$n) {
    $low = $n.ToLowerInvariant()
    foreach ($v in $vidNeedles) {
        if ($low.Contains($v.ToLowerInvariant())) { return $true }
    }
    return $false
}

function Test-SkipPath([string]$full) {
    $low = $full.ToLowerInvariant()
    if ($low.StartsWith('usb\')) { return $true }
    if ($low.StartsWith('hid\vid_')) { return $true }
    return $false
}

# Only the names this repair was handed are ever dropped. Everything else on
# LowerFilters - mouhid, HidBth, a second vendor filter that is running - is
# copied through untouched.
function Test-Removable([string]$n) {
    if ([string]::IsNullOrEmpty($n)) { return $false }
    foreach ($r in $remove) {
        if ($n.Equals($r, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Test-HasKeep($names) {
    foreach ($n in $names) {
        if ((-not [string]::IsNullOrEmpty($n)) -and $n.Equals($keep, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

$targets = New-Object System.Collections.Generic.List[object]
$ids = New-Object System.Collections.Generic.List[string]

$root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($enumPath)
if ($root) {
    foreach ($sub in $root.GetSubKeyNames()) {
        $low = $sub.ToLowerInvariant()
        if (-not ($low.Contains($pidA.ToLowerInvariant()) -or $low.Contains($pidB.ToLowerInvariant()))) { continue }
        if (-not (Test-Vid $sub)) { continue }
        if (Test-SkipPath ('BTHENUM\' + $sub)) { continue }
        $dev = $root.OpenSubKey($sub)
        if (-not $dev) { continue }
        $devVal = $dev.GetValue('LowerFilters')
        if ($null -ne $devVal) {
            [void]$targets.Add([pscustomobject]@{
                Path     = 'HKLM:\' + $enumPath + '\' + $sub
                Kind     = 'device'
                Previous = @($devVal)
                Filtered = @()
                Changed  = $false
            })
        }
        foreach ($inst in $dev.GetSubKeyNames()) {
            $full = 'BTHENUM\' + $sub + '\' + $inst
            if (Test-SkipPath $full) { continue }
            [void]$ids.Add($full)
            $instKey = $dev.OpenSubKey($inst)
            if ($instKey) {
                $instVal = $instKey.GetValue('LowerFilters')
                if ($null -ne $instVal) {
                    [void]$targets.Add([pscustomobject]@{
                        Path     = 'HKLM:\' + $enumPath + '\' + $sub + '\' + $inst
                        Kind     = 'instance'
                        Previous = @($instVal)
                        Filtered = @()
                        Changed  = $false
                    })
                }
                $instKey.Dispose()
            }
        }
        $dev.Dispose()
    }
    $root.Dispose()
}

if ($ids.Count -eq 0) {
    Write-Host "DEVICE_REPAIR_FILTERS no BTHENUM instances pid=$targetPid"
    'no-instances' | Set-Content -LiteralPath $statusFile -Encoding ASCII
    exit 2
}

# Every previous value is printed verbatim BEFORE any write, so this edit can
# be undone by hand from the transcript alone.
$changes = 0
foreach ($t in $targets) {
    Write-Host ("DEVICE_REPAIR_FILTERS key=" + $t.Path + " kind=" + $t.Kind)
    Write-Host ("DEVICE_REPAIR_FILTERS previous LowerFilters=[" + (@($t.Previous) -join '|') + "]")
    $kept = New-Object System.Collections.Generic.List[string]
    foreach ($n in @($t.Previous)) {
        if (Test-Removable $n) {
            Write-Host ("DEVICE_REPAIR_FILTERS drop=" + $n + " key=" + $t.Path)
            continue
        }
        [void]$kept.Add([string]$n)
    }
    $t.Filtered = @($kept)
    $t.Changed = ($t.Filtered.Count -ne @($t.Previous).Count)
    if ($t.Changed) { $changes++ }
    Write-Host ("DEVICE_REPAIR_FILTERS filtered LowerFilters=[" + ($t.Filtered -join '|') + "] changed=" + $t.Changed)
}

if ($changes -eq 0) {
    Write-Host "DEVICE_REPAIR_FILTERS nothing to remove pid=$targetPid keep=$keep"
    'nothing-to-do' | Set-Content -LiteralPath $statusFile -Encoding ASCII
    exit 0
}

# Refuse before writing anything rather than leave the stack with no working
# vendor filter.
$keepAnywhere = $false
foreach ($t in $targets) {
    if (Test-HasKeep $t.Filtered) { $keepAnywhere = $true }
}
if (-not $keepAnywhere) {
    Write-Host "DEVICE_REPAIR_FILTERS refuse: keep=$keep would not survive on this stack"
    'failed' | Set-Content -LiteralPath $statusFile -Encoding ASCII
    exit 1
}

$writeFailures = 0
foreach ($t in $targets) {
    if (-not $t.Changed) { continue }
    if ($t.Filtered.Count -eq 0) {
        # An empty MultiString is not a valid LowerFilters value, so the value
        # itself goes - but only while the keep filter still lives on the other
        # key of this stack (device vs instance).
        $keepElsewhere = $false
        foreach ($o in $targets) {
            if ($o.Path -eq $t.Path) { continue }
            if (Test-HasKeep $o.Filtered) { $keepElsewhere = $true }
        }
        if (-not $keepElsewhere) {
            Write-Host ("DEVICE_REPAIR_FILTERS refuse: emptying " + $t.Path + " would drop keep=" + $keep)
            'failed' | Set-Content -LiteralPath $statusFile -Encoding ASCII
            exit 1
        }
        Write-Host ("DEVICE_REPAIR_FILTERS delete value LowerFilters key=" + $t.Path)
        try {
            Remove-ItemProperty -LiteralPath $t.Path -Name 'LowerFilters' -Force -ErrorAction Stop
        } catch {
            Write-Host ("DEVICE_REPAIR_FILTERS write error key=" + $t.Path + " err=" + $_.Exception.Message)
            $writeFailures++
        }
        continue
    }
    Write-Host ("DEVICE_REPAIR_FILTERS write LowerFilters=[" + ($t.Filtered -join '|') + "] key=" + $t.Path)
    try {
        Set-ItemProperty -LiteralPath $t.Path -Name 'LowerFilters' -Value ([string[]]$t.Filtered) -Type MultiString -ErrorAction Stop
    } catch {
        Write-Host ("DEVICE_REPAIR_FILTERS write error key=" + $t.Path + " err=" + $_.Exception.Message)
        $writeFailures++
    }
}
if ($writeFailures -ne 0) {
    Write-Host "DEVICE_REPAIR_FILTERS registry write failures=$writeFailures"
    'failed' | Set-Content -LiteralPath $statusFile -Encoding ASCII
    exit 1
}

# The registry now names exactly one vendor filter, so rebuild the stack to
# make PnP act on it. Restart only - never enable/disable, never unpair.
$restartFailures = 0
foreach ($id in $ids) {
    Write-Host "DEVICE_REPAIR_FILTERS restart $id"
    & pnputil.exe /restart-device "$id"
    if ($LASTEXITCODE -ne 0) { $restartFailures++ }
}
if ($restartFailures -ne 0) {
    Write-Host "DEVICE_REPAIR_FILTERS restart failures=$restartFailures - registry edits above are already applied"
    'failed' | Set-Content -LiteralPath $statusFile -Encoding ASCII
    exit 1
}

# PnP loads the filter asynchronously, so poll instead of reading once.
$deadline = (Get-Date).AddSeconds(10)
$queryOut = ''
$running = $false
while ($true) {
    $queryOut = (& sc.exe query "$keep" 2>&1 | Out-String)
    if ($queryOut -match 'RUNNING') { $running = $true; break }
    if ((Get-Date) -ge $deadline) { break }
    Start-Sleep -Milliseconds 500
}
Write-Host "DEVICE_REPAIR_FILTERS sc query svc=$keep running=$running"
Write-Host $queryOut
if ($running) {
    'ok' | Set-Content -LiteralPath $statusFile -Encoding ASCII
    exit 0
}

# Still not running. Only now is signing policy relevant: a self-signed KMDF
# filter cannot load without test signing, and HVCI blocks it regardless.
$startOptions = ''
try {
    $startOptions = [string](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name SystemStartOptions -ErrorAction Stop).SystemStartOptions
} catch {
    $startOptions = ''
}
$hvci = 'unavailable'
$hvciOn = $false
try {
    $dg = Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction Stop
    $svcRunning = @($dg.SecurityServicesRunning)
    $hvci = ($svcRunning -join ',')
    if ($svcRunning -contains 2) { $hvciOn = $true }
} catch {
    $hvci = 'unavailable'
}
$testSigning = $startOptions.ToUpperInvariant().Contains('TESTSIGNING')
Write-Host "DEVICE_REPAIR_FILTERS startoptions=$startOptions"
Write-Host "DEVICE_REPAIR_FILTERS testsigning=$testSigning hvci=$hvci hvcion=$hvciOn"
if ((-not $testSigning) -or $hvciOn) {
    Write-Host "DEVICE_REPAIR_FILTERS filter blocked svc=$keep"
    'filter-blocked' | Set-Content -LiteralPath $statusFile -Encoding ASCII
    exit 3
}
Write-Host "DEVICE_REPAIR_FILTERS filter not running svc=$keep with testsigning on and hvci off"
'failed' | Set-Content -LiteralPath $statusFile -Encoding ASCII
exit 1
""";
        var script = template
            .Replace("__PID__", pid, StringComparison.Ordinal)
            .Replace("__NONCE__", ValidateNonce(nonce), StringComparison.Ordinal)
            .Replace("__KEEP__", keep, StringComparison.Ordinal)
            .Replace("__REMOVE__", removeLiteral, StringComparison.Ordinal)
            .Replace("__VIDS__", vidLiteral, StringComparison.Ordinal);
        foreach (var name in DeviceEnable.ForbiddenNames)
        {
            if (script.Contains(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"DeviceRepair refuses {name}.");
        }
        return script;
    }

    // Shared elevation shape for both repairs: write the script, ShellExecute
    // it with Verb=runas, and poll the status sidecar because ExitCode is
    // unavailable once UseShellExecute is true. Started=false means
    // ShellExecute handed back no process at all - UAC refused outright.
    static (bool Started, string Sidecar, int? Exit) LaunchElevated(
        string scriptPath,
        string script,
        string statusPath)
    {
        try { File.Delete(statusPath); } catch { /* ignore */ }
        File.WriteAllText(scriptPath, script);
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Path.GetTempPath(),
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return (false, "", null); }
        if (p is null)
            return (false, "", null);
        string sidecar = "";
        int? exit = null;
        using (p)
        {
            var started = DateTime.UtcNow;
            var until = started + TimeSpan.FromMinutes(2);
            while (DateTime.UtcNow < until)
            {
                if (File.Exists(statusPath))
                {
                    try { sidecar = File.ReadAllText(statusPath).Trim(); }
                    catch { sidecar = ""; }
                    if (sidecar is "ok" or "failed" or "no-instances" or "filter-blocked" or "nothing-to-do")
                        break;
                }
                try
                {
                    // Cancelled UAC prompt: the launcher is gone and nothing was written.
                    if (p.HasExited
                        && sidecar.Length == 0
                        && DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
                        break;
                }
                catch { /* UseShellExecute */ }
                Thread.Sleep(150);
            }
            if (!p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            try { if (p.HasExited) exit = p.ExitCode; } catch { /* UseShellExecute */ }
        }
        return (true, sidecar, exit);
    }

    static RepairOutcome MapOutcome(string sidecar, int? exit) => sidecar switch
    {
        // "nothing-to-do": the stale names were already gone, so the stack is
        // in the state the repair wanted. Same answer as a completed repair.
        "ok" or "nothing-to-do" => RepairOutcome.Ok,
        "no-instances" => RepairOutcome.NoInstances,
        "filter-blocked" => RepairOutcome.FilterBlocked,
        _ when exit == NoInstancesExitCode => RepairOutcome.NoInstances,
        _ when exit == FilterBlockedExitCode => RepairOutcome.FilterBlocked,
        // "failed", an unrecognised sidecar, and an empty sidecar (UAC
        // cancelled or the script never ran) are all the same to the caller.
        _ => RepairOutcome.Failed,
    };

    // Never throws for NoInstances / FilterBlocked - those are returned so the
    // caller can explain them. Throws only when the request itself is invalid
    // (bad PID, unknown service) or the temp script cannot be written.
    internal static RepairOutcome Apply(string pid, string filterServiceName)
    {
        pid = ValidatePid(pid);
        var svc = ValidateFilterServiceName(filterServiceName);
        // One nonce per attempt, minted here because Apply IS the attempt: it
        // names this attempt's script and its status sidecar on both sides of
        // the elevation boundary - see StatusSidecarPath for what a PID-only
        // name lets the poller believe. Logged so two overlapping attempts can
        // be told apart in a support log.
        var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var script = BuildScript(pid, nonce, svc);
        var temp = Path.Combine(Path.GetTempPath(), $"mm-repair-{pid}-{ValidateNonce(nonce)}.ps1");
        Logger.Log($"DEVICE_REPAIR pid={pid} nonce={nonce} svc={svc}");
        var run = LaunchElevated(temp, script, StatusSidecarPath(pid, nonce));
        if (!run.Started)
        {
            Logger.Log($"DEVICE_REPAIR pid={pid} outcome=failed reason=no-process");
            return RepairOutcome.Failed;
        }
        var outcome = MapOutcome(run.Sidecar, run.Exit);
        Logger.Log($"DEVICE_REPAIR pid={pid} svc={svc} exit={run.Exit?.ToString() ?? "n/a"} sidecar={run.Sidecar} outcome={outcome}");
        return outcome;
    }

    // Same never-throws-for-NoInstances/FilterBlocked contract as Apply.
    // Throws only when the request itself is invalid (bad PID, unknown or
    // contradictory service names) or the temp script cannot be written.
    internal static RepairOutcome ApplyRemoveStaleFilters(string pid, string keepService, string[] removeServices)
    {
        pid = ValidatePid(pid);
        var keep = ValidateFilterServiceName(keepService);
        var remove = ValidateRemoveList(keep, removeServices);
        // Its own attempt, so its own nonce - this repair can be running while
        // an Apply for the same PID is, and neither may poll the other's file.
        var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var script = BuildRemoveStaleFiltersScript(pid, nonce, keep, remove);
        var temp = Path.Combine(Path.GetTempPath(), $"mm-repair-filters-{pid}-{ValidateNonce(nonce)}.ps1");
        var removed = string.Join(",", remove);
        Logger.Log($"DEVICE_REPAIR_FILTERS pid={pid} nonce={nonce} keep={keep} remove={removed}");
        var run = LaunchElevated(temp, script, FiltersStatusSidecarPath(pid, nonce));
        if (!run.Started)
        {
            Logger.Log($"DEVICE_REPAIR_FILTERS pid={pid} keep={keep} remove={removed} outcome=failed reason=no-process");
            return RepairOutcome.Failed;
        }
        var outcome = MapOutcome(run.Sidecar, run.Exit);
        Logger.Log($"DEVICE_REPAIR_FILTERS pid={pid} keep={keep} remove={removed} exit={run.Exit?.ToString() ?? "n/a"} sidecar={run.Sidecar} outcome={outcome}");
        return outcome;
    }
}
