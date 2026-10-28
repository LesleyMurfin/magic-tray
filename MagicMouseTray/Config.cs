// SPDX-License-Identifier: MIT
using System.IO;
using Microsoft.Win32;

namespace MagicMouseTray;

// One learned battery answer for one PID: which collection of the device answered, which
// channel it answered over, and the report id that was used. Persisted as
// "battery_channel_<pid>=<collection>:<channel>:0x<rid>" so a mouse this app has already
// figured out is read with a single HID call instead of a probe walk on every poll.
//
// Keyed on PID plus collection TOKEN, never on the interface path: a path carries the
// Bluetooth MAC and the instance ids Windows re-mints on re-pairing, while "col02" is the
// same string across reboots, re-pairings and USB-versus-Bluetooth transports.
internal readonly record struct BatteryChannelAnswer(string Collection, string Channel, byte ReportId)
{
    // HID Input report 0x90 read with HidD_GetInputReport, percent at buf[2]. The vendor
    // collection channel (usage page 0xFF00, usage 0x0014).
    internal const string Input90 = "input90";
    // HID Feature report read with HidD_GetFeature at the id the descriptor declares for
    // Generic Device Controls (0x0006) Battery Strength (0x0020), percent at buf[1].
    internal const string Feature47 = "feature47";
    // The interface path carries no &col fragment at all - the 030D shape: one interface,
    // battery report on the mouse collection itself.
    internal const string NoCollection = "none";

    // The persisted form. Written by Config, read back by Parse, and quoted verbatim by the
    // user-run capture script, so all three agree on one spelling.
    internal string Value => $"{Collection}:{Channel}:0x{ReportId:X2}";

    // Defensive by contract: config.ini is a plain text file a user may edit and an older
    // build may have written. An unparseable, unknown-channel or unknown-collection value is
    // IGNORED - it must never throw and never take Config.Load down with it.
    internal static BatteryChannelAnswer? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(':');
        if (parts.Length != 3) return null;
        var collection = NormalizeCollection(parts[0]);
        var channel = NormalizeChannel(parts[1]);
        var rid = ParseReportId(parts[2]);
        if (collection is null || channel is null || rid is null) return null;
        return new BatteryChannelAnswer(collection, channel, rid.Value);
    }

    // "none", or "col" followed by at least one digit. Anything else is a collection token
    // this app never wrote and cannot match against a live interface path.
    internal static string? NormalizeCollection(string? token)
    {
        var t = token?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(t)) return null;
        if (t == NoCollection) return NoCollection;
        if (!t.StartsWith("col", StringComparison.Ordinal) || t.Length == 3) return null;
        for (int i = 3; i < t.Length; i++)
            if (!char.IsAsciiDigit(t[i])) return null;
        return t;
    }

    internal static string? NormalizeChannel(string? token)
    {
        var t = token?.Trim().ToLowerInvariant();
        if (t == Input90) return Input90;
        if (t == Feature47) return Feature47;
        return null;
    }

    // Always hex, with or without the 0x prefix the app itself writes: a report id is only
    // ever quoted in hex in this project (0x90, 0x47), so "47" must not decode as 47 decimal.
    static byte? ParseReportId(string? token)
    {
        var t = token?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return byte.TryParse(t, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var rid) ? rid : null;
    }
}

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
    // config.ini key prefix for a learned battery answer, one line per PID.
    internal const string BatteryChannelKeyPrefix = "battery_channel_";
    // Learned battery answers by PID. Unlike every other setting here this one is written
    // from the POLL thread (the battery reader learns and forgets as devices answer), while
    // the rest of Config is written from the UI thread.
    //
    // _gate therefore covers THREE dictionaries, not one. Persist() enumerates Thresholds
    // and _deviceEnabled as well, and since Persist() can now run on the poll thread, every
    // writer of any of the three has to hold _gate - otherwise the enumeration throws into
    // the bare catch in Persist() and the whole save is dropped with no log line.
    // Still unguarded, and pre-existing: the READS in GetThreshold / IsDeviceEnabled, which
    // AdaptivePoller calls from the poll thread. That race predates the learned channel and
    // is not closed here, so do not read the lock below as a clean bill of health.
    readonly object _gate = new();
    readonly Dictionary<string, BatteryChannelAnswer> _batteryChannel = new(StringComparer.OrdinalIgnoreCase);
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
                else if (key.StartsWith(BatteryChannelKeyPrefix, StringComparison.Ordinal))
                {
                    var answer = BatteryChannelAnswer.Parse(val);
                    if (answer is not null)
                        cfg._batteryChannel[key[BatteryChannelKeyPrefix.Length..]] = answer.Value;
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

    // "Is there an explicit enabled_<pid> line for this PID?" - not what it says.
    // Absent means the device was never toggled here, which the repair planner
    // reads as "this PC does not own it" and stays quiet about. Same dictionary,
    // same case-insensitive keying as IsDeviceEnabled / SetDeviceEnabled.
    internal bool HasDeviceEnabledEntry(string pid) => _deviceEnabled.ContainsKey(pid);

    internal void SetDeviceEnabled(string pid, bool value)
    {
        // _gate, not because two UI callers race each other, but because Persist() enumerates
        // this dictionary and since the learned battery channel landed, Persist() also runs on
        // the poll thread (BatteryChannelMemory -> SetBatteryChannel -> Persist, off
        // AdaptivePoller.ReadBatteryGuarded's Task.Run). An unguarded write here throws out of
        // that enumeration and the bare catch in Persist swallows the whole save. Monitor is
        // reentrant, so the nested Persist() lock is free.
        lock (_gate)
        {
            _deviceEnabled[pid] = value;
            Persist();
        }
        Logger.Log($"CONFIG enabled pid={pid} val={value.ToString().ToLower()}");
    }

    internal void SetThreshold(string pid, int value)
    {
        if (!IsValid(value)) return;
        // Same reason as SetDeviceEnabled: Persist() enumerates Thresholds on the poll thread.
        lock (_gate)
        {
            Thresholds[pid] = value;
            Persist();
        }
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

    // The learned battery answer for a PID, or null when this app has not figured that device
    // out yet - or has just forgotten a stale one.
    internal BatteryChannelAnswer? GetBatteryChannel(string pid)
    {
        lock (_gate)
            return _batteryChannel.TryGetValue(pid, out var answer) ? answer : null;
    }

    // True only when the stored answer actually CHANGED. The poll loop calls this after every
    // successful battery read - once every 30 seconds at the fastest - and config.ini must not
    // be rewritten on a tick that learned nothing new.
    internal bool SetBatteryChannel(string pid, BatteryChannelAnswer answer)
    {
        lock (_gate)
        {
            if (_batteryChannel.TryGetValue(pid, out var current) && current == answer) return false;
            _batteryChannel[pid] = answer;
            Persist();
        }
        Logger.Log($"CONFIG battery_channel pid={pid} val={answer.Value}");
        return true;
    }

    // True only when there was something to forget, so the caller logs one relearn, not one
    // per poll. The expected answer is passed in so a stale caller - an abandoned poll read
    // that outlived its cycle and spends its third strike late - cannot delete an answer
    // learned after it started.
    internal bool ClearBatteryChannel(string pid, BatteryChannelAnswer expected)
    {
        lock (_gate)
        {
            if (!_batteryChannel.TryGetValue(pid, out var current) || current != expected) return false;
            _batteryChannel.Remove(pid);
            Persist();
        }
        Logger.Log($"CONFIG battery_channel pid={pid} val=cleared");
        return true;
    }

    internal static bool IsValid(int t) => t is 10 or 5 or 1;

    // Locked: the battery reader persists a learned answer from the poll thread while the
    // menu persists a threshold from the UI thread, and two threads writing config.ini at
    // once would leave a half-written file behind. Monitor is reentrant, so the callers
    // above may hold _gate already - and they must, because the three foreach loops below
    // enumerate Thresholds, _deviceEnabled and _batteryChannel. A write to any of them from
    // another thread mid-enumeration throws InvalidOperationException into the catch below,
    // which drops the entire save silently.
    void Persist()
    {
        lock (_gate)
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
                foreach (var kv in _batteryChannel)
                    lines.Add($"{BatteryChannelKeyPrefix}{kv.Key}={kv.Value.Value}");
                File.WriteAllLines(_configPath, lines);
            }
            catch { }
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
        catch { }
    }
}
