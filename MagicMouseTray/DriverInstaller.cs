// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MagicMouseTray;

// What an Offer* entry point actually did. A caller may persist a driver
// choice only on Confirmed: the user agreed and the step was launched without
// an immediate error. Cancelled means the user declined and nothing ran.
// Failed means the offer was refused or errored. Unavailable means the package
// or device fact the offer needs is not on this branch / this machine, which
// is a repo or hardware state and not a user error.
internal enum InstallOutcome
{
    Confirmed,
    Cancelled,
    Failed,
    Unavailable,
}

// User-initiated driver offers. Never silent. KMDF never falls back to
// PATH-A (Install-MagicMousePatch.ps1). PathA is a dedicated user-initiated
// offer and never falls back to KMDF. Stock unbinds to HidBth; never FLIP:NoFilter.
internal static class DriverInstaller
{
    const string BtHidEnumBase = @"SYSTEM\CurrentControlSet\Enum\BTHENUM";
    const string HidUuidPrefix = "{00001124-0000-1000-8000-00805f9b34fb}";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static DriverInstaller()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("MagicTray/1.0");
    }

    internal static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MagicMouseTray", "driver-cache");

    // v1/v2: open the documented tealtadpole page. Do not pnputil / rebind.
    internal static string V1V2BootCampPageUrl => DriverPackageCatalog.TealtadpolePageUrl;

    internal static InstallOutcome OfferV1V2ScrollFix()
    {
        var url = V1V2BootCampPageUrl;
        try
        {
            Logger.Log($"DRIVER_OFFER v1v2 url={url}");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBlocked($"Could not open {url}. {ex.Message}");
            return InstallOutcome.Failed;
        }
        return InstallOutcome.Confirmed;
    }

    // v1 trackpad (030E only): confirm-first Boot Camp applewtp. Open the
    // documented GitHub folder. Do not pnputil / rebind. Cancel aborts.
    // Never 0265 / 0324. Never KMDF / PathA.
    internal static string TrackpadV1BootCampPageUrl =>
        DriverPackageCatalog.TealtadpoleTrackpadPageUrl;

    internal sealed record TrackpadV1BootCampPlan(
        string Pid,
        string Prompt,
        string Url);

    internal static readonly string[] TrackpadV1BootCampForbiddenNames =
    [
        DriverPackageCatalog.KmdfOneClickName,
        DriverPackageCatalog.KmdfUninstallCmdName,
        DriverPackageCatalog.PathAInstallScriptName,
        DriverPackageCatalog.PathAUninstallScriptName,
        "Install-KMDF",
        "Install-MagicMousePatch",
        "pnputil",
        "FLIP:NoFilter",
        "TrackpadPtp",
    ];

    internal static bool IsTrackpadV1BootCampPid(string? pid) =>
        !string.IsNullOrEmpty(pid)
        && pid.Equals("030e", StringComparison.OrdinalIgnoreCase);

    internal static TrackpadV1BootCampPlan PlanTrackpadV1BootCamp(string pid)
    {
        if (!IsTrackpadV1BootCampPid(pid))
        {
            throw new InvalidOperationException(
                "Trackpad Boot Camp is only for Magic Trackpad v1 (030E). " +
                "Will not offer 0265 or 0324. Will not run Install-KMDF.cmd, " +
                "Install-MagicMousePatch, pnputil, or TrackpadPtp.");
        }

        const string prompt =
            "This opens the Boot Camp Apple Wireless Trackpad page (applewtp) " +
            "for Magic Trackpad v1 (030E) only. Test Mode is not required. " +
            "Right-click AppleWirelessTrackpad.inf to install. " +
            "The tray does not run pnputil.\n\n" +
            "OK continues. Cancel aborts.";
        return new TrackpadV1BootCampPlan(
            pid.ToLowerInvariant(),
            prompt,
            TrackpadV1BootCampPageUrl);
    }

    internal static InstallOutcome OfferTrackpadV1BootCamp(string pid)
    {
        TrackpadV1BootCampPlan plan;
        try
        {
            plan = PlanTrackpadV1BootCamp(pid);
            foreach (var forbidden in TrackpadV1BootCampForbiddenNames)
            {
                if (plan.Url.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Trackpad Boot Camp refuses {forbidden}.");
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            ShowBlocked(ex.Message);
            return InstallOutcome.Failed;
        }

        var result = System.Windows.Forms.MessageBox.Show(
            plan.Prompt,
            "Magic Tray",
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Information);
        if (result != System.Windows.Forms.DialogResult.OK)
        {
            Logger.Log("DRIVER_OFFER trackpadv1 aborted cancelled");
            return InstallOutcome.Cancelled;
        }

        try
        {
            Logger.Log($"DRIVER_OFFER trackpadv1 url={plan.Url}");
            Process.Start(new ProcessStartInfo(plan.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBlocked($"Could not open {plan.Url}. {ex.Message}");
            return InstallOutcome.Failed;
        }
        return InstallOutcome.Confirmed;
    }

    // v1/v2: user-initiated Stock restore. Unbind applewirelessmouse on this
    // PID only. HidBth remains. Not Test Mode. Never 0323 uninstall scripts.
    // Never FLIP:NoFilter. Never silent / poll / startup.
    internal sealed record V1V2StockRestorePlan(
        string Pid,
        string Prompt,
        string UnbindScript,
        IReadOnlyList<string> ExternalScripts);

    internal static readonly string[] V1V2StockForbiddenNames =
    [
        DriverPackageCatalog.KmdfUninstallCmdName,
        DriverPackageCatalog.KmdfOneClickName,
        "Uninstall-MagicMousePatch",
        "Install-MagicMousePatch",
        "FLIP:NoFilter",
    ];

    internal static bool IsV1V2StockPid(string? pid)
    {
        if (string.IsNullOrEmpty(pid)) return false;
        pid = pid.ToLowerInvariant();
        return Array.Exists(DriverHealthChecker.AppleFilterPids, p => p == pid);
    }

    internal static V1V2StockRestorePlan PlanV1V2StockRestore(string pid)
    {
        if (!IsV1V2StockPid(pid))
        {
            throw new InvalidOperationException(
                "v1/v2 Stock restore is only for Magic Mouse v1/v2 (030d/0269/0310). " +
                "Will not run Install-KMDF.cmd, Uninstall-KMDF.cmd, " +
                "Install-MagicMousePatch, or Uninstall-MagicMousePatch. " +
                "Will not run FLIP:NoFilter.");
        }

        pid = pid.ToLowerInvariant();
        const string prompt =
            "This restores this Magic Mouse to stock Windows HID (HidBth). " +
            "No Apple filter. Test Mode is not required.\n\n" +
            "OK continues. Cancel aborts.";
        return new V1V2StockRestorePlan(
            pid,
            prompt,
            BuildV1V2StockUnbindScript(pid),
            ExternalScripts: []);
    }

    internal static InstallOutcome OfferV1V2StockRestore(string pid)
    {
        V1V2StockRestorePlan plan;
        try
        {
            plan = PlanV1V2StockRestore(pid);
        }
        catch (InvalidOperationException ex)
        {
            ShowBlocked(ex.Message);
            return InstallOutcome.Failed;
        }

        var result = System.Windows.Forms.MessageBox.Show(
            plan.Prompt,
            "Magic Tray",
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Warning);
        if (result != System.Windows.Forms.DialogResult.OK)
        {
            Logger.Log("DRIVER_V1V2_STOCK_ABORTED cancelled");
            return InstallOutcome.Cancelled;
        }

        try
        {
            ExecuteV1V2StockRestore(plan);
        }
        catch (Exception ex) when (IsUacDeclined(ex))
        {
            Logger.Log("DRIVER_V1V2_STOCK_ABORTED cancelled reason=uac-declined");
            return InstallOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            ShowBlocked(ex.Message);
            return InstallOutcome.Failed;
        }
        return InstallOutcome.Confirmed;
    }

    internal static void ExecuteV1V2StockRestore(V1V2StockRestorePlan plan)
    {
        if (plan.ExternalScripts.Count != 0)
        {
            throw new InvalidOperationException(
                "v1/v2 Stock restore must not run 0323 uninstall scripts.");
        }

        foreach (var forbidden in V1V2StockForbiddenNames)
        {
            if (plan.UnbindScript.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"v1/v2 Stock restore refuses {forbidden}.");
            }
        }

        var temp = Path.Combine(Path.GetTempPath(), $"mm-v1v2-stock-{plan.Pid}.ps1");
        File.WriteAllText(temp, plan.UnbindScript);
        Logger.Log($"DRIVER_V1V2_STOCK_UNBIND pid={plan.Pid}");
        RunElevated(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{temp}\"",
            workingDirectory: Path.GetTempPath(),
            windowStyle: ProcessWindowStyle.Normal,
            timeout: TimeSpan.FromMinutes(5));
    }

    static string BuildV1V2StockUnbindScript(string pid)
    {
        const string template = """
$ErrorActionPreference = 'Stop'
$targetPid = '__PID__'
$filter = 'applewirelessmouse'
$pidToken = '_PID&' + $targetPid
$vidNeedles = @('_VID&000205ac_', '_VID&0001004c_')

function Test-TargetVid([string]$n) {
    foreach ($v in $vidNeedles) {
        if ($n.ToLowerInvariant().Contains($v.ToLowerInvariant())) { return $true }
    }
    return $false
}

function Strip-Filter([Microsoft.Win32.RegistryKey]$key) {
    if ($null -eq $key) { return }
    $lf = $key.GetValue('LowerFilters')
    if ($null -eq $lf) { return }
    $arr = @($lf)
    $kept = New-Object System.Collections.Generic.List[string]
    foreach ($item in $arr) {
        if ($item -and ($item.ToString().ToLowerInvariant() -ne $filter)) {
            [void]$kept.Add($item.ToString())
        }
    }
    if ($kept.Count -eq $arr.Count) { return }
    if ($kept.Count -eq 0) { $key.DeleteValue('LowerFilters', $false) }
    else { $key.SetValue('LowerFilters', $kept.ToArray(), [Microsoft.Win32.RegistryValueKind]::MultiString) }
}

$restart = New-Object System.Collections.Generic.List[string]
$bth = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Enum\BTHENUM', $true)
if ($bth) {
    foreach ($sub in $bth.GetSubKeyNames()) {
        if (-not $sub.ToLowerInvariant().Contains($pidToken.ToLowerInvariant())) { continue }
        if (-not (Test-TargetVid $sub)) { continue }
        $dev = $bth.OpenSubKey($sub, $true)
        if (-not $dev) { continue }
        Strip-Filter $dev
        foreach ($inst in $dev.GetSubKeyNames()) {
            $ik = $dev.OpenSubKey($inst, $true)
            if (-not $ik) { continue }
            Strip-Filter $ik
            [void]$restart.Add(('BTHENUM\' + $sub + '\' + $inst))
            $ik.Dispose()
        }
        $dev.Dispose()
    }
    $bth.Dispose()
}

$hid = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Enum\HID', $true)
if ($hid) {
    $hidPidA = 'PID_' + $targetPid
    $hidPidB = 'PID&' + $targetPid
    foreach ($sub in $hid.GetSubKeyNames()) {
        $low = $sub.ToLowerInvariant()
        if (-not ($low.Contains($hidPidA.ToLowerInvariant()) -or $low.Contains($hidPidB.ToLowerInvariant()))) { continue }
        $dev = $hid.OpenSubKey($sub, $true)
        if (-not $dev) { continue }
        Strip-Filter $dev
        foreach ($inst in $dev.GetSubKeyNames()) {
            $ik = $dev.OpenSubKey($inst, $true)
            if (-not $ik) { continue }
            Strip-Filter $ik
            $ik.Dispose()
        }
        $dev.Dispose()
    }
    $hid.Dispose()
}

foreach ($id in $restart) {
    & pnputil.exe /restart-device $id | Out-Host
}
""";
        return template.Replace("__PID__", pid, StringComparison.Ordinal);
    }

    // 0323: snapshot default main of magic-mouse-v3-windows-fix and run
    // v2-kmdf-driver/Install-KMDF.cmd elevated. If the cmd is missing, stop
    // with an error — never fall back to PATH-A.
    internal static async Task<InstallOutcome> OfferV3KmdfInstallAsync(CancellationToken ct = default)
    {
        var prompt = System.Windows.Forms.MessageBox.Show(
            "The Magic Mouse 2024 (0323) KMDF driver is self-signed. Test Mode must be on and Memory Integrity (HVCI) must be off before Windows will load it.\n\nOK continues with Install-KMDF.cmd. Cancel aborts.",
            "Magic Tray",
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Warning);
        if (prompt != System.Windows.Forms.DialogResult.OK)
        {
            Logger.Log("DRIVER_KMDF_ABORTED cancelled");
            return InstallOutcome.Cancelled;
        }

        string extractDir;
        try
        {
            extractDir = await DownloadV3DefaultBranchAsync(ct);
        }
        catch (Exception ex)
        {
            ShowBlocked($"Could not pull {DriverPackageCatalog.V3RepoUrl}. {ex.Message}");
            return InstallOutcome.Failed;
        }

        var script = FindKmdfOneClick(extractDir, DriverPackageCatalog.KmdfInstallCmdRelativePath);
        if (script is null)
        {
            // The cmd not being on the branch is a repo state, not a user
            // error, and never a reason to fall back to PATH-A.
            ShowBlocked(
                $"Cloned {DriverPackageCatalog.V3RepoUrl} default branch " +
                $"'{DriverPackageCatalog.V3RepoRef}' but " +
                $"{DriverPackageCatalog.KmdfInstallCmdRelativePath} is not on this branch. " +
                "Will not run PATH-A Install-MagicMousePatch.ps1. " +
                "Will not patch applewirelessmouse.sys.");
            return InstallOutcome.Unavailable;
        }

        if (IsPathAInstaller(script) || !IsKmdfOneClick(script))
        {
            ShowBlocked("Refusing a script that is not v2-kmdf-driver/Install-KMDF.cmd.");
            return InstallOutcome.Failed;
        }

        Logger.Log($"DRIVER_KMDF_ONECLICK script={script}");
        try
        {
            RunElevated(
                "cmd.exe",
                $"/c \"{script}\"",
                workingDirectory: Path.GetDirectoryName(script),
                windowStyle: ProcessWindowStyle.Normal,
                timeout: TimeSpan.FromMinutes(15));
        }
        catch (Exception ex) when (IsUacDeclined(ex))
        {
            Logger.Log("DRIVER_KMDF_ABORTED cancelled reason=uac-declined");
            return InstallOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            ShowBlocked(ex.Message);
            return InstallOutcome.Failed;
        }
        return InstallOutcome.Confirmed;
    }

    // 0323: user-initiated Patched Apple offer. Same Test Mode / HVCI facts as
    // KMDF. Scroll and battery are mutually exclusive. Never KMDF fallback.
    // currentStatus is what the caller already read for this mouse. Null means
    // the caller did not say, and an unknown state is warned rather than waved
    // through: losing KMDF silently is the expensive outcome, a warning the
    // user did not need is cheap. The warning never blocks - it warns, then obeys.
    internal static async Task<InstallOutcome> OfferV3PathAInstallAsync(
        DriverStatus? currentStatus = null,
        CancellationToken ct = default)
    {
        var body =
            "The Magic Mouse 2024 (0323) patched Apple driver is not WHQL after patch. Test Mode must be on and Memory Integrity (HVCI) must be off before Windows will load it. Scroll and battery are mutually exclusive.";
        if (WarnsKmdfDisplacement(currentStatus))
            body += "\n\n" + KmdfDisplacementWarning();

        var prompt = System.Windows.Forms.MessageBox.Show(
            body + "\n\nOK continues. Cancel aborts.",
            "Magic Tray",
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Warning);
        if (prompt != System.Windows.Forms.DialogResult.OK)
        {
            Logger.Log("DRIVER_PATHA_ABORTED cancelled");
            return InstallOutcome.Cancelled;
        }

        string extractDir;
        try
        {
            extractDir = await DownloadV3DefaultBranchAsync(ct);
        }
        catch (Exception ex)
        {
            ShowBlocked($"Could not pull {DriverPackageCatalog.V3RepoUrl}. {ex.Message}");
            return InstallOutcome.Failed;
        }

        var script = FindPathAInstaller(extractDir, DriverPackageCatalog.PathAInstallScriptRelativePath);
        if (script is null)
        {
            ShowBlocked(
                $"Cloned {DriverPackageCatalog.V3RepoUrl} default branch " +
                $"'{DriverPackageCatalog.V3RepoRef}' but " +
                $"{DriverPackageCatalog.PathAInstallScriptRelativePath} is not on this branch. " +
                "Will not run v2-kmdf-driver/Install-KMDF.cmd. " +
                "Will not fall back to KMDF.");
            return InstallOutcome.Unavailable;
        }

        if (!IsPathAInstaller(script) || IsKmdfOneClick(script) || IsPathAUninstallScript(script))
        {
            ShowBlocked(
                "Refusing a script that is not v1-binary-patch/installer/Install-MagicMousePatch.ps1.");
            return InstallOutcome.Failed;
        }

        Logger.Log($"DRIVER_PATHA_ONECLICK script={script}");
        try
        {
            RunElevated(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                workingDirectory: Path.GetDirectoryName(script),
                windowStyle: ProcessWindowStyle.Normal,
                timeout: TimeSpan.FromMinutes(15));
        }
        catch (Exception ex) when (IsUacDeclined(ex))
        {
            Logger.Log("DRIVER_PATHA_ABORTED cancelled reason=uac-declined");
            return InstallOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            ShowBlocked(ex.Message);
            return InstallOutcome.Failed;
        }
        return InstallOutcome.Confirmed;
    }

    // Only a mouse that is on KMDF right now has KMDF to lose. A user already
    // on stock or on the patched Apple driver must not be told they are about
    // to lose something they do not have.
    internal static bool WarnsKmdfDisplacement(DriverStatus? currentStatus) =>
        currentStatus is null or DriverStatus.PatchedKmdf;

    // Why this route can cost a working KMDF mouse its driver: Windows ranks
    // driver packages by signature trust, Apple's INF is WHQL/catalog-signed
    // and now claims 0323 too, and the KMDF package is test-signed. Stated as
    // a real possibility with its mechanism, not as a certainty, and with the
    // way back.
    internal static string KmdfDisplacementWarning() =>
        "If this mouse is on the KMDF driver right now, this install can move it off. " +
        "Apple's own INF now also claims the Magic Mouse 2024 (0323), and Apple's package is WHQL-signed. " +
        "Windows ranks a WHQL-signed package above the test-signed KMDF package, " +
        "so installing this route can win that ranking and displace KMDF. " +
        "This does not happen every time, but it can. " +
        "If it does, you lose what KMDF gives you: the tunable scroll speed, and the direct battery read. " +
        "On the patched Apple driver scroll and battery are mutually exclusive, " +
        "and reading the battery needs a brief mode flip each time. " +
        "To get back, pick KMDF again in the tray and reinstall it.";

    // 0323: user-initiated Stock restore. Unbind to HidBth. Not Test Mode.
    // Run Uninstall-KMDF.cmd and Uninstall-MagicMousePatch.ps1 when present.
    // Never FLIP:NoFilter.
    internal static async Task<InstallOutcome> OfferV3StockRestoreAsync(CancellationToken ct = default)
    {
        var prompt = System.Windows.Forms.MessageBox.Show(
            "This restores the Magic Mouse 2024 (0323) to stock Windows HID (HidBth). No KMDF bind and no patched Apple filter. Test Mode is not required.\n\nOK continues. Cancel aborts.",
            "Magic Tray",
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Warning);
        if (prompt != System.Windows.Forms.DialogResult.OK)
        {
            Logger.Log("DRIVER_STOCK_ABORTED cancelled");
            return InstallOutcome.Cancelled;
        }

        string extractDir;
        try
        {
            extractDir = await DownloadV3DefaultBranchAsync(ct);
        }
        catch (Exception ex)
        {
            ShowBlocked($"Could not pull {DriverPackageCatalog.V3RepoUrl}. {ex.Message}");
            return InstallOutcome.Failed;
        }

        var kmdfUninstall = FindKmdfUninstaller(extractDir);
        var pathAUninstall = FindPathAUninstaller(extractDir);
        if (kmdfUninstall is null && pathAUninstall is null)
        {
            ShowBlocked(
                $"{DriverPackageCatalog.KmdfUninstallCmdRelativePath} and " +
                $"{DriverPackageCatalog.PathAUninstallScriptRelativePath} are not on " +
                $"{DriverPackageCatalog.V3RepoUrl} default branch '{DriverPackageCatalog.V3RepoRef}'. " +
                "Will not run FLIP:NoFilter as Stock.");
            return InstallOutcome.Unavailable;
        }

        // Two elevated steps, so a declined prompt means different things at
        // each one. Counting the steps that returned is the only way to tell
        // "nothing happened" from "half of it happened": RunElevated returns
        // only after the process exited 0.
        var completedSteps = 0;
        try
        {
            if (kmdfUninstall is not null)
            {
                Logger.Log($"DRIVER_STOCK_UNBIND script={kmdfUninstall}");
                RunElevated(
                    "cmd.exe",
                    $"/c \"{kmdfUninstall}\"",
                    workingDirectory: Path.GetDirectoryName(kmdfUninstall),
                    windowStyle: ProcessWindowStyle.Normal,
                    timeout: TimeSpan.FromMinutes(15));
                completedSteps++;
            }

            if (pathAUninstall is not null)
            {
                Logger.Log($"DRIVER_STOCK_UNBIND script={pathAUninstall}");
                RunElevated(
                    "powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{pathAUninstall}\"",
                    workingDirectory: Path.GetDirectoryName(pathAUninstall),
                    windowStyle: ProcessWindowStyle.Normal,
                    timeout: TimeSpan.FromMinutes(15));
                completedSteps++;
            }
        }
        catch (Exception ex) when (IsUacDeclined(ex) && completedSteps == 0)
        {
            Logger.Log("DRIVER_STOCK_ABORTED cancelled reason=uac-declined");
            return InstallOutcome.Cancelled;
        }
        catch (Exception ex) when (IsUacDeclined(ex))
        {
            // Declining the second prompt is not the same as declining the
            // first. completedSteps == 1 here can only be the KMDF uninstall,
            // which already ran elevated, and nothing rolls it back - so this
            // stays Failed and keeps its error box: the mouse is off KMDF with
            // the patched Apple filter still bound, and only the user can
            // finish that.
            Logger.Log("DRIVER_STOCK_PARTIAL cancelled reason=uac-declined step=2");
            ShowBlocked(V3StockPartialRestoreMessage());
            return InstallOutcome.Failed;
        }
        catch (Exception ex) when (completedSteps == 1)
        {
            // Same half-changed machine state as the declined second prompt -
            // KMDF uninstalled, patched Apple filter still bound - reached by a
            // different exception: RunElevated's 15-minute timeout, or its
            // non-zero exit ("powershell.exe exited 1."), or a start failure.
            // The state and the reason are both needed, so neither
            // replaces the other: a timeout and a non-zero exit are different
            // problems, and ex.Message is the only thing that tells them apart.
            Logger.Log($"DRIVER_STOCK_PARTIAL failed step=2 err={ex.Message}");
            ShowBlocked(V3StockPartialRestoreMessage(ex.Message));
            return InstallOutcome.Failed;
        }
        catch (Exception ex)
        {
            ShowBlocked(ex.Message);
            return InstallOutcome.Failed;
        }
        return InstallOutcome.Confirmed;
    }

    // Says which half of the restore happened, because "The operation was
    // canceled by the user." (the Win32Exception text) would leave the user
    // believing the machine is untouched when it is mid-change.
    internal static string V3StockPartialRestoreMessage() =>
        $"{DriverPackageCatalog.KmdfUninstallCmdRelativePath} ran, then the administrator prompt " +
        $"for {DriverPackageCatalog.PathAUninstallScriptRelativePath} was declined. " +
        V3StockPartialRestoreState();

    // Step 2 failed after step 1 returned, and not by a declined prompt. The
    // reason goes on its own line rather than inside the sentence: it is a
    // thrown message, so its punctuation and length are not ours to assume.
    internal static string V3StockPartialRestoreMessage(string reason) =>
        $"{DriverPackageCatalog.KmdfUninstallCmdRelativePath} ran, then " +
        $"{DriverPackageCatalog.PathAUninstallScriptRelativePath} stopped before it finished. " +
        V3StockPartialRestoreState() +
        $"\n\n{reason}";

    // The state is identical whichever exception reported it, so both messages
    // share this half rather than drifting apart as two near-copies.
    static string V3StockPartialRestoreState() =>
        "The KMDF uninstall is done and the patched Apple filter is still in place, " +
        "so this Magic Mouse is not on stock HidBth yet. " +
        "Run Stock again and approve both prompts to finish it.";

    // Keyboard PATH-C. Discovers the live Bluetooth MAC and passes -Mac.
    // The script requires -Mac - never omit it. Confirm-first like every other
    // elevated offer: Cancel runs nothing at all.
    //
    // RunElevated hides the console, so the script's own words reach the user
    // only through the dialogs below and the -LogPath it is given.
    internal static InstallOutcome OfferKeyboardSdpPatch()
    {
        var script = FindKeyboardPatchScript();
        if (script is null)
        {
            ShowBlocked(
                $"{DriverPackageCatalog.KeyboardPatchScriptPath} not found next to the tray.");
            return InstallOutcome.Unavailable;
        }

        var mac = TryDiscoverKeyboardMac();
        if (mac is null)
        {
            ShowBlocked(
                "Could not discover a Magic Keyboard Bluetooth MAC. " +
                "Refusing to run kbd-patch-cachedservices.ps1 without -Mac.");
            return InstallOutcome.Unavailable;
        }

        // Unavailable, not Failed: the unpatched record this offer needs is
        // not on this machine. Nothing ran, nothing is wrong, and the user is
        // told what is actually left to do.
        if (SkipsKeyboardSdpPatch(SdpPatchReader.ForMac(mac)))
        {
            Logger.Log("DRIVER_SDP_SKIPPED reason=already-applied");
            ShowInfo(KeyboardSdpAlreadyAppliedMessage());
            return InstallOutcome.Unavailable;
        }

        var prompt = System.Windows.Forms.MessageBox.Show(
            KeyboardSdpPatchPrompt(mac),
            "Magic Tray",
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Warning);
        if (prompt != System.Windows.Forms.DialogResult.OK)
        {
            Logger.Log("DRIVER_SDP_ABORTED cancelled");
            return InstallOutcome.Cancelled;
        }

        var logPath = NewKeyboardPatchLogPath();
        var args =
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Mac \"{mac}\" " +
            $"-LogPath \"{logPath}\"";
        Logger.Log($"DRIVER_SDP script={script} mac={mac}");
        int exitCode;
        try
        {
            exitCode = RunElevatedExitCode("powershell.exe", args);
        }
        catch (Exception ex) when (IsUacDeclined(ex))
        {
            Logger.Log("DRIVER_SDP_ABORTED cancelled reason=uac-declined");
            return InstallOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            ShowBlocked(ex.Message);
            return InstallOutcome.Failed;
        }

        // Every non-zero exit is Failed with the script's own words, exit 4
        // (PARTIAL) included: a half-unlocked cache is reported as the half
        // that happened, never as success for the half that did.
        if (exitCode != 0)
        {
            Logger.Log($"DRIVER_SDP_FAILED exit={exitCode}");
            ShowBlocked(KeyboardSdpFailureMessage(exitCode, logPath, ReadPatchLogTail(logPath)));
            return InstallOutcome.Failed;
        }

        Logger.Log("DRIVER_SDP_DONE exit=0");
        ShowInfo(KeyboardSdpNextStepMessage());
        return InstallOutcome.Confirmed;
    }

    // Applied is the ONLY read-back meaning "there is nothing to do": the
    // marker is in the cache, so running the script again would only confirm
    // it. NotApplied is the exact state this offer exists for, and Unknown is
    // no evidence at all - an unreadable hive, a record in a shape this reader
    // does not parse - so both of those still run the script and let it decide
    // with its own eyes. Refusing to run on Unknown would turn "I cannot tell"
    // into "you cannot have it".
    internal static bool SkipsKeyboardSdpPatch(SdpPatchState state) =>
        state == SdpPatchState.Applied;

    // Exit codes kbd-patch-cachedservices.ps1 defines in its own header. 0 is
    // "the cache carries the unlock and nothing was blocked" - newly inserted
    // OR already there. 4 is the mixed run: at least one record carries the
    // unlock AND at least one candidate record was blocked.
    internal const int KeyboardPatchExitNoRecord = 2;
    internal const int KeyboardPatchExitNotPatched = 3;
    internal const int KeyboardPatchExitPartial = 4;

    // The log goes in the TRAY's profile, not the elevated process's: a UAC
    // prompt answered with a different administrator account has a different
    // %APPDATA%, and the tray must be able to read back the run it started.
    internal static string NewKeyboardPatchLogPath() => Path.Combine(
        Logger.LogDir,
        $"kbd-patch-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    internal static string KeyboardSdpAlreadyAppliedMessage() =>
        "The battery unlock is already in this keyboard's Bluetooth record, so nothing was run. "
        + "Windows only re-reads that record when the Bluetooth radio cycles: turn Bluetooth off "
        + "and on again, and the percent appears within a minute or two.\n\n"
        + "Re-pairing the keyboard erases the unlock - that is the one case where this needs "
        + "running again.";

    internal static string KeyboardSdpNextStepMessage() =>
        "This keyboard's Bluetooth record now carries the battery unlock. One step is left: "
        + "turn Bluetooth off and on again, because Windows only re-reads that record when the "
        + "radio cycles. The percent appears within a minute or two.\n\n"
        + "Running this again later is safe - it reports the unlock as already applied and "
        + "changes nothing.";

    // The script's own last lines, not a guess at what an exit code meant. The
    // tail is included because the elevated console is hidden, so this dialog
    // and debug.log are the only places its words can appear.
    internal static string KeyboardSdpFailureMessage(int exitCode, string logPath, string? logTail)
    {
        var head = exitCode switch
        {
            KeyboardPatchExitNoRecord =>
                "No Bluetooth record for this keyboard carries the battery report, so nothing "
                + "was changed. Windows rebuilds that record when the keyboard re-pairs, so "
                + "pairing it again and retrying is the next thing to try.",
            KeyboardPatchExitNotPatched =>
                "Nothing in this keyboard's Bluetooth cache could be unlocked, so nothing was "
                + "changed. The log below says what blocked each record.",
            KeyboardPatchExitPartial =>
                "Part of this keyboard's Bluetooth cache was unlocked, but at least one record "
                + "was blocked. Turn Bluetooth off and on again - the percent may already "
                + "appear. If it does not, please report the blocked record and the log below.",
            // An exit code this app does not name includes PowerShell's own 1,
            // which can land before the registry was opened at all or after one
            // record has already been rewritten.
            _ =>
                $"The battery unlock stopped with exit code {exitCode} before it could report "
                + "what it did. Part of this keyboard's Bluetooth cache may already carry the "
                + "unlock. The original bytes of every record it touched are backed up in the "
                + "log's own directory.",
        };

        var text = head + $"\n\nLog: {logPath}";
        text += string.IsNullOrWhiteSpace(logTail)
            ? "\n\nThe run left no readable log at that path, which is itself worth reporting."
            : $"\n\n{logTail}";
        return text;
    }

    // Last lines of the run log, if it is there at all; null when it is not,
    // which the caller's dialog says in so many words.
    internal static string? ReadPatchLogTail(string path, int maxLines = 12)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var lines = File.ReadAllLines(path);
            var from = Math.Max(0, lines.Length - maxLines);
            return string.Join("\n", lines[from..]).Trim();
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_SDP_LOG_UNREADABLE err={ex.Message}");
            return null;
        }
    }

    // States what runs, what it changes, that it needs one administrator
    // approval, that it changes nothing else, and how to undo it.
    internal static string KeyboardSdpPatchPrompt(string mac) =>
        $"This runs {DriverPackageCatalog.KeyboardPatchScriptPath} -Mac {mac} for this Magic Keyboard. " +
        "It changes the Bluetooth SDP cache entry for that keyboard so Windows exposes its battery. " +
        "It needs one administrator approval and changes nothing else. " +
        "Re-pairing the keyboard can undo it.\n\n" +
        "OK continues. Cancel aborts.";

    internal static async Task<string> DownloadV3DefaultBranchAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(CacheRoot);
        var zipPath = Path.Combine(CacheRoot, $"{DriverPackageCatalog.V3RepoName}-{DriverPackageCatalog.V3RepoRef}.zip");
        var extractDir = Path.Combine(CacheRoot, $"{DriverPackageCatalog.V3RepoName}-{DriverPackageCatalog.V3RepoRef}");
        var url = DriverPackageCatalog.V3ZipUrl;

        Logger.Log($"DRIVER_PULL url={url}");
        await using (var net = await Http.GetStreamAsync(url, ct))
        await using (var file = File.Create(zipPath))
            await net.CopyToAsync(file, ct);

        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, extractDir);
        return extractDir;
    }

    // PATH-A patched applewirelessmouse.sys. Blocks KMDF lookup from picking
    // this script. The dedicated PathA offer runs it on purpose.
    internal static bool IsPathAInstaller(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var n = path.Replace('\\', '/');
        return n.Contains("v1-binary-patch", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Install-MagicMousePatch", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsKmdfOneClick(string path)
    {
        if (string.IsNullOrEmpty(path) || IsPathAInstaller(path))
            return false;
        var n = path.Replace('\\', '/');
        return n.Contains("v2-kmdf-driver/", StringComparison.OrdinalIgnoreCase)
            && n.EndsWith("/" + DriverPackageCatalog.KmdfOneClickName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsKmdfUninstall(string path)
    {
        if (string.IsNullOrEmpty(path) || IsPathAInstaller(path))
            return false;
        var n = path.Replace('\\', '/');
        return n.Contains("v2-kmdf-driver/", StringComparison.OrdinalIgnoreCase)
            && n.EndsWith("/" + DriverPackageCatalog.KmdfUninstallCmdName, StringComparison.OrdinalIgnoreCase);
    }

    // Install-KMDF.cmd under v2-kmdf-driver only. Null if default branch has not landed it.
    internal static string? FindKmdfOneClick(string extractRoot, string pathInRepo)
    {
        if (!Directory.Exists(extractRoot))
            return null;

        if (!string.IsNullOrEmpty(pathInRepo) && !IsPathAInstaller(pathInRepo))
        {
            var rel = pathInRepo.Replace('/', Path.DirectorySeparatorChar);
            foreach (var file in Directory.EnumerateFiles(extractRoot, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(rel, StringComparison.OrdinalIgnoreCase) && IsKmdfOneClick(file))
                    return file;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(extractRoot, "v2-kmdf-driver", SearchOption.AllDirectories))
        {
            var cmd = Path.Combine(dir, DriverPackageCatalog.KmdfOneClickName);
            if (File.Exists(cmd) && IsKmdfOneClick(cmd))
                return cmd;
        }

        return null;
    }

    // Install-MagicMousePatch.ps1 under v1-binary-patch only. Null if missing.
    // Never returns Install-KMDF.cmd.
    internal static string? FindPathAInstaller(string extractRoot, string pathInRepo)
    {
        if (!Directory.Exists(extractRoot))
            return null;

        if (!string.IsNullOrEmpty(pathInRepo) && IsPathAInstaller(pathInRepo)
            && !IsPathAUninstallScript(pathInRepo))
        {
            var rel = pathInRepo.Replace('/', Path.DirectorySeparatorChar);
            foreach (var file in Directory.EnumerateFiles(extractRoot, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(rel, StringComparison.OrdinalIgnoreCase)
                    && IsPathAInstallScript(file))
                    return file;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(extractRoot, "v1-binary-patch", SearchOption.AllDirectories))
        {
            var ps1 = Path.Combine(dir, "installer", DriverPackageCatalog.PathAInstallScriptName);
            if (File.Exists(ps1) && IsPathAInstallScript(ps1))
                return ps1;
        }

        return null;
    }

    // Uninstall-MagicMousePatch.ps1 under v1-binary-patch. Null if missing.
    // Never invents FLIP:NoFilter.
    internal static string? FindPathAUninstaller(string extractRoot)
    {
        if (!Directory.Exists(extractRoot))
            return null;

        var rel = DriverPackageCatalog.PathAUninstallScriptRelativePath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var file in Directory.EnumerateFiles(extractRoot, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(rel, StringComparison.OrdinalIgnoreCase)
                && IsPathAUninstallScript(file))
                return file;
        }

        return null;
    }

    // Uninstall-KMDF.cmd under v2-kmdf-driver. Null if missing.
    // Never invents FLIP:NoFilter. Never returns Install-KMDF.cmd.
    internal static string? FindKmdfUninstaller(string extractRoot)
    {
        if (!Directory.Exists(extractRoot))
            return null;

        var rel = DriverPackageCatalog.KmdfUninstallCmdRelativePath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var file in Directory.EnumerateFiles(extractRoot, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(rel, StringComparison.OrdinalIgnoreCase) && IsKmdfUninstall(file))
                return file;
        }

        foreach (var dir in Directory.EnumerateDirectories(extractRoot, "v2-kmdf-driver", SearchOption.AllDirectories))
        {
            var cmd = Path.Combine(dir, DriverPackageCatalog.KmdfUninstallCmdName);
            if (File.Exists(cmd) && IsKmdfUninstall(cmd))
                return cmd;
        }

        return null;
    }

    static bool IsPathAInstallScript(string path)
    {
        if (string.IsNullOrEmpty(path) || IsKmdfOneClick(path)) return false;
        var n = path.Replace('\\', '/');
        return IsPathAInstaller(path)
            && n.EndsWith("/" + DriverPackageCatalog.PathAInstallScriptName, StringComparison.OrdinalIgnoreCase);
    }

    static bool IsPathAUninstallScript(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var n = path.Replace('\\', '/');
        return n.EndsWith("/" + DriverPackageCatalog.PathAUninstallScriptName, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? FindKeyboardPatchScript()
    {
        var names = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "scripts", DriverPackageCatalog.KeyboardPatchScriptName),
            Path.Combine(AppContext.BaseDirectory, DriverPackageCatalog.KeyboardPatchScriptName),
        };
        foreach (var p in names)
            if (File.Exists(p)) return p;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, DriverPackageCatalog.KeyboardPatchScriptPath);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // BTHENUM instance tails carry the BT MAC, e.g. ...&E806884B0741_C00000000
    internal static string? TryDiscoverKeyboardMac()
    {
        try
        {
            using var btEnumKey = Registry.LocalMachine.OpenSubKey(BtHidEnumBase, writable: false);
            if (btEnumKey == null) return null;

            foreach (var subkeyName in btEnumKey.GetSubKeyNames())
            {
                if (!subkeyName.StartsWith(HidUuidPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsAppleVid(subkeyName)) continue;

                var pid = TryPid(subkeyName);
                if (pid is null || !DriverHealthChecker.IsKeyboardPid(pid)) continue;

                using var deviceKey = btEnumKey.OpenSubKey(subkeyName, writable: false);
                if (deviceKey == null) continue;

                foreach (var instanceName in deviceKey.GetSubKeyNames())
                {
                    var mac = ParseMacFromInstance(instanceName);
                    if (mac is not null) return mac;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_SDP_MAC_FAILED err={ex.Message}");
        }
        return null;
    }

    internal static string? ParseMacFromInstance(string instanceName)
    {
        var m = Regex.Match(instanceName, @"([0-9A-Fa-f]{12})_C00000000", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    static bool IsAppleVid(string subkeyName)
    {
        return subkeyName.Contains("_VID&000205ac_", StringComparison.OrdinalIgnoreCase)
            || subkeyName.Contains("_VID&0001004c_", StringComparison.OrdinalIgnoreCase);
    }

    static string? TryPid(string subkeyName)
    {
        int pidIdx = subkeyName.LastIndexOf("_PID&", StringComparison.OrdinalIgnoreCase);
        if (pidIdx < 0 || pidIdx + 9 > subkeyName.Length) return null;
        return subkeyName.Substring(pidIdx + 5, 4).ToLowerInvariant();
    }

    // ShellExecute reports a declined UAC prompt as Win32Exception with native
    // error 1223 (ERROR_CANCELLED); Process.Start surfaces that unchanged.
    // Same signal DeviceEnable.LaunchElevated and DeviceRepair.LaunchElevated
    // fold into Started=false. The native code is the test, not the message,
    // which is localised. Every other Win32Exception - 2 ERROR_FILE_NOT_FOUND,
    // 740 ERROR_ELEVATION_REQUIRED - is a real failure and stays Failed.
    // A caller that sees this must not also show an error box: the user is the
    // one who said no, and nothing ran.
    internal static bool IsUacDeclined(Exception ex) =>
        ex is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 1223;

    static void RunElevated(
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden)
    {
        var exitCode = RunElevatedExitCode(fileName, arguments, timeout, workingDirectory, windowStyle);
        if (exitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited {exitCode}.");
    }

    // Same launch, exit code returned instead of thrown. A caller that knows
    // what its script's codes MEAN reports them; everything else keeps the
    // throwing form above, where any non-zero exit is simply a failure.
    static int RunElevatedExitCode(
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
        return p.ExitCode;
    }

    // Offer* returns its outcome instead of throwing it, so the explanation
    // has to be shown here or the user never sees why nothing happened.
    static void ShowBlocked(string message)
    {
        Logger.Log($"DRIVER_BLOCKED err={message.Replace("\n", " ").Replace("\r", " ")}");
        System.Windows.Forms.MessageBox.Show(
            message,
            "Magic Tray",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Error);
    }

    // The counterpart to ShowBlocked for a state that is not a fault: nothing
    // failed, and there is one more step the user has to take.
    static void ShowInfo(string message)
    {
        Logger.Log($"DRIVER_INFO msg={message.Replace("\n", " ")}");
        System.Windows.Forms.MessageBox.Show(
            message,
            "Magic Tray",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Information);
    }
}
