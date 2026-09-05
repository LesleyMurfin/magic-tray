// SPDX-License-Identifier: MIT
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MagicMouseTray;

/// <summary>
/// One row of the report device table: display name, PID, bound driver, battery text.
/// </summary>
internal readonly record struct BugReportDevice(
    string Name,
    string Pid,
    string Driver,
    string Battery);

/// <summary>
/// Builds a MAC-redacted diagnostic snapshot and a GitHub issue draft URL.
/// Does not call the GitHub API — the user submits the draft while logged in.
/// </summary>
internal static class BugReport
{
    internal const int LogTailLines = 40;
    internal const int MaxUrlChars = 7000;
    internal const string NewIssueBase = "https://github.com/LesleyMurfin/magic-tray/issues/new";

    /// <summary>
    /// Informational assembly version, falling back to the numeric version, then "unknown".
    /// </summary>
    internal static string AppVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "unknown";
    }

    /// <summary>Windows build string as reported by the runtime.</summary>
    internal static string OsDescription() => RuntimeInformation.OSDescription;

    /// <summary>
    /// Scrubs everything that identifies the machine or its owner: Bluetooth
    /// addresses, Windows profile paths, and the local account/host names. The
    /// result is pasted into a PUBLIC GitHub issue, so this is load-bearing.
    /// </summary>
    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        text = Regex.Replace(text, @"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", "<mac>");
        text = Regex.Replace(text, @"\bDev_[0-9A-Fa-f]{12}\b", "Dev_<mac>", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])", "<mac>");
        // Logged log/temp/script paths all carry C:\Users\<name>\AppData\...
        text = Regex.Replace(text, @"(?<=\\Users\\)[^\\\r\n]+?(?=\\|\r|\n|$)", "<user>",
            RegexOptions.IgnoreCase);
        foreach (var (pattern, placeholder) in LocalIdentifiers)
            text = pattern.Replace(text, placeholder);
        return text;
    }

    /// <summary>
    /// Literal account and host name patterns — those values have no shape to match.
    /// </summary>
    static readonly (Regex Pattern, string Placeholder)[] LocalIdentifiers = BuildLocalIdentifiers();

    /// <summary>
    /// Collects the local account and host names into replace patterns, skipping
    /// blanks and case-insensitive duplicates.
    /// </summary>
    static (Regex, string)[] BuildLocalIdentifiers()
    {
        var raw = new List<(string Value, string Placeholder)>(3);
        Add(SafeEnv(() => Environment.UserName), "<user>");
        Add(SafeEnv(() => Environment.MachineName), "<host>");
        Add(SafeEnv(() => Environment.UserDomainName), "<host>");
        // Longest first, so "lesley-pc" is not half-eaten by "lesley".
        return raw.OrderByDescending(v => v.Value.Length)
            .Select(v => (LocalIdentifierRegex(v.Value), v.Placeholder))
            .ToArray();

        void Add(string? value, string placeholder)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            foreach (var existing in raw)
                if (string.Equals(existing.Value, value, StringComparison.OrdinalIgnoreCase))
                    return;
            raw.Add((value, placeholder));
        }
    }

    /// <summary>
    /// Pattern redacting one local identifier. Values of 3+ chars replace as a
    /// substring; one- and two-character values such as "JD" or "PC" match only a
    /// whole delimited token, so "JSON" and "PCIe" stay intact.
    /// </summary>
    internal static Regex LocalIdentifierRegex(string value)
    {
        var escaped = Regex.Escape(value);
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (value.Length >= 3)
            return new Regex(escaped, options);
        return new Regex($@"(?<![A-Za-z0-9]){escaped}(?![A-Za-z0-9])", options);
    }

    /// <summary>Reads an environment value, returning null when the lookup throws.</summary>
    static string? SafeEnv(Func<string> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the last <paramref name="lines"/> lines of <paramref name="path"/>,
    /// redacted. Streams the file so a rolled 1 MB debug.log is never fully
    /// allocated, and returns "" for a missing or unreadable log.
    /// </summary>
    internal static string ReadLogTail(string path, int lines)
    {
        if (lines <= 0 || string.IsNullOrEmpty(path) || !File.Exists(path))
            return "";
        try
        {
            // debug.log rolls at MaxBytes and the tray calls this on the UI
            // thread, so stream it and hold only the last `lines` entries.
            // FileShare.ReadWrite: Logger may be appending while we read.
            var tail = new Queue<string>(lines);
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == lines)
                    tail.Dequeue();
                tail.Enqueue(line);
            }
            if (tail.Count == 0)
                return "";
            return Redact(string.Join(Environment.NewLine, tail));
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Merges driver health and battery readings into one report row per PID.
    /// </summary>
    internal static List<BugReportDevice> Collect(
        IReadOnlyList<DeviceDriverHealth> health,
        IReadOnlyDictionary<string, (int Pct, DeviceKind Kind, string Pid)> batteries)
    {
        var rows = new List<BugReportDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (batteries != null)
        {
            foreach (var kv in batteries.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var pid = (kv.Value.Pid ?? "").ToUpperInvariant();
                seen.Add(pid);
                var healthMatch = FindHealth(health, pid);
                rows.Add(new BugReportDevice(
                    Redact(kv.Key),
                    string.IsNullOrEmpty(pid) ? "?" : pid,
                    healthMatch?.Status.ToString() ?? "n/a",
                    BatteryText(kv.Value.Pct)));
            }
        }

        if (health != null)
        {
            foreach (var h in health)
            {
                var pid = (h.Pid ?? "").ToUpperInvariant();
                if (string.IsNullOrEmpty(pid) || !seen.Add(pid))
                    continue;
                var bound = string.IsNullOrEmpty(h.BoundDriverName) ? h.Status.ToString() : $"{h.Status} ({h.BoundDriverName})";
                rows.Add(new BugReportDevice("paired", pid, bound, "n/a"));
            }
        }

        return rows;
    }

    /// <summary>
    /// Renders the redacted bug-report body: environment, device table, and log tail.
    /// </summary>
    internal static string FormatMarkdown(
        string version,
        string os,
        string? driver0323,
        IReadOnlyList<BugReportDevice> devices,
        string logTail)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### What happened");
        sb.AppendLine();
        sb.AppendLine("(type here)");
        sb.AppendLine();
        sb.AppendLine("### Environment");
        sb.AppendLine();
        sb.AppendLine($"- Magic Tray: {version}");
        sb.AppendLine($"- Windows: {os}");
        if (!string.IsNullOrEmpty(driver0323))
            sb.AppendLine($"- 0323 lasting choice: `{driver0323}`");
        var hint = TroubleshootHint(devices);
        if (!string.IsNullOrEmpty(hint))
        {
            sb.AppendLine($"- Hint: {hint}");
        }
        sb.AppendLine();
        sb.AppendLine("### Devices");
        sb.AppendLine();
        sb.AppendLine("| Name | PID | Driver | Battery |");
        sb.AppendLine("|---|---|---|---|");
        if (devices == null || devices.Count == 0)
        {
            sb.AppendLine("| (none detected) | | | |");
        }
        else
        {
            foreach (var d in devices)
                sb.AppendLine($"| {d.Name} | `{d.Pid}` | {d.Driver} | {d.Battery} |");
        }
        sb.AppendLine();
        sb.AppendLine("### Log (last lines, MAC redacted)");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(string.IsNullOrEmpty(logTail) ? "(no debug.log)" : logTail.TrimEnd());
        sb.AppendLine("```");
        return Redact(sb.ToString());
    }

    /// <summary>
    /// Issue title naming the first device whose driver state is not healthy.
    /// </summary>
    internal static string IssueTitle(IReadOnlyList<BugReportDevice> devices, string version)
    {
        if (devices != null)
        {
            foreach (var d in devices)
            {
                if (!string.Equals(d.Driver, "Ok", StringComparison.Ordinal)
                    && !string.Equals(d.Driver, "n/a", StringComparison.Ordinal)
                    && !string.Equals(d.Driver, "PatchedKmdf", StringComparison.Ordinal))
                    return $"bug: PID {d.Pid} {d.Driver} (Magic Tray {version})";
            }
            if (devices.Count > 0)
                return $"bug: PID {devices[0].Pid} (Magic Tray {version})";
        }
        return $"bug: Magic Tray {version}";
    }

    /// <summary>Issue title for a feature request.</summary>
    internal static string FeatureTitle(string version) => $"feat: Magic Tray {version}";

    /// <summary>
    /// Renders the feature-request body: prompts plus version, with no diagnostics.
    /// </summary>
    internal static string FormatFeatureMarkdown(string version, string os)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Idea");
        sb.AppendLine();
        sb.AppendLine("(type here)");
        sb.AppendLine();
        sb.AppendLine("### Why it helps");
        sb.AppendLine();
        sb.AppendLine("(type here)");
        sb.AppendLine();
        sb.AppendLine("### Environment");
        sb.AppendLine();
        sb.AppendLine($"- Magic Tray: {version}");
        sb.AppendLine($"- Windows: {os}");
        return Redact(sb.ToString());
    }

    /// <summary>
    /// First self-help hint matching the collected devices, or "" when none applies.
    /// </summary>
    internal static string TroubleshootHint(IReadOnlyList<BugReportDevice>? devices)
    {
        if (devices == null)
            return "";
        foreach (var d in devices)
        {
            if (d.Pid.Equals("0323", StringComparison.OrdinalIgnoreCase)
                && d.Driver.Contains("Stock", StringComparison.OrdinalIgnoreCase))
                return "0323 is on stock Windows — pointer works; wheel needs KMDF (Test Mode + Memory Integrity off).";
            if (d.Driver.Contains("PathA", StringComparison.OrdinalIgnoreCase))
                return "Patched Apple: scroll and battery are mutually exclusive. KMDF is the path with both.";
            if (d.Driver.Contains("NotBound", StringComparison.OrdinalIgnoreCase)
                || d.Driver.Contains("NotInstalled", StringComparison.OrdinalIgnoreCase))
                return "Driver package is not bound. Use the tray Driver radio (confirm UAC).";
        }
        return "";
    }

    /// <summary>
    /// Bounded, fully percent-encoded ?labels=&amp;title=&amp;body= draft URL. Browsers
    /// and GitHub reject very long URLs, so both title and body are clipped to
    /// <see cref="MaxUrlChars"/> measured on the ENCODED text (the full report is
    /// on the clipboard).
    /// </summary>
    internal static string IssueUrl(string title, string body, string label = "bug")
    {
        const string note = "\n\n(truncated — full report is on the clipboard)";
        const string bodyKey = "&body=";
        var encodedNote = Uri.EscapeDataString(note);
        var head = $"{NewIssueBase}?labels={Uri.EscapeDataString(label)}&title=";

        // Clip the title first: an encoded title long enough to fill the cap on
        // its own leaves nothing for the body to give back, and the returned URL
        // would be one the browser refuses to open.
        var titleBudget = Math.Max(0, MaxUrlChars - head.Length - bodyKey.Length - encodedNote.Length);
        var clippedTitle = title ?? "";
        while (clippedTitle.Length > 0 && Uri.EscapeDataString(clippedTitle).Length > titleBudget)
            clippedTitle = clippedTitle[..(clippedTitle.Length * 3 / 4)];

        var prefix = head + Uri.EscapeDataString(clippedTitle) + bodyKey;
        var encoded = Uri.EscapeDataString(body ?? "");
        if (prefix.Length + encoded.Length <= MaxUrlChars)
            return prefix + encoded;

        // Reserve the note: clipping only the body would push past the cap.
        var budget = Math.Max(0, MaxUrlChars - prefix.Length - encodedNote.Length);
        var clipped = body ?? "";
        while (clipped.Length > 0 && Uri.EscapeDataString(clipped).Length > budget)
            clipped = clipped[..(clipped.Length * 3 / 4)];
        return prefix + Uri.EscapeDataString(clipped) + encodedNote;
    }

    /// <summary>Health entry for a PID, or null when the checker did not report it.</summary>
    static DeviceDriverHealth? FindHealth(IReadOnlyList<DeviceDriverHealth>? health, string pid)
    {
        if (health == null)
            return null;
        foreach (var h in health)
        {
            if (string.Equals(h.Pid, pid, StringComparison.OrdinalIgnoreCase))
                return h;
        }
        return null;
    }

    /// <summary>Battery cell text: a percentage, or "no reading" for sentinels.</summary>
    static string BatteryText(int pct) => pct < 0 ? "no reading" : $"{pct}%";
}
