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

    internal int GlobalThreshold { get; private set; } = 20;
    internal Dictionary<string, int> Thresholds { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    internal bool StartWithWindows { get; private set; }
    // Retained so older config.ini files still load. The tray no longer flips
    // LowerFilters; this flag is ignored by the UI and poller.
    internal bool EnableV3Recycle { get; private set; } = false;
    internal bool EnableThirdParty { get; private set; } = false;
    internal bool UpdateCheck { get; private set; } = true;

    internal static Config Load()
    {
        var cfg = new Config();
        if (!File.Exists(ConfigPath)) return cfg;

        try
        {
            foreach (var line in File.ReadAllLines(ConfigPath))
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();

                if (key == "threshold" && int.TryParse(val, out int t) && IsValid(t))
                    cfg.GlobalThreshold = t;
                else if (key.StartsWith("threshold_") && int.TryParse(val, out int t2) && IsValid(t2))
                    cfg.Thresholds[key.Substring(10)] = t2;
                else if (key == "start_with_windows" && bool.TryParse(val, out bool s))
                    cfg.StartWithWindows = s;
                else if (key == "enable_v3_recycle" && bool.TryParse(val, out bool r))
                    cfg.EnableV3Recycle = r;
                else if (key == "enable_third_party" && bool.TryParse(val, out bool tp))
                    cfg.EnableThirdParty = tp;
                else if (key == "update_check" && bool.TryParse(val, out bool uc))
                    cfg.UpdateCheck = uc;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"CONFIG_LOAD_FAILED err=\"{ex.Message}\"");
        }
        return cfg;
    }

    internal int GetThreshold(string pid) => Thresholds.TryGetValue(pid, out var t) ? t : GlobalThreshold;

    internal void SetThreshold(string pid, int value)
    {
        if (!IsValid(value)) return;
        Thresholds[pid] = value;
        Persist();
        Logger.Log($"CONFIG threshold pid={pid} val={value}");
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

    static bool IsValid(int t) => t is 10 or 15 or 20 or 25;

    void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var lines = new List<string>
            {
                $"threshold={GlobalThreshold}",
                $"start_with_windows={StartWithWindows.ToString().ToLower()}",
                $"enable_v3_recycle={EnableV3Recycle.ToString().ToLower()}",
                $"enable_third_party={EnableThirdParty.ToString().ToLower()}",
                $"update_check={UpdateCheck.ToString().ToLower()}"
            };
            foreach (var kv in Thresholds)
                lines.Add($"threshold_{kv.Key}={kv.Value}");
            File.WriteAllLines(ConfigPath, lines);
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
