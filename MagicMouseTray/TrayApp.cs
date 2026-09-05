// SPDX-License-Identifier: MIT
using Microsoft.Win32;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MagicMouseTray;

// Copy helpers for the tray menu. Kept free of WinForms so tests can cover
// per-device badge/action rules without spinning a NotifyIcon.
internal static class TrayMenu
{
    internal const string ProductName = "Magic Tray";
    internal const string ReleasesUrl = "https://github.com/LesleyMurfin/magic-tray/releases";
    internal const string RepoUrl = "https://github.com/LesleyMurfin/magic-tray";
    internal const string IssuesUrl = "https://github.com/LesleyMurfin/magic-tray/issues";
    internal const string AlertsDocUrl = "https://github.com/LesleyMurfin/magic-tray/blob/main/docs/ALERTS.md";
    internal const string EnabledOnThisPc = "Enabled on this PC";
    internal const string HelpMenuLabel = "Help/Documentation";
    internal const string HowAlertsWorkLabel = "How alerts work";
    internal const string RepositoryLabel = "Repository";
    internal const string ReportBugLabel = "Report a bug";
    internal const string RequestFeatureLabel = "Request a feature";
    internal const string ReportBugConfirm =
        "Magic Tray will collect version, driver badges, battery readings, and the last log lines (Bluetooth MAC redacted), copy them, and open a GitHub issue draft. You submit it while logged in.\n\nContinue?";
    internal const string RequestFeatureConfirm =
        "Magic Tray will open a GitHub feature-request draft with the app version. The text is also on the clipboard. You submit it while logged in.\n\nContinue?";

    // Global picker: percent floor, then time alerts. No invented hours.
    internal static string GlobalThresholdLabel(int pct) => $"{pct}%  then time alerts";

    // Per-device: (~Nd)/(~Nh) only when GetHoursToEmpty > 0. Unknown → "10%".
    internal static string DeviceThresholdLabel(int pct, double hoursToEmpty)
    {
        if (hoursToEmpty > 0)
            return $"{pct}%  ({FormatHoursToEmpty(hoursToEmpty)})";
        return $"{pct}%";
    }

    internal static string FormatHoursToEmpty(double hours)
    {
        if (hours >= 24)
        {
            var days = Math.Max(1, (int)Math.Round(hours / 24.0));
            return $"~{days}d";
        }

        var h = Math.Max(1, (int)Math.Round(hours));
        return $"~{h}h";
    }

    internal static string? FindLocalAlertsDoc(string? startDir = null)
    {
        string? dir = startDir ?? AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var p = Path.Combine(dir, "docs", "ALERTS.md");
            if (File.Exists(p))
                return p;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    internal static bool PidEq(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static bool IsV3(DeviceKind kind, string? pid) =>
        kind == DeviceKind.MagicMouseV3 || PidEq(pid, "0323");

    internal static bool IsV1V2Mouse(DeviceKind kind) =>
        kind is DeviceKind.MagicMouseV1 or DeviceKind.MagicMouseV2;

    internal static string BatteryText(int pct) => DeviceCapability.BatteryLabel(pct);

    // v1/v2 Driver radios replaced the orange Fix scroll item. Always false.
    internal static bool ShowFixScroll(DeviceKind kind, DriverStatus? status)
    {
        _ = kind;
        _ = status;
        return false;
    }

    internal static bool ShowFixKeyboard(DeviceKind kind, int pct) =>
        kind == DeviceKind.MagicKeyboard && pct == -2;

    internal const string TrackpadV1BootCampLabel = "Boot Camp";

    // Magic Trackpad v1 (030E) only. Not KMDF radios, not 0265/0324.
    internal static bool ShowTrackpadV1BootCamp(DeviceKind kind, string? pid) =>
        kind == DeviceKind.MagicTrackpadV1 && DriverInstaller.IsTrackpadV1BootCampPid(pid);

    internal static bool ShowBatteryReads(bool v3Connected, DriverStatus? v3Status) =>
        v3Connected && v3Status == DriverStatus.PatchedKmdf;

    // Scroll vs Battery radios: only while v3 is bound to patched Apple (PathA).
    internal const string V3ModeRadioScroll = "Scroll";
    internal const string V3ModeRadioBattery = "Battery";

    internal static bool ShowPathAModeSwitch(DeviceKind kind, string? pid, DriverStatus? status) =>
        IsV3(kind, pid) && status == DriverStatus.PathAPatched;

    // Exact radio copy. No PATH-A / applewirelessmouse / installer names.
    internal const string V3RadioKmdf = "KMDF";
    internal const string V3RadioPatchedApple = "Patched Apple driver";
    internal const string V3RadioStockWindows = "Stock Windows";

    internal static readonly string[] V3DriverRadioLabels =
    [
        V3RadioKmdf,
        V3RadioPatchedApple,
        V3RadioStockWindows,
    ];

    // Exactly one radio from Classify; NotBound checks none.
    internal static string? V3CheckedDriverRadio(DriverStatus? status) => status switch
    {
        DriverStatus.PatchedKmdf => V3RadioKmdf,
        DriverStatus.PathAPatched => V3RadioPatchedApple,
        DriverStatus.StockKmdf => V3RadioStockWindows,
        _ => null,
    };

    // Exact v1/v2 radio copy. No PATH-A / applewirelessmouse / KMDF / tealtadpole.
    internal const string V1V2RadioBootCamp = "Boot Camp";
    internal const string V1V2RadioStockWindows = "Stock Windows";

    internal static readonly string[] V1V2DriverRadioLabels =
    [
        V1V2RadioBootCamp,
        V1V2RadioStockWindows,
    ];

    // Exactly one radio from Classify; NotBound checks none.
    internal static string? V1V2CheckedDriverRadio(DriverStatus? status) => status switch
    {
        DriverStatus.Ok => V1V2RadioBootCamp,
        DriverStatus.NotInstalled => V1V2RadioStockWindows,
        _ => null,
    };

    // Checked radio is disabled. Stock clickable on Ok (Boot Camp bound) and NotBound.
    internal static bool V1V2BootCampRadioEnabled(DriverStatus? status) =>
        status != DriverStatus.Ok;

    internal static bool V1V2StockRadioEnabled(DriverStatus? status) =>
        status != DriverStatus.NotInstalled;

    internal static string? V3Badge(DriverStatus? status) => status switch
    {
        DriverStatus.PatchedKmdf => "KMDF",
        DriverStatus.PathAPatched => "Patched Apple",
        DriverStatus.StockKmdf => "Stock",
        DriverStatus.Error => "Error",
        _ => "Not bound",
    };

    internal static string? V1V2Badge(DriverStatus? status) => status switch
    {
        DriverStatus.Ok => "Boot Camp",
        DriverStatus.NotInstalled => "Stock",
        DriverStatus.Error => "Error",
        _ => "Not bound",
    };

    internal static string? RecommendedLabel(DeviceKind kind, DriverStatus? status, int pct)
    {
        if (kind == DeviceKind.MagicMouseV3 && status == DriverStatus.NotBound)
            return $"Recommended: KMDF ({DriverPackageCatalog.PatchedKmdfServiceName})";
        if (IsV1V2Mouse(kind) && status == DriverStatus.NotBound)
            return "Recommended: Boot Camp";
        if (kind == DeviceKind.MagicKeyboard && pct == -2)
            return "Recommended: SDP battery patch";
        return null;
    }

    // First Driver submenu item: live bound service, or (none).
    internal static string BoundLabel(string? boundDriverName) =>
        string.IsNullOrEmpty(boundDriverName) ? "Bound: (none)" : $"Bound: {boundDriverName}";

    internal static bool IconAttention(string pid, DriverStatus status)
    {
        if (PidEq(pid, "0323"))
            return status is DriverStatus.UnknownAppleMouse or DriverStatus.Error;
        // NotInstalled is valid Stock for v1/v2 — not attention.
        return status is DriverStatus.NotBound
            or DriverStatus.UnknownAppleMouse or DriverStatus.Error;
    }

    // Health-only tray row: known mouse PID not already in poll results, or UnknownAppleMouse.
    // 030D Ok and NotInstalled must appear — Stock radios are reachable on NotInstalled.
    internal static bool ShouldShowHealthRow(IEnumerable<string> shownPids, string healthPid, DriverStatus status)
    {
        if (string.IsNullOrEmpty(healthPid))
            return false;
        foreach (var shown in shownPids)
        {
            if (PidEq(shown, healthPid))
                return false;
        }

        if (status == DriverStatus.UnknownAppleMouse)
            return true;

        if (!Array.Exists(DriverHealthChecker.KnownMousePids, p => PidEq(p, healthPid)))
            return false;

        return status is DriverStatus.Ok
            or DriverStatus.NotBound
            or DriverStatus.NotInstalled
            or DriverStatus.PathAPatched
            or DriverStatus.PatchedKmdf
            or DriverStatus.StockKmdf;
    }


    internal static string RowLabel(string name, int pct, string? badge, string extras)
    {
        var s = $"{name}    {BatteryText(pct)}";
        if (!string.IsNullOrEmpty(extras)) s += $"  {extras}";
        if (!string.IsNullOrEmpty(badge)) s += $"    {badge}";
        return s;
    }
}

// System tray icon, per-device menu, battery alerts. Driver work is always
// user-initiated via DriverInstaller — never a silent rebind.
internal sealed class TrayApp : IDisposable
{
    readonly NotifyIcon _tray;
    readonly Config _config;
    readonly AdaptivePoller _poller;
    readonly ToolStripMenuItem _startupItem;
    readonly ToolStripMenuItem _batteryReadsItem;
    readonly ToolStripMenuItem _globalThresholdMenu;

    readonly Dictionary<string, (int Pct, DeviceKind Kind, string Pid)> _deviceBatteries = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ToolStripMenuItem> _deviceMenuItems = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> _firedEvents = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> _lastGoodPct = new(StringComparer.OrdinalIgnoreCase);

    ToolStripMenuItem? _deviceSection;
    IReadOnlyList<DeviceDriverHealth> _health = Array.Empty<DeviceDriverHealth>();

    CriticalAlert? _criticalAlert;
    string? _criticalDevice;
    bool _criticalStayOnDisconnect;
    Icon? _currentIcon;
    static bool _lightTaskbar;
    ToolStripMenuItem? _updateItem;

    internal TrayApp(Config config)
    {
        _config = config;
        _startupItem = null!;
        _batteryReadsItem = null!;
        _globalThresholdMenu = null!;

        _health = ReadHealth();
        var menu = BuildMenu(out _startupItem, out _batteryReadsItem, out _globalThresholdMenu);

        RefreshTheme();
        _currentIcon = MakeIcon(-1, false, Marker.Mouse, AnyDriverAttention());
        _tray = new NotifyIcon
        {
            Icon = _currentIcon,
            ContextMenuStrip = menu,
            Visible = true,
            Text = $"{TrayMenu.ProductName} — starting..."
        };

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemVisualChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemVisualChanged;

        _poller = new AdaptivePoller(_config);
        _poller.BatteryChanged += OnBatteryChanged;
        _poller.Start();

        if (_config.UpdateCheck)
            _ = CheckForUpdateBackgroundAsync();
    }

    async Task CheckForUpdateBackgroundAsync()
    {
        var tag = await UpdateChecker.CheckForUpdateAsync();
        if (tag != null)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_updateItem != null)
                {
                    _updateItem.Text = $"Update available — {tag}";
                    _updateItem.Visible = true;
                }
            });
        }
    }

    /// <summary>
    /// Builds the tray context menu, including the Help section entries that open the
    /// pre-filled bug and feature drafts. Rebuilt state is refreshed on Opening.
    /// </summary>
    ContextMenuStrip BuildMenu(
        out ToolStripMenuItem startupItem,
        out ToolStripMenuItem batteryReadsItem,
        out ToolStripMenuItem globalThresholdMenu)
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            _health = ReadHealth();
            UpdateDeviceMenuItems();
            RefreshGlobalThresholdChecks();
            UpdateBatteryReadsVisibility();
        };

        _deviceSection = new ToolStripMenuItem("Devices") { Enabled = false };
        menu.Items.Add(_deviceSection);
        menu.Items.Add(new ToolStripSeparator());

        var bluetoothMenu = new ToolStripMenuItem(BluetoothSettings.MenuLabel);
        foreach (var label in BluetoothSettings.MenuItemLabels)
        {
            var btItem = new ToolStripMenuItem(label);
            if (label == BluetoothSettings.RenameADevice)
                btItem.Click += (_, _) => BluetoothSettings.OpenRenamePage();
            else
                btItem.Click += (_, _) => BluetoothSettings.OpenDevicesPage();
            bluetoothMenu.DropDownItems.Add(btItem);
        }
        menu.Items.Add(bluetoothMenu);

        globalThresholdMenu = new ToolStripMenuItem("Low battery threshold");
        foreach (var t in Config.ThresholdChoices)
        {
            var tItem = new ToolStripMenuItem(TrayMenu.GlobalThresholdLabel(t))
            {
                Checked = t == _config.GlobalThreshold,
                Tag = t,
            };
            tItem.Click += (_, _) =>
            {
                _config.SetGlobalThreshold(t);
                RefreshGlobalThresholdChecks();
                UpdateTrayIcon();
            };
            globalThresholdMenu.DropDownItems.Add(tItem);
        }
        menu.Items.Add(globalThresholdMenu);

        startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = _config.StartWithWindows
        };
        startupItem.Click += (_, _) =>
        {
            _config.SetStartWithWindows(!_config.StartWithWindows);
            _startupItem.Checked = _config.StartWithWindows;
        };
        menu.Items.Add(startupItem);

        var thirdPartyItem = new ToolStripMenuItem(
            _config.EnableThirdParty ? "Show Logitech devices [On]" : "Show Logitech devices [Off]")
        {
            Checked = _config.EnableThirdParty
        };
        thirdPartyItem.Click += (_, _) =>
        {
            _config.SetEnableThirdParty(!_config.EnableThirdParty);
            thirdPartyItem.Text = _config.EnableThirdParty ? "Show Logitech devices [On]" : "Show Logitech devices [Off]";
            thirdPartyItem.Checked = _config.EnableThirdParty;
        };
        menu.Items.Add(thirdPartyItem);

        batteryReadsItem = new ToolStripMenuItem("Battery reads")
        {
            Checked = true,
            Enabled = false,
            Visible = false
        };
        menu.Items.Add(batteryReadsItem);

        menu.Items.Add(new ToolStripSeparator());

        var refresh = new ToolStripMenuItem("Refresh Now");
        refresh.Click += (_, _) => _poller.RefreshNow();
        menu.Items.Add(refresh);

        var diagnostics = new ToolStripMenuItem("Diagnostics");

        var testToast = new ToolStripMenuItem("Test notification");
        testToast.Click += (_, _) =>
        {
            var live = _deviceBatteries.FirstOrDefault(kv => kv.Value.Pct >= 0);
            string name;
            DeviceKind kind;
            int pct;
            if (!string.IsNullOrEmpty(live.Key))
            {
                name = live.Key;
                kind = live.Value.Kind;
                pct = live.Value.Pct;
            }
            else
            {
                name = TrayMenu.ProductName;
                kind = DeviceKind.MagicMouseV3;
                pct = 10;
            }
            var preview = BatteryAlertPolicy.PreviewToast(kind, name, pct);
            ToastNotifier.Show(preview.Title, preview.Body);
        };
        diagnostics.DropDownItems.Add(testToast);

        var openLogs = new ToolStripMenuItem("Open logs");
        openLogs.Click += (_, _) => OpenLogsInEditor();
        diagnostics.DropDownItems.Add(openLogs);

        var openDiagFolder = new ToolStripMenuItem("Open diagnostics folder");
        openDiagFolder.Click += (_, _) => OpenDiagnosticsFolder();
        diagnostics.DropDownItems.Add(openDiagFolder);

        AddDiagnosticScript(diagnostics, DiagnosticScripts.CaptureStateLabel,
            DiagnosticScripts.Find(DiagnosticScripts.CaptureState));
        AddDiagnosticScript(diagnostics, DiagnosticScripts.DiagnoseDriverLabel,
            DiagnosticScripts.Find(DiagnosticScripts.DiagnoseDriver));
        var stack = DiagnosticScripts.FindStackDump();
        if (stack is not null)
            AddDiagnosticScript(diagnostics, stack.Value.Label, stack.Value.Path);

        menu.Items.Add(diagnostics);

        var help = new ToolStripMenuItem(TrayMenu.HelpMenuLabel);
        var howAlerts = new ToolStripMenuItem(TrayMenu.HowAlertsWorkLabel);
        howAlerts.Click += (_, _) => OpenHelpUrl(TrayMenu.AlertsDocUrl, TrayMenu.FindLocalAlertsDoc());
        help.DropDownItems.Add(howAlerts);
        var repoItem = new ToolStripMenuItem(TrayMenu.RepositoryLabel);
        repoItem.Click += (_, _) => OpenHelpUrl(TrayMenu.RepoUrl);
        help.DropDownItems.Add(repoItem);
        var bugItem = new ToolStripMenuItem(TrayMenu.ReportBugLabel);
        bugItem.Click += (_, _) => OpenGitHubDraft(feature: false);
        help.DropDownItems.Add(bugItem);
        var featItem = new ToolStripMenuItem(TrayMenu.RequestFeatureLabel);
        featItem.Click += (_, _) => OpenGitHubDraft(feature: true);
        help.DropDownItems.Add(featItem);
        menu.Items.Add(help);

        menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) =>
        {
            Dispose();
            System.Windows.Application.Current.Shutdown();
        };
        menu.Items.Add(quit);

        menu.Items.Add(new ToolStripSeparator());

        var asmVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var semver = asmVer != null ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}" : "1.0.0";
        menu.Items.Add(new ToolStripMenuItem($"{TrayMenu.ProductName} {semver}") { Enabled = false });

        _updateItem = new ToolStripMenuItem("Update available") { Visible = false };
        _updateItem.Click += (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TrayMenu.ReleasesUrl)
            {
                UseShellExecute = true
            });
        };
        menu.Items.Add(_updateItem);

        return menu;
    }

    void RefreshGlobalThresholdChecks()
    {
        foreach (ToolStripMenuItem tItem in _globalThresholdMenu.DropDownItems)
        {
            if (tItem.Tag is int t)
                tItem.Checked = t == _config.GlobalThreshold;
        }
    }

    void UpdateBatteryReadsVisibility()
    {
        var v3Connected = _deviceBatteries.Values.Any(v => TrayMenu.IsV3(v.Kind, v.Pid));
        DriverStatus? v3Status = null;
        foreach (var h in _health)
        {
            if (TrayMenu.PidEq(h.Pid, "0323"))
            {
                v3Status = h.Status;
                break;
            }
        }
        _batteryReadsItem.Visible = TrayMenu.ShowBatteryReads(v3Connected, v3Status);
        _batteryReadsItem.Checked = _batteryReadsItem.Visible;
        _batteryReadsItem.Enabled = false;
    }

    void OpenLogsInEditor()
    {
        var logPath = Logger.LogPath;
        if (!System.IO.File.Exists(logPath))
        {
            System.IO.Directory.CreateDirectory(Logger.LogDir);
            System.IO.File.WriteAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Log file created on demand from tray.\r\n");
        }

        var npp = FindNotepadPlusPlus();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = npp ?? "notepad.exe",
                Arguments = $"\"{logPath}\"",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            Logger.Log($"OPEN_LOGS editor={(npp ?? "notepad.exe")} path={logPath}");
        }
        catch (Exception ex)
        {
            Logger.Log($"OPEN_LOGS_FAIL err={ex.Message}");
        }
    }

    /// <summary>
    /// Opens the folder holding debug.log so a reporter can attach it by hand;
    /// failures are logged, never surfaced as a dialog.
    /// </summary>
    void OpenDiagnosticsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Logger.LogDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Logger.LogDir}\"",
                UseShellExecute = true
            });
            Logger.Log($"OPEN_DIAG_FOLDER path={Logger.LogDir}");
        }
        catch (Exception ex)
        {
            Logger.Log($"OPEN_DIAG_FOLDER_FAIL err={ex.Message}");
        }
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser, then
    /// <paramref name="localFallback"/> if that throws. Returns true only when a
    /// browser (or the local fallback) actually launched, so callers can run their
    /// own fallback instead of assuming success.
    /// </summary>
    bool OpenHelpUrl(string url, string? localFallback = null)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"OPEN_HELP_FAIL url={url} err={ex.Message}");
            if (string.IsNullOrEmpty(localFallback))
                return false;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(localFallback)
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex2)
            {
                Logger.Log($"OPEN_HELP_LOCAL_FAIL path={localFallback} err={ex2.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Confirms with the user, builds the redacted bug or feature Markdown, copies it
    /// to the clipboard, and opens the pre-filled GitHub draft. On launch failure it
    /// logs GITHUB_DRAFT_FAIL and opens the plain issues page instead.
    /// </summary>
    /// <param name="feature">true for a feature request, false for a bug report.</param>
    void OpenGitHubDraft(bool feature)
    {
        var caption = feature ? TrayMenu.RequestFeatureLabel : TrayMenu.ReportBugLabel;
        var confirm = feature ? TrayMenu.RequestFeatureConfirm : TrayMenu.ReportBugConfirm;
        if (MessageBox.Show(confirm, caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
            != DialogResult.OK)
            return;

        try
        {
            var version = BugReport.AppVersion();
            var os = BugReport.OsDescription();
            string md;
            string title;
            string url;
            if (feature)
            {
                md = BugReport.FormatFeatureMarkdown(version, os);
                title = BugReport.FeatureTitle(version);
                url = BugReport.IssueUrl(title, md, "enhancement");
            }
            else
            {
                var rows = BugReport.Collect(_health, _deviceBatteries);
                var log = BugReport.ReadLogTail(Logger.LogPath, BugReport.LogTailLines);
                md = BugReport.FormatMarkdown(version, os, _config.Driver0323, rows, log);
                title = BugReport.IssueTitle(rows, version);
                url = BugReport.IssueUrl(title, md, "bug");
            }
            Clipboard.SetText(md);
            if (!OpenHelpUrl(url))
            {
                Logger.Log($"GITHUB_DRAFT_FAIL feature={feature} err=draft_launch_failed");
                OpenHelpUrl(TrayMenu.IssuesUrl);
                return;
            }
            Logger.Log(feature ? "FEATURE_DRAFT opened" : "BUG_REPORT opened");
        }
        catch (Exception ex)
        {
            Logger.Log($"GITHUB_DRAFT_FAIL feature={feature} err={ex.Message}");
            OpenHelpUrl(TrayMenu.IssuesUrl);
        }
    }

    static void AddDiagnosticScript(ToolStripMenuItem diagnostics, string label, string? path)
    {
        if (path is null) return;
        var item = new ToolStripMenuItem(label);
        item.Click += (_, _) => RunDiagnosticScript(path);
        diagnostics.DropDownItems.Add(item);
    }

    static void RunDiagnosticScript(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(DiagnosticScripts.StartInfo(path));
            Logger.Log($"DIAG_SCRIPT path={path}");
        }
        catch (Exception ex)
        {
            Logger.Log($"DIAG_SCRIPT_FAIL path={path} err={ex.Message}");
        }
    }

    static string? FindNotepadPlusPlus()
    {
        string[] candidates =
        {
            @"C:\Program Files\Notepad++\notepad++.exe",
            @"C:\Program Files (x86)\Notepad++\notepad++.exe",
        };
        foreach (var c in candidates)
            if (System.IO.File.Exists(c)) return c;
        return null;
    }

    void OnBatteryChanged(int pct, string name, DeviceKind kind, string pid)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _health = ReadHealth();

            if (string.IsNullOrEmpty(name))
            {
                // All-gone: Evaluate every known name with pct=-1 (AA death / v3 CloseModal)
                // before wiping the tray. Mixed drop is handled by AdaptivePoller's named -1.
                var dropped = new Dictionary<string, (DeviceKind Kind, string Pid)>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _deviceBatteries)
                    dropped[kv.Key] = (kv.Value.Kind, kv.Value.Pid);
                foreach (var kv in _lastGoodPct)
                {
                    if (dropped.ContainsKey(kv.Key)) continue;
                    var inferred = DeviceCapability.KindForName(kv.Key) ?? DeviceKind.MagicMouseV1;
                    dropped[kv.Key] = (inferred, string.Empty);
                }
                foreach (var kv in dropped)
                    ApplyBatteryAlert(-1, kv.Key, kv.Value.Kind, kv.Value.Pid);
                _deviceBatteries.Clear();
                if (_criticalAlert != null && !_criticalStayOnDisconnect)
                {
                    _criticalAlert.Close();
                    Logger.Log("CRITICAL_ALERT_CLOSED reason=no_devices");
                }
            }
            else
            {
                name = new string(name.Where(c => !char.IsControl(c)).ToArray());
                _deviceBatteries[name] = (pct, kind, pid);
                ApplyBatteryAlert(pct, name, kind, pid);
            }

            UpdateTrayIcon();
        });
    }

    void ApplyBatteryAlert(int pct, string name, DeviceKind kind, string pid)
    {
        if (!_config.IsDeviceEnabled(pid))
            return;

        var threshold = _config.GetThreshold(pid);
        int lastGood = _lastGoodPct.TryGetValue(name, out var lg) ? lg : int.MinValue;
        if (pct >= 0)
            _lastGoodPct[name] = pct;

        if (!_firedEvents.TryGetValue(name, out var fired))
            _firedEvents[name] = fired = new HashSet<string>(StringComparer.Ordinal);

        var hours = pct >= 0
            ? DrainRateTracker.GetHoursToEmpty(name, pct)
            : -1;
        var rateKnown = hours >= 0;
        BatteryAlertPolicy.RearmFired(fired, kind, pct, hours, rateKnown, threshold);

        if (pct > 1 && _criticalDevice == name && _criticalAlert != null)
        {
            _criticalAlert.Close();
            Logger.Log("CRITICAL_ALERT_CLOSED reason=rearm");
        }

        var decision = BatteryAlertPolicy.Evaluate(
            kind, name, pct, threshold, hours, rateKnown,
            DateTime.Now, lastGood, fired);

        if (decision.EventId != null &&
            decision.Action is BatteryAlertAction.Toast or BatteryAlertAction.Modal)
            fired.Add(decision.EventId);

        if (decision.Action == BatteryAlertAction.Toast &&
            decision.Title != null && decision.Body != null)
        {
            ToastNotifier.Show(decision.Title, decision.Body);
        }
        else if (decision.Action == BatteryAlertAction.Modal &&
                 decision.Body != null && _criticalAlert == null)
        {
            _criticalAlert = new CriticalAlert(decision.Body);
            _criticalAlert.FormClosed += (_, _) =>
            {
                _criticalAlert = null;
                _criticalDevice = null;
                _criticalStayOnDisconnect = false;
            };
            _criticalDevice = name;
            _criticalStayOnDisconnect = BatteryAlertPolicy.IsAaPowered(kind);
            _criticalAlert.Show();
            Logger.Log($"CRITICAL_ALERT_SHOWN device={name} pct={pct}");
        }

        if (BatteryAlertPolicy.ShouldCloseModal(decision.CloseModal, _criticalDevice, name)
            && _criticalAlert != null)
        {
            _criticalAlert.Close();
            Logger.Log("CRITICAL_ALERT_CLOSED reason=disconnect");
        }
    }


    void OnSystemVisualChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.Invoke(() => { RefreshTheme(); UpdateTrayIcon(); });

    bool AnyDriverAttention()
    {
        foreach (var h in _health)
        {
            if (!_config.IsDeviceEnabled(h.Pid)) continue;
            if (TrayMenu.IconAttention(h.Pid, h.Status))
                return true;
        }
        return false;
    }

    DeviceDriverHealth? FindHealth(string pid)
    {
        foreach (var h in _health)
        {
            if (TrayMenu.PidEq(h.Pid, pid))
                return h;
        }
        return null;
    }

    void UpdateTrayIcon()
    {
        int lowestPct = -1;
        string lowestName = string.Empty;
        foreach (var kv in _deviceBatteries)
        {
            if (!_config.IsDeviceEnabled(kv.Value.Pid)) continue;
            if (kv.Value.Pct < 0) continue;
            if (lowestPct < 0 || kv.Value.Pct < lowestPct) { lowestPct = kv.Value.Pct; lowestName = kv.Key; }
        }

        bool anyLow = false;
        foreach (var kv in _deviceBatteries)
        {
            if (!_config.IsDeviceEnabled(kv.Value.Pid)) continue;
            if (kv.Value.Pct >= 0 && kv.Value.Pct <= _config.GetThreshold(kv.Value.Pid))
                anyLow = true;
        }

        var newIcon = MakeIcon(lowestPct, anyLow, MarkerFor(lowestName), AnyDriverAttention());
        var oldIcon = _currentIcon;
        _tray.Icon = newIcon;
        _currentIcon = newIcon;
        oldIcon?.Dispose();

        string tip;
        if (_deviceBatteries.Count == 0)
        {
            tip = $"{TrayMenu.ProductName} — no devices detected";
        }
        else
        {
            var parts = _deviceBatteries.Select(kv =>
            {
                var pct = kv.Value.Pct;
                return $"{kv.Key}: {TrayMenu.BatteryText(pct)}";
            });
            var joined = string.Join(" | ", parts);
            tip = $"{joined} · {FormatInterval(_poller.LastInterval)}";
        }

        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        Logger.Log($"TRAY_UPDATE devices={_deviceBatteries.Count} lowest={lowestPct} tooltip=\"{_tray.Text}\"");
        UpdateDeviceMenuItems();
        UpdateBatteryReadsVisibility();
    }

    void UpdateDeviceMenuItems()
    {
        if (_deviceSection is null) return;
        var menu = _tray.ContextMenuStrip;
        if (menu is null) return;

        foreach (var item in _deviceMenuItems.Values)
            menu.Items.Remove(item);
        _deviceMenuItems.Clear();

        var shownPids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int insertAt = menu.Items.IndexOf(_deviceSection) + 1;

        if (_deviceBatteries.Count == 0)
        {
            _deviceSection.Text = "No devices detected";
            _deviceSection.Enabled = true;
            _deviceSection.DropDownItems.Clear();
            _deviceSection.DropDownItems.Add(new ToolStripMenuItem(
                "Pair a Magic Mouse or keyboard over Bluetooth, then Refresh Now") { Enabled = false });
        }
        else
        {
            _deviceSection.Enabled = false;
            _deviceSection.DropDownItems.Clear();
            _deviceSection.Text = _deviceBatteries.Count == 1
                ? "1 device"
                : $"{_deviceBatteries.Count} devices";
        }

        foreach (var kv in _deviceBatteries.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            shownPids.Add(kv.Value.Pid);
            var item = BuildDeviceRow(kv.Key, kv.Value.Pct, kv.Value.Kind, kv.Value.Pid);
            _deviceMenuItems[kv.Key] = item;
            menu.Items.Insert(insertAt++, item);
        }

        foreach (var h in _health)
        {
            if (!TrayMenu.ShouldShowHealthRow(shownPids, h.Pid, h.Status)) continue;

            if (h.Status == DriverStatus.UnknownAppleMouse)
            {
                var key = $"unknown:{h.Pid}";
                if (_deviceMenuItems.ContainsKey(key)) continue;
                var item = BuildUnknownRow(h);
                _deviceMenuItems[key] = item;
                menu.Items.Insert(insertAt++, item);
                shownPids.Add(h.Pid);
                continue;
            }

            if (!MouseBatteryDevice.TryKnownMouse(h.Pid, out var name, out var kind))
                continue;
            var healthKey = $"health:{h.Pid}";
            if (_deviceMenuItems.ContainsKey(healthKey)) continue;
            shownPids.Add(h.Pid);
            var row = BuildDeviceRow(name, -1, kind, h.Pid);
            _deviceMenuItems[healthKey] = row;
            menu.Items.Insert(insertAt++, row);
        }

        if (_deviceBatteries.Count == 0 && _deviceMenuItems.Count > 0)
        {
            _deviceSection.Text = _deviceMenuItems.Count == 1
                ? "1 device"
                : $"{_deviceMenuItems.Count} devices";
            _deviceSection.Enabled = false;
            _deviceSection.DropDownItems.Clear();
        }
    }

    ToolStripMenuItem BuildDeviceRow(string name, int pct, DeviceKind kind, string pid)
    {
        var health = FindHealth(pid);
        var status = health?.Status;


        string? badge = null;
        if (TrayMenu.IsV3(kind, pid))
            badge = TrayMenu.V3Badge(status);
        else if (TrayMenu.IsV1V2Mouse(kind))
            badge = TrayMenu.V1V2Badge(status);
        else if (status == DriverStatus.UnknownAppleMouse)
            badge = "Unknown model";

        var extras = new List<string>();
        var rate = DrainRateTracker.GetDrainRatePctPerHour(name);
        if (rate > 0.001) extras.Add($"{rate:F1}%/h");
        if (pct >= 0)
        {
            var hoursLeft = DrainRateTracker.GetHoursToEmpty(name, pct);
            if (hoursLeft > 24) extras.Add($"~{(hoursLeft / 24.0):F1}d");
            else if (hoursLeft > 0)
                extras.Add($"~{Math.Max(1, (int)Math.Round(hoursLeft))}h");
        }

        var item = new ToolStripMenuItem(TrayMenu.RowLabel(name, pct, badge, string.Join("  ", extras)));
        item.AccessibleName = string.IsNullOrEmpty(badge)
            ? $"{name}, {TrayMenu.BatteryText(pct)}"
            : $"{name}, {TrayMenu.BatteryText(pct)}, {badge}";

        if (status == DriverStatus.UnknownAppleMouse)
        {
            var warn = new ToolStripMenuItem("Unknown Apple mouse — check for an app update")
            {
                ForeColor = Color.OrangeRed
            };
            warn.Click += (_, _) => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(TrayMenu.ReleasesUrl) { UseShellExecute = true });
            item.DropDownItems.Add(warn);
        }


        if (TrayMenu.ShowFixKeyboard(kind, pct))
        {
            var kbdRec = TrayMenu.RecommendedLabel(kind, status, pct);
            if (kbdRec != null)
                item.DropDownItems.Add(new ToolStripMenuItem(kbdRec) { Enabled = false });
            var fix = new ToolStripMenuItem("Fix battery reads") { ForeColor = Color.OrangeRed };
            fix.Click += (_, _) => RunDriverAction(DriverInstaller.OfferKeyboardSdpPatch);
            item.DropDownItems.Add(fix);
        }

        if (TrayMenu.ShowTrackpadV1BootCamp(kind, pid))
        {
            var offerPid = pid;
            var bootCamp = new ToolStripMenuItem(TrayMenu.TrackpadV1BootCampLabel);
            bootCamp.Click += (_, _) =>
                RunDriverAction(() => DriverInstaller.OfferTrackpadV1BootCamp(offerPid));
            item.DropDownItems.Add(bootCamp);
        }

        if (TrayMenu.IsV3(kind, pid))
        {
            var driverMenu = new ToolStripMenuItem($"Driver: {TrayMenu.V3Badge(status)}");

            driverMenu.DropDownItems.Add(new ToolStripMenuItem(TrayMenu.BoundLabel(health?.BoundDriverName)) { Enabled = false });
            var rec = TrayMenu.RecommendedLabel(kind, status, pct);
            if (rec != null)
                driverMenu.DropDownItems.Add(new ToolStripMenuItem(rec) { Enabled = false });

            var kmdf = new ToolStripMenuItem(TrayMenu.V3RadioKmdf)
            {
                Checked = status == DriverStatus.PatchedKmdf
            };
            if (status != DriverStatus.PatchedKmdf)
                kmdf.Click += (_, _) => _ = RunDriverActionAsync(async () =>
                {
                    _config.SetDriver0323(Config.Driver0323Kmdf);
                    await DriverInstaller.OfferV3KmdfInstallAsync();
                });
            else
                kmdf.Enabled = false;
            driverMenu.DropDownItems.Add(kmdf);

            var patchedApple = new ToolStripMenuItem(TrayMenu.V3RadioPatchedApple)
            {
                Checked = status == DriverStatus.PathAPatched
            };
            if (status != DriverStatus.PathAPatched)
                patchedApple.Click += (_, _) => _ = RunDriverActionAsync(async () =>
                {
                    _config.SetDriver0323(Config.Driver0323PathA);
                    await DriverInstaller.OfferV3PathAInstallAsync();
                });
            else
                patchedApple.Enabled = false;
            driverMenu.DropDownItems.Add(patchedApple);

            var stock = new ToolStripMenuItem(TrayMenu.V3RadioStockWindows)
            {
                Checked = status == DriverStatus.StockKmdf
            };
            if (status != DriverStatus.StockKmdf)
                stock.Click += (_, _) => _ = RunDriverActionAsync(async () =>
                {
                    _config.SetDriver0323(Config.Driver0323Stock);
                    await DriverInstaller.OfferV3StockRestoreAsync();
                });
            else
                stock.Enabled = false;
            driverMenu.DropDownItems.Add(stock);

            if (TrayMenu.ShowPathAModeSwitch(kind, pid, status))
            {
                bool modeA = V3RecycleManager.IsV3InModeA();
                bool modeB = V3RecycleManager.IsV3InModeB();

                var scroll = new ToolStripMenuItem(TrayMenu.V3ModeRadioScroll)
                {
                    Checked = modeB,
                    Enabled = !modeB
                };
                if (!modeB)
                    scroll.Click += (_, _) => _ = RunDriverActionAsync(async () =>
                    {
                        _config.SetDriver0323(Config.Driver0323PathA);
                        await Task.Run(() => V3RecycleManager.SubmitFlipAndWait(V3RecycleManager.FlipPhase.AppleFilter));
                    });
                driverMenu.DropDownItems.Add(scroll);

                var battery = new ToolStripMenuItem(TrayMenu.V3ModeRadioBattery)
                {
                    Checked = modeA,
                    Enabled = !modeA
                };
                if (!modeA)
                    battery.Click += (_, _) => _ = RunDriverActionAsync(async () =>
                    {
                        _config.SetDriver0323(Config.Driver0323PathA);
                        await Task.Run(() => V3RecycleManager.SubmitFlipAndWait(V3RecycleManager.FlipPhase.NoFilter));
                    });
                driverMenu.DropDownItems.Add(battery);
            }

            item.DropDownItems.Add(driverMenu);
        }
        else if (TrayMenu.IsV1V2Mouse(kind))
        {
            var driverMenu = new ToolStripMenuItem($"Driver: {TrayMenu.V1V2Badge(status)}");

            var rec = TrayMenu.RecommendedLabel(kind, status, pct);
            driverMenu.DropDownItems.Add(new ToolStripMenuItem(TrayMenu.BoundLabel(health?.BoundDriverName)) { Enabled = false });
            if (rec != null)
                driverMenu.DropDownItems.Add(new ToolStripMenuItem(rec) { Enabled = false });

            var bootCamp = new ToolStripMenuItem(TrayMenu.V1V2RadioBootCamp)
            {
                Checked = status == DriverStatus.Ok
            };
            if (TrayMenu.V1V2BootCampRadioEnabled(status))
                bootCamp.Click += (_, _) => RunDriverAction(DriverInstaller.OfferV1V2ScrollFix);
            else
                bootCamp.Enabled = false;
            driverMenu.DropDownItems.Add(bootCamp);

            var stock = new ToolStripMenuItem(TrayMenu.V1V2RadioStockWindows)
            {
                Checked = status == DriverStatus.NotInstalled
            };
            var stockPid = pid;
            if (TrayMenu.V1V2StockRadioEnabled(status))
                stock.Click += (_, _) => RunDriverAction(() => DriverInstaller.OfferV1V2StockRestore(stockPid));
            else
                stock.Enabled = false;
            driverMenu.DropDownItems.Add(stock);

            item.DropDownItems.Add(driverMenu);
        }

        item.DropDownItems.Add(BuildEnabledOnThisPcItem(pid, name));
        item.DropDownItems.Add(new ToolStripSeparator());
        var thrMenu = new ToolStripMenuItem("Low battery alert");
        var currentThr = _config.GetThreshold(pid);
        foreach (var t in Config.ThresholdChoices)
        {
            var hours = DrainRateTracker.GetHoursToEmpty(name, t);
            var tItem = new ToolStripMenuItem(TrayMenu.DeviceThresholdLabel(t, hours))
            {
                Checked = t == currentThr,
                Tag = t,
            };
            var pidStr = pid;
            tItem.Click += (_, _) =>
            {
                _config.SetThreshold(pidStr, t);
                UpdateTrayIcon();
            };
            thrMenu.DropDownItems.Add(tItem);
        }
        item.DropDownItems.Add(thrMenu);

        return item;
    }

    ToolStripMenuItem BuildEnabledOnThisPcItem(string pid, string? nameForModal)
    {
        var item = new ToolStripMenuItem(TrayMenu.EnabledOnThisPc)
        {
            Checked = _config.IsDeviceEnabled(pid),
            CheckOnClick = true,
        };
        var pidStr = pid;
        item.Click += (_, _) =>
        {
            var want = item.Checked;
            var prompt = want
                ? "Allow this device on this PC again? Windows will start using it."
                : "Stop this device on this PC? It will not move the cursor here until you enable it again. It stays paired.";
            var confirm = System.Windows.Forms.MessageBox.Show(
                prompt, "Magic Tray",
                System.Windows.Forms.MessageBoxButtons.OKCancel,
                System.Windows.Forms.MessageBoxIcon.Warning);
            if (confirm != System.Windows.Forms.DialogResult.OK)
            {
                item.Checked = !want;
                return;
            }
            try
            {
                _config.SetDeviceEnabled(pidStr, want);
                DeviceEnable.Apply(pidStr, want);
                _poller.RefreshNow();
                if (!want
                    && !string.IsNullOrEmpty(nameForModal)
                    && string.Equals(_criticalDevice, nameForModal, StringComparison.OrdinalIgnoreCase)
                    && _criticalAlert != null)
                {
                    _criticalAlert.Close();
                    Logger.Log("CRITICAL_ALERT_CLOSED reason=disabled");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"DEVICE_ENABLE_FAIL pid={pidStr} err={ex.Message}");
                item.Checked = !want;
                _config.SetDeviceEnabled(pidStr, !want);
                System.Windows.Forms.MessageBox.Show(
                    "Could not change the device on this PC. UAC cancelled or the device action failed.",
                    "Magic Tray",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
            UpdateTrayIcon();
        };
        return item;
    }


    ToolStripMenuItem BuildUnknownRow(DeviceDriverHealth h)
    {
        var pid = string.IsNullOrEmpty(h.Pid) ? "unknown" : h.Pid.ToUpperInvariant();
        var item = new ToolStripMenuItem($"Unknown Apple mouse (PID {pid})")
        {
            ForeColor = Color.OrangeRed
        };
        item.AccessibleName = $"Unknown Apple mouse, PID {pid}";
        var warn = new ToolStripMenuItem("Check for an app update") { ForeColor = Color.OrangeRed };
        warn.Click += (_, _) => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(TrayMenu.ReleasesUrl) { UseShellExecute = true });
        item.DropDownItems.Add(warn);
        item.DropDownItems.Add(BuildEnabledOnThisPcItem(h.Pid, null));
        return item;
    }

    IReadOnlyList<DeviceDriverHealth> ReadHealth() =>
        DriverHealthChecker.GetPerDeviceStatus(_config.Driver0323);

    void RunDriverAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_ACTION_FAIL err={ex.Message}");
            ToastNotifier.ShowError(TrayMenu.ProductName, ex.Message);
        }
        _health = ReadHealth();
        _poller.RefreshNow();
        UpdateDeviceMenuItems();
    }

    async Task RunDriverActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_ACTION_FAIL err={ex.Message}");
            ToastNotifier.ShowError(TrayMenu.ProductName, ex.Message);
        }
        _health = ReadHealth();
        _poller.RefreshNow();
        System.Windows.Application.Current?.Dispatcher.Invoke(UpdateDeviceMenuItems);
    }

    static string FormatInterval(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalMinutes}m";

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnSystemVisualChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemVisualChanged;
        _poller.BatteryChanged -= OnBatteryChanged;
        _poller.Dispose();
        _criticalAlert?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _currentIcon?.Dispose();
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    enum Marker { Mouse, Keyboard }

    static Marker MarkerFor(string name) =>
        name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) ? Marker.Keyboard : Marker.Mouse;

    static Icon MakeIcon(int pct, bool isLow, Marker marker, bool driverMissing = false)
    {
        int S = SystemInformation.SmallIconSize.Width;
        float k = S / 16f;
        int R(float v) => (int)Math.Round(v * k, MidpointRounding.AwayFromZero);

        using var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.None;

        bool alert = pct >= 0 && (isLow || pct < 10);
        Color outline = alert ? FillColor(pct, isLow) : ThemeOutlineColor();

        int bx = R(1), by = R(3), bw = R(11), bh = R(7);
        int t = Math.Max(1, R(1));
        using (var ob = new SolidBrush(outline))
        {
            g.FillRectangle(ob, bx, by, bw, t);
            g.FillRectangle(ob, bx, by + bh - t, bw, t);
            g.FillRectangle(ob, bx, by, t, bh);
            g.FillRectangle(ob, bx + bw - t, by, t, bh);
            g.FillRectangle(ob, bx + bw, by + R(2), R(1.5f), R(3));
        }

        if (pct >= 0)
        {
            int tx = bx + t, ty = by + t, tw = bw - 2 * t, th = bh - 2 * t;
            int fw = pct == 0 ? 0 : Math.Min(tw, Math.Max(1, (int)Math.Round(tw * pct / 100.0)));
            if (fw > 0)
                using (var fb = new SolidBrush(FillColor(pct, isLow)))
                    g.FillRectangle(fb, tx, ty, fw, th);

            g.SmoothingMode = S >= 20 ? SmoothingMode.AntiAlias : SmoothingMode.None;
            DrawMarker(g, marker, outline, R);
            g.SmoothingMode = SmoothingMode.None;
        }

        if (driverMissing)
            using (var dot = new SolidBrush(Color.FromArgb(255, 220, 30)))
                g.FillRectangle(dot, S - R(3), 0, R(3), R(3));

        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    static Color FillColor(int pct, bool isLow) => (pct, isLow) switch
    {
        (_, true) => Color.FromArgb(255, 64, 13),
        (> 50, _) => Color.FromArgb(52, 199, 89),
        (>= 20, _) => Color.FromArgb(255, 204, 0),
        (>= 10, _) => Color.FromArgb(255, 149, 0),
        _ => Color.FromArgb(255, 59, 48),
    };

    static void DrawMarker(Graphics g, Marker m, Color color, Func<float, int> R)
    {
        using var b = new SolidBrush(color);
        var rect = m == Marker.Mouse
            ? new Rectangle(R(6), R(10), R(4), R(6))
            : new Rectangle(R(5), R(11), R(6), R(4));
        using var path = Capsule(rect);
        g.FillPath(b, path);
    }

    static GraphicsPath Capsule(Rectangle r)
    {
        var p = new GraphicsPath();
        if (r.Width <= 1 || r.Height <= 1) { p.AddRectangle(r); return p; }
        if (r.Height > r.Width)
        {
            int d = r.Width;
            p.AddArc(r.X, r.Y, d, d, 180, 180);
            p.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
        }
        else
        {
            int d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        }
        p.CloseFigure();
        return p;
    }

    static void RefreshTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _lightTaskbar = (key?.GetValue("SystemUsesLightTheme") as int? ?? 0) == 1;
        }
        catch { _lightTaskbar = false; }
    }

    static Color ThemeOutlineColor() =>
        _lightTaskbar ? Color.FromArgb(70, 70, 70) : Color.FromArgb(220, 220, 220);
}
