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
    // Set once the user has been sent to the repo to star it. Only drives the
    // tray label; the app never contacts GitHub to confirm the star.
    internal bool StarClicked { get; private set; } = false;
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
                else if (key == "star_clicked" && bool.TryParse(val, out bool sc))
                    cfg.StarClicked = sc;
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

    // The PID every Logitech interface reported before DeviceRegistry.ExtractPid gave each
    // product its own identity. It is not a PID any device can have now, so it is only ever read
    // out of a config.ini written by an older build.
    const string LegacyLogitechPid = "logi";

    // Whether the tray polls and shows this device. An absent key means enabled: a PID is only
    // written here when the user toggles it, so "never toggled" cannot mean "switched off".
    //
    // kind exists for one upgrade. Every Logitech interface used to report Pid "logi", so a user
    // who turned EnableThirdParty on and then unchecked their Logitech row holds enabled_logi=false
    // and nothing else; after the per-product split that key matches no PID, and the device they
    // switched off would be polled and shown again. One vendor-wide key could only ever have said
    // "do not poll my Logitech hardware", so it keeps suppressing every Logitech product until the
    // user says otherwise per product. It can only ever suppress - a legacy true, like an absent
    // key, is the ordinary default - and an explicit enabled_<pid> line wins in both directions,
    // being the newer and the more specific statement. A non-Logitech kind is never affected, and
    // neither is a caller that does not know the kind, which is why kind is optional: the gate that
    // matters (AdaptivePoller.ShouldSkipPid) has it in scope, and the rest compile unchanged.
    //
    // Persist keeps the legacy line as it keeps any other entry, which is the wanted behaviour:
    // which Logitech products this user owns is not knowable from the file, so expanding the key
    // into per-product lines is guesswork and dropping it re-enables the rest of them.
    internal bool IsDeviceEnabled(string pid, DeviceKind? kind = null)
    {
        if (_deviceEnabled.TryGetValue(pid, out var enabled)) return enabled;
        if (kind == DeviceKind.LogitechMouse
            && _deviceEnabled.TryGetValue(LegacyLogitechPid, out var vendorWide))
            return vendorWide;
        return true;
    }

    // "Is there an explicit enabled_<pid> line for this PID?" - not what it says.
    // Absent means the device was never toggled here, which the repair planner
    // reads as "this PC does not own it" and stays quiet about. Same dictionary,
    // same case-insensitive keying as IsDeviceEnabled / SetDeviceEnabled.
    // The legacy vendor-wide "logi" key deliberately does NOT answer for a
    // Logitech product here, even though IsDeviceEnabled consults it: a key
    // written against a different identity is not the user toggling THIS pid,
    // so it cannot claim this PC owns this device.
    internal bool HasDeviceEnabledEntry(string pid) => _deviceEnabled.ContainsKey(pid);

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

    internal void SetStarClicked(bool value)
    {
        if (StarClicked == value) return;
        StarClicked = value;
        Persist();
        Logger.Log($"CONFIG star_clicked={value}");
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
                $"update_check={UpdateCheck.ToString().ToLower()}",
                $"star_clicked={StarClicked.ToString().ToLower()}"
            };
            if (!string.IsNullOrEmpty(Driver0323))
                lines.Add($"driver_0323={Driver0323}");
            foreach (var kv in Thresholds)
                lines.Add($"threshold_{kv.Key}={kv.Value}");
            foreach (var kv in _deviceEnabled)
                lines.Add($"enabled_{kv.Key}={kv.Value.ToString().ToLower()}");
            File.WriteAllLines(_configPath, lines);
        }
        // Best effort by design - a config write that fails must never take the tray down,
        // and the in-memory value stays authoritative for this session. It is recorded
        // because the symptom otherwise is a setting that silently forgets itself across
        // restarts, with nothing in the log to say the write was the thing that failed.
        catch (Exception ex)
        {
            Logger.Log($"CONFIG_SAVE_FAILED path=\"{_configPath}\" err=\"{ex.Message}\"");
        }
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
        // Best effort by design - Run-key access is routinely denied by policy on managed
        // machines and that must not block startup. Recorded because the symptom is a
        // start-with-Windows checkbox that reads as set and does nothing.
        catch (Exception ex)
        {
            Logger.Log($"CONFIG_STARTUP_FAILED enable={enable.ToString().ToLower()} err=\"{ex.Message}\"");
        }
    }
}
