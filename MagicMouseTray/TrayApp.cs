// SPDX-License-Identifier: MIT
using Microsoft.Win32;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MagicMouseTray;

// System tray icon and a simple right-click menu: device status, battery,
// start with Windows, quit. The tray does not install, bind, or repair drivers.
internal sealed class TrayApp : IDisposable
{
    readonly NotifyIcon _tray;
    readonly Config _config;
    readonly AdaptivePoller _poller;
    readonly ToolStripMenuItem _startupItem;

    // Per-device battery state, keyed by device name. Updated per poll event.
    readonly Dictionary<string, (int Pct, DeviceKind Kind, string Pid)> _deviceBatteries = new(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<string, ToolStripMenuItem> _deviceMenuItems = new(StringComparer.OrdinalIgnoreCase);
    ToolStripMenuItem? _deviceSection;
    ToolStripMenuItem? _driverWarningItem;
    ToolStripSeparator? _driverWarningSeparator;

    readonly Dictionary<string, HashSet<int>> _firedBoundaries = new(StringComparer.OrdinalIgnoreCase);

    CriticalAlert? _criticalAlert;

    DriverStatus _driverStatus;

    Icon? _currentIcon;

    static bool _lightTaskbar;

    ToolStripMenuItem? _updateItem;

    internal TrayApp(Config config)
    {
        _config = config;
        _startupItem = null!;

        _driverStatus = DriverHealthChecker.GetStatus();
        var menu = BuildMenu(out _startupItem);

        RefreshTheme();
        _currentIcon = MakeIcon(-1, false, Marker.Mouse, _driverStatus != DriverStatus.Ok);
        _tray = new NotifyIcon
        {
            Icon = _currentIcon,
            ContextMenuStrip = menu,
            Visible = true,
            Text = "Magic Tray — starting..."
        };

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemVisualChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemVisualChanged;

        _poller = new AdaptivePoller(_config);
        _poller.BatteryChanged += OnBatteryChanged;
        _poller.Start();

        if (_config.UpdateCheck)
        {
            _ = CheckForUpdateBackgroundAsync();
        }
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

    ContextMenuStrip BuildMenu(out ToolStripMenuItem startupItem)
    {
        var menu = new ContextMenuStrip();

        menu.Opening += (_, _) =>
        {
            _driverStatus = DriverHealthChecker.GetStatus();
            UpdateDeviceMenuItems();
            UpdateDriverWarningItem();
        };

        _deviceSection = new ToolStripMenuItem("Devices") { Enabled = false };
        menu.Items.Add(_deviceSection);
        menu.Items.Add(new ToolStripSeparator());

        // Unknown-model banner only. Missing/unbound filters are status text on
        // the device row — never an "install driver" or PowerShell action.
        _driverWarningItem = new ToolStripMenuItem("") { ForeColor = Color.OrangeRed };
        _driverWarningItem.Click += (_, _) =>
        {
            if (_driverWarningItem.Tag is string url)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        };
        _driverWarningSeparator = new ToolStripSeparator();
        menu.Items.Add(_driverWarningItem);
        menu.Items.Add(_driverWarningSeparator);
        UpdateDriverWarningItem();

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

        menu.Items.Add(new ToolStripSeparator());

        var refresh = new ToolStripMenuItem("Refresh battery");
        refresh.Click += (_, _) => _poller.RefreshNow();
        menu.Items.Add(refresh);

        var testToast = new ToolStripMenuItem("Test battery alert");
        testToast.Click += (_, _) =>
        {
            var (name, batt) = _deviceBatteries.FirstOrDefault(kv => kv.Value.Pct >= 0);
            ToastNotifier.Show(batt.Pct >= 0 ? batt.Pct : 15, name?.Length > 0 ? name : "Magic Mouse");
        };
        menu.Items.Add(testToast);

        var openLogs = new ToolStripMenuItem("Open logs");
        openLogs.Click += (_, _) => OpenLogsInEditor();
        menu.Items.Add(openLogs);

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
        menu.Items.Add(new ToolStripMenuItem($"Magic Tray {semver}") { Enabled = false });

        _updateItem = new ToolStripMenuItem("Update available") { Visible = false };
        _updateItem.Click += (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/LesleyMurfin/magic-tray/releases/latest") { UseShellExecute = true });
        };
        menu.Items.Add(_updateItem);

        return menu;
    }

    void UpdateDriverWarningItem()
    {
        if (_driverWarningItem == null || _driverWarningSeparator == null) return;

        if (_driverStatus != DriverStatus.UnknownAppleMouse)
        {
            _driverWarningItem.Visible = false;
            _driverWarningSeparator.Visible = false;
            return;
        }

        _driverWarningItem.Text = "Unknown Apple mouse — check for an app update";
        _driverWarningItem.Tag = "https://github.com/LesleyMurfin/magic-tray/releases";
        _driverWarningItem.Visible = true;
        _driverWarningSeparator.Visible = true;
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
            _driverStatus = DriverHealthChecker.GetStatus();

            if (string.IsNullOrEmpty(name))
            {
                _deviceBatteries.Clear();
                _firedBoundaries.Clear();
            }
            else
            {
                name = new string(name.Where(c => !char.IsControl(c)).ToArray());
                _deviceBatteries[name] = (pct, kind, pid);

                var threshold = _config.GetThreshold(pid);
                if (pct < 0 || pct > threshold)
                {
                    _firedBoundaries.Remove(name);
                }
                else
                {
                    if (!_firedBoundaries.TryGetValue(name, out var fired))
                        _firedBoundaries[name] = fired = new HashSet<int>();

                    var boundaries = threshold > 10
                        ? new[] { threshold, 10 }
                        : new[] { threshold };

                    foreach (var boundary in boundaries)
                    {
                        if (pct <= boundary && fired.Add(boundary))
                        {
                            ToastNotifier.Show(pct, name);
                            break;
                        }
                    }
                }

                const int CriticalPct = 1;
                if (pct >= 0 && pct <= CriticalPct && _criticalAlert == null)
                {
                    _criticalAlert = new CriticalAlert(pct, name);
                    _criticalAlert.FormClosed += (_, _) => _criticalAlert = null;
                    _criticalAlert.Show();
                    Logger.Log($"CRITICAL_ALERT_SHOWN device={name} pct={pct}");
                }
            }

            if (_criticalAlert != null && (_deviceBatteries.Count == 0 ||
                _deviceBatteries.Values.All(p => p.Pct < 0)))
            {
                _criticalAlert.Close();
                Logger.Log("CRITICAL_ALERT_CLOSED reason=no_devices");
            }

            UpdateTrayIcon();
        });
    }

    void OnSystemVisualChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.Invoke(() => { RefreshTheme(); UpdateTrayIcon(); });

    void UpdateTrayIcon()
    {
        int lowestPct = -1;
        string lowestName = string.Empty;
        foreach (var kv in _deviceBatteries)
        {
            if (kv.Value.Pct < 0) continue;
            if (lowestPct < 0 || kv.Value.Pct < lowestPct) { lowestPct = kv.Value.Pct; lowestName = kv.Key; }
        }

        bool anyLow = false;
        foreach (var kv in _deviceBatteries)
        {
            if (kv.Value.Pct >= 0 && kv.Value.Pct <= _config.GetThreshold(kv.Value.Pid))
                anyLow = true;
        }

        var newIcon = MakeIcon(lowestPct, anyLow, MarkerFor(lowestName), _driverStatus != DriverStatus.Ok);
        var oldIcon = _currentIcon;
        _tray.Icon = newIcon;
        _currentIcon = newIcon;
        oldIcon?.Dispose();

        string tip;
        if (_deviceBatteries.Count == 0)
        {
            tip = "Magic Tray — no devices detected";
        }
        else
        {
            var parts = _deviceBatteries.Select(kv =>
            {
                var pct = kv.Value.Pct;
                var pctStr = DeviceCapability.BatteryLabel(pct);
                return $"{kv.Key}: {pctStr}";
            });
            var joined = string.Join(" | ", parts);
            tip = $"{joined} · {FormatInterval(_poller.LastInterval)}";
            if (_driverStatus != DriverStatus.Ok && _driverStatus != DriverStatus.Error)
                tip = $"⚠ {tip}";
        }

        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        Logger.Log($"TRAY_UPDATE devices={_deviceBatteries.Count} lowest={lowestPct} tooltip=\"{_tray.Text}\"");
        UpdateDeviceMenuItems();
    }

    void UpdateDeviceMenuItems()
    {
        if (_deviceSection is null) return;

        if (_deviceBatteries.Count == 0)
        {
            _deviceSection.Text = "No devices detected";
            _deviceSection.Enabled = true;
            _deviceSection.DropDownItems.Clear();
            _deviceSection.DropDownItems.Add(new ToolStripMenuItem(
                "Pair a Magic Mouse or keyboard over Bluetooth, then Refresh battery") { Enabled = false });
            foreach (var key in _deviceMenuItems.Keys.ToList())
            {
                _tray.ContextMenuStrip?.Items.Remove(_deviceMenuItems[key]);
                _deviceMenuItems.Remove(key);
            }
            return;
        }

        _deviceSection.Enabled = false;
        _deviceSection.DropDownItems.Clear();

        foreach (var kv in _deviceBatteries)
        {
            var pct = kv.Value.Pct;
            var knd = DeviceCapability.KindForName(kv.Key) ?? kv.Value.Kind;
            var driverForRow = knd == DeviceKind.MagicMouseV3
                ? DriverHealthChecker.GetStatusForPid(kv.Value.Pid)
                : (knd is DeviceKind.MagicMouseV1 or DeviceKind.MagicMouseV2
                    ? DriverHealthChecker.GetStatusForPid(kv.Value.Pid)
                    : DriverStatus.Ok);
            var boundFilter = DriverHealthChecker.GetBoundFilterForPid(kv.Value.Pid);
            var row = DeviceCapability.Describe(knd, pct, driverForRow, boundFilter);

            var rate = DrainRateTracker.GetDrainRatePctPerHour(kv.Key);
            var extras = new List<string>();
            if (rate > 0.001) extras.Add($"{rate:F1}%/h");
            if (pct >= 0)
            {
                var hoursLeft = DrainRateTracker.GetHoursToThreshold(kv.Key, pct, 0);
                if (hoursLeft > 24) extras.Add($"~{(hoursLeft / 24.0):F1}d");
            }
            var extra = extras.Count > 0 ? "  " + string.Join("  ", extras) : "";
            var label = $"{kv.Key}: {DeviceCapability.BatteryLabel(pct)}{extra}";

            if (!_deviceMenuItems.TryGetValue(kv.Key, out var item))
            {
                item = new ToolStripMenuItem(label);
                _deviceMenuItems[kv.Key] = item;
                var sepIdx = _tray.ContextMenuStrip!.Items.IndexOf(_deviceSection) + 1;
                _tray.ContextMenuStrip.Items.Insert(sepIdx, item);
            }
            else
            {
                item.Text = label;
                item.Enabled = true;
                item.ForeColor = SystemColors.ControlText;
            }

            item.DropDownItems.Clear();
            item.DropDownItems.Add(new ToolStripMenuItem($"Status: {row.Status}") { Enabled = false });
            if (!string.IsNullOrEmpty(boundFilter))
                item.DropDownItems.Add(new ToolStripMenuItem($"Driver: {boundFilter}") { Enabled = false });
            else if (knd is DeviceKind.MagicMouseV1 or DeviceKind.MagicMouseV2 or DeviceKind.MagicMouseV3)
                item.DropDownItems.Add(new ToolStripMenuItem($"Driver: {DeviceCapability.DriverLabel(driverForRow, null)}") { Enabled = false });

            if (row.ActionLabel is { } al && row.ActionUrl is { } url)
            {
                var action = new ToolStripMenuItem(al);
                action.Click += (_, _) => System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                item.DropDownItems.Add(action);
            }

            item.DropDownItems.Add(new ToolStripSeparator());
            var thrMenu = new ToolStripMenuItem("Low battery alert");
            var currentThr = _config.GetThreshold(kv.Value.Pid);
            foreach (var t in new[] { 10, 15, 20, 25 })
            {
                var tItem = new ToolStripMenuItem($"{t}%") { Checked = t == currentThr };
                var pidStr = kv.Value.Pid;
                tItem.Click += (_, _) =>
                {
                    _config.SetThreshold(pidStr, t);
                    UpdateTrayIcon();
                };
                thrMenu.DropDownItems.Add(tItem);
            }
            item.DropDownItems.Add(thrMenu);
        }

        foreach (var key in _deviceMenuItems.Keys.Except(_deviceBatteries.Keys).ToList())
        {
            _tray.ContextMenuStrip?.Items.Remove(_deviceMenuItems[key]);
            _deviceMenuItems.Remove(key);
        }

        _deviceSection.Text = _deviceBatteries.Count == 1 ? "1 device" : $"{_deviceBatteries.Count} devices";
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
