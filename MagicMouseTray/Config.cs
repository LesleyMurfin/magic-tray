// SPDX-License-Identifier: MIT
using System.IO;
using Microsoft.Win32;

namespace MagicMouseTray;

// Persists user settings to %APPDATA%\MagicMouseTray\config.ini.
// Start-with-Windows is stored in HKCU Run registry key (not Startup folder)
// because ProcessPath is available without install and needs no shortcut logic.
internal sealed class Config
{
    static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagicMouseTray", "config.ini");

    const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "MagicMouseTray";

    internal int GlobalThreshold { get; private set; } = 10;
    // Percent picker: 10%, 5%, 1% going down. Time alerts are not a radio.
    internal static readonly int[] ThresholdChoices = { 10, 5, 1 };
    internal Dictionary<string, int> Thresholds { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    internal bool StartWithWindows { get; private set; }
    // Loaded from older config.ini files. Recycle is gone; the tray ignores this flag.
    internal bool EnableV3Recycle { get; private set; } = false;
    internal bool EnableThirdParty { get; private set; } = false;
    internal bool UpdateCheck { get; private set; } = true;
    // Lasting 0323 radio: kmdf | pathA | stock. PathA survives Mode A (no Apple LowerFilters).
    internal const string Driver0323Kmdf = "kmdf";
    internal const string Driver0323PathA = "pathA";
    internal const string Driver0323Stock = "stock";
    internal string? Driver0323 { get; private set; }
    readonly string _configPath;
    readonly Dictionary<string, bool> _deviceEnabled = new(StringComparer.OrdinalIgnoreCase);

    Config(string? path = null) => _configPath = path ?? ConfigPath;

    internal static Config Load() => Load(ConfigPath);

    internal static Config Load(string path)
    {
        var cfg = new Config(path);
        if (!File.Exists(path)) return cfg;

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();

                if (key == "threshold" && int.TryParse(val, out int t) && IsValid(t))
                    cfg.GlobalThreshold = t;
                else if (key.StartsWith("threshold_") && int.TryParse(val, out int t2) && IsValid(t2))
                    cfg.Thresholds[key.Substring(10)] = t2;
                else if (key.StartsWith("enabled_") && bool.TryParse(val, out bool en))
                    cfg._deviceEnabled[key.Substring(8)] = en;
                else if (key == "start_with_windows" && bool.TryParse(val, out bool s))
                    cfg.StartWithWindows = s;
                else if (key == "enable_v3_recycle" && bool.TryParse(val, out bool r))
                    cfg.EnableV3Recycle = r;
                else if (key == "enable_third_party" && bool.TryParse(val, out bool tp))
                    cfg.EnableThirdParty = tp;
                else if (key == "update_check" && bool.TryParse(val, out bool uc))
                    cfg.UpdateCheck = uc;
                else if (key == "driver_0323")
                {
                    var choice = ParseDriver0323(val);
                    if (choice is not null)
                        cfg.Driver0323 = choice;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"CONFIG_LOAD_FAILED err=\"{ex.Message}\"");
        }
        return cfg;
    }

    internal int GetThreshold(string pid) => Thresholds.TryGetValue(pid, out var t) ? t : GlobalThreshold;

    internal bool IsDeviceEnabled(string pid) =>
        !_deviceEnabled.TryGetValue(pid, out var enabled) || enabled;

    internal void SetDeviceEnabled(string pid, bool value)
    {
        _deviceEnabled[pid] = value;
        Persist();
        Logger.Log($"CONFIG enabled pid={pid} val={value.ToString().ToLower()}");
    }

    internal void SetThreshold(string pid, int value)
    {
        if (!IsValid(value)) return;
        Thresholds[pid] = value;
        Persist();
        Logger.Log($"CONFIG threshold pid={pid} val={value}");
    }

    internal void SetGlobalThreshold(int value)
    {
        if (!IsValid(value)) return;
        GlobalThreshold = value;
        Persist();
        Logger.Log($"CONFIG threshold val={value}");
    }

    internal void SetStartWithWindows(bool value)
    {
        StartWithWindows = value;
        Persist();
        ApplyStartup(value);
        Logger.Log($"CONFIG start_with_windows={value}");
    }

    internal void SetEnableV3Recycle(bool value)
    {
        EnableV3Recycle = value;
        Persist();
        Logger.Log($"CONFIG enable_v3_recycle={value}");
    }

    internal void SetEnableThirdParty(bool value)
    {
        EnableThirdParty = value;
        Persist();
        Logger.Log($"CONFIG enable_third_party={value}");
    }

    internal void SetDriver0323(string value)
    {
        var choice = ParseDriver0323(value);
        if (choice is null) return;
        Driver0323 = choice;
        Persist();
        Logger.Log($"CONFIG driver_0323={choice}");
    }

    internal static string? ParseDriver0323(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Equals(Driver0323Kmdf, StringComparison.OrdinalIgnoreCase)) return Driver0323Kmdf;
        if (value.Equals(Driver0323PathA, StringComparison.OrdinalIgnoreCase)) return Driver0323PathA;
        if (value.Equals(Driver0323Stock, StringComparison.OrdinalIgnoreCase)) return Driver0323Stock;
        return null;
    }

    internal static bool IsPathALastingChoice(string? value) =>
        ParseDriver0323(value) == Driver0323PathA;

    internal static bool IsValid(int t) => t is 10 or 5 or 1;

    void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            var lines = new List<string>
            {
                $"threshold={GlobalThreshold}",
                $"start_with_windows={StartWithWindows.ToString().ToLower()}",
                $"enable_v3_recycle={EnableV3Recycle.ToString().ToLower()}",
                $"enable_third_party={EnableThirdParty.ToString().ToLower()}",
                $"update_check={UpdateCheck.ToString().ToLower()}"
            };
            if (!string.IsNullOrEmpty(Driver0323))
                lines.Add($"driver_0323={Driver0323}");
            foreach (var kv in Thresholds)
                lines.Add($"threshold_{kv.Key}={kv.Value}");
            foreach (var kv in _deviceEnabled)
                lines.Add($"enabled_{kv.Key}={kv.Value.ToString().ToLower()}");
            File.WriteAllLines(_configPath, lines);
        }
        catch { }
    }

    static void ApplyStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enable)
                key.SetValue(AppName, Environment.ProcessPath ?? string.Empty);
            else
                key.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch { }
    }
}
