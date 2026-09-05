// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

/// <summary>
/// Covers the diagnostic snapshot that ships to a PUBLIC GitHub issue: privacy
/// redaction, Markdown shape, title selection, log tailing, and draft URL bounds.
/// </summary>
public class BugReportTests
{
    /// <summary>
    /// Bluetooth MAC addresses and Dev_ ids are scrubbed while the 4-digit PID,
    /// which carries no identity and is needed to triage, survives.
    /// </summary>
    [Fact]
    public void Redact_ReplacesMacAndDevId_KeepsPid()
    {
        var raw = "DRIVER_CHECK pid=0x0323 bth=HidBth Dev_AABBCCDDEEFF aa:bb:cc:dd:ee:ff aabbccddeeff";
        var redacted = BugReport.Redact(raw);
        Assert.Contains("0323", redacted);
        Assert.DoesNotContain("AABBCCDDEEFF", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aa:bb:cc:dd:ee:ff", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<mac>", redacted);
        Assert.Contains("Dev_<mac>", redacted);
    }

    /// <summary>
    /// The bug body carries version, driver choice, and the device table, and never
    /// leaks the BTHENUM device instance path.
    /// </summary>
    [Fact]
    public void FormatMarkdown_HasEnvironmentAndDevices_NoDeviceId()
    {
        var devices = new[]
        {
            new BugReportDevice("Magic Mouse 2024", "0323", "StockKmdf", "30%"),
        };
        var md = BugReport.FormatMarkdown("1.1.0", "Windows 11", "kmdf", devices, "OK battery=30%");
        Assert.Contains("Magic Tray: 1.1.0", md);
        Assert.Contains("`0323`", md);
        Assert.Contains("StockKmdf", md);
        Assert.Contains("0323 lasting choice: `kmdf`", md);
        Assert.Contains("(type here)", md);
        Assert.DoesNotContain("BTHENUM", md);
    }

    /// <summary>
    /// A PID reported by both the battery poller and the health checker yields one
    /// merged row, with the device name redacted.
    /// </summary>
    [Fact]
    public void Collect_MergesBatteryAndHealth_WithoutDeviceId()
    {
        var health = new[]
        {
            new DeviceDriverHealth(@"BTHENUM\Dev_AABBCCDDEEFF", "0323", DriverStatus.PatchedKmdf, "MagicMouseDriver"),
        };
        var batteries = new Dictionary<string, (int Pct, DeviceKind Kind, string Pid)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Magic Mouse 2024"] = (42, DeviceKind.MagicMouseV3, "0323"),
        };
        var rows = BugReport.Collect(health, batteries);
        Assert.Single(rows);
        Assert.Equal("0323", rows[0].Pid);
        Assert.Equal("42%", rows[0].Battery);
        Assert.Equal(nameof(DriverStatus.PatchedKmdf), rows[0].Driver);
        Assert.DoesNotContain("AABB", rows[0].Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The title names the first unhealthy device, so a triager sees the fault and
    /// not a healthy sibling device.
    /// </summary>
    [Fact]
    public void IssueTitle_UsesFirstNonOkDriver()
    {
        var devices = new[]
        {
            new BugReportDevice("v1", "030D", "Ok", "80%"),
            new BugReportDevice("v3", "0323", "StockKmdf", "no reading"),
        };
        Assert.Equal("bug: PID 0323 StockKmdf (Magic Tray 1.1.0)", BugReport.IssueTitle(devices, "1.1.0"));
    }

    /// <summary>A 0323 on stock Windows is told it needs KMDF to get the wheel working.</summary>
    [Fact]
    public void TroubleshootHint_Stock0323_PointsAtKmdf()
    {
        var devices = new[] { new BugReportDevice("v3", "0323", "StockKmdf", "30%") };
        Assert.Contains("KMDF", BugReport.TroubleshootHint(devices));
    }

    /// <summary>
    /// A feature request carries the idea prompts and version but no diagnostics -
    /// no log tail is attached to a request that has no fault to diagnose.
    /// </summary>
    [Fact]
    public void FormatFeatureMarkdown_HasIdeaAndVersion_NoLog()
    {
        var md = BugReport.FormatFeatureMarkdown("1.1.0", "Windows 11");
        Assert.Contains("### Idea", md);
        Assert.Contains("Magic Tray: 1.1.0", md);
        Assert.DoesNotContain("debug.log", md);
        Assert.Equal("feat: Magic Tray 1.1.0", BugReport.FeatureTitle("1.1.0"));
    }

    /// <summary>A feature draft is labelled enhancement, never bug.</summary>
    [Fact]
    public void IssueUrl_Feature_UsesEnhancementLabel()
    {
        var url = BugReport.IssueUrl("feat: test", "idea", "enhancement");
        Assert.Contains("labels=enhancement", url);
        Assert.DoesNotContain("labels=bug", url);
    }

    /// <summary>
    /// The draft URL targets the new-issue endpoint with a bug label and a
    /// percent-encoded body, so GitHub pre-fills the form rather than mangling it.
    /// </summary>
    [Fact]
    public void IssueUrl_PrefillsBugLabelAndBody()
    {
        var url = BugReport.IssueUrl("bug: test", "hello body");
        Assert.StartsWith(BugReport.NewIssueBase, url);
        Assert.Contains("labels=bug", url);
        Assert.Contains("title=", url);
        Assert.Contains("body=", url);
        Assert.DoesNotContain("hello body", url);
        Assert.Contains(Uri.EscapeDataString("hello body"), url);
    }

    /// <summary>
    /// An oversized body is clipped so the URL stays within the length cap browsers
    /// and GitHub enforce, and the draft says it was truncated.
    /// </summary>
    [Fact]
    public void IssueUrl_LongBody_StaysUnderMax()
    {
        var huge = new string('x', 50_000);
        var url = BugReport.IssueUrl("bug: test", huge);
        Assert.True(url.Length <= BugReport.MaxUrlChars);
        Assert.Contains("truncated", url);
    }

    /// <summary>
    /// Only the requested number of trailing lines is returned, and the tail is
    /// redacted before it can reach the issue body.
    /// </summary>
    [Fact]
    public void ReadLogTail_ReturnsLastLines_Redacted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mm-bug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "debug.log");
        try
        {
            File.WriteAllLines(path, ["keep-me", "Dev_AABBCCDDEEFF", "tail"]);
            var tail = BugReport.ReadLogTail(path, 2);
            Assert.DoesNotContain("AABBCCDDEEFF", tail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("tail", tail);
            Assert.DoesNotContain("keep-me", tail);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    /// <summary>
    /// Windows profile paths and the live account and host names are scrubbed; these
    /// identify the reporter and their machine in a public issue.
    /// </summary>
    [Fact]
    public void Redact_ScrubsProfilePathAndLocalNames()
    {
        // Synthetic name: the profile-path rule must not need the live account.
        var path = BugReport.Redact(
            @"OPEN_DIAG path=C:\Users\mm-test-user\AppData\Local\MagicMouseTray\debug.log");
        Assert.Contains(@"C:\Users\<user>\AppData\Local\MagicMouseTray\debug.log", path);
        Assert.DoesNotContain("mm-test-user", path, StringComparison.OrdinalIgnoreCase);

        var user = Environment.UserName;
        var host = Environment.MachineName;
        var env = BugReport.Redact($"DIAG user={user} host={host}");
        if (!string.IsNullOrWhiteSpace(user))
            Assert.DoesNotContain(user, env, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(host))
            Assert.DoesNotContain(host, env, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One- and two-character account or host names ("JD", "PC") are redacted as whole
    /// delimited tokens only, so ordinary log words like "JSON" and "PCIe" survive.
    /// </summary>
    [Fact]
    public void LocalIdentifierRegex_ShortNames_MatchWholeTokenOnly()
    {
        var pc = BugReport.LocalIdentifierRegex("PC");
        Assert.Equal("host=<host>", pc.Replace("host=PC", "<host>"));
        Assert.Equal("PCIe", pc.Replace("PCIe", "<host>"));

        var jd = BugReport.LocalIdentifierRegex("JD");
        Assert.Equal("user=<user>", jd.Replace("user=JD", "<user>"));
        Assert.Equal("JSON", jd.Replace("JSON", "<user>"));

        var one = BugReport.LocalIdentifierRegex("X");
        Assert.Equal("user=<user>", one.Replace("user=X", "<user>"));
        Assert.Equal("Xtra", one.Replace("Xtra", "<user>"));
    }

    /// <summary>
    /// Names of three chars or more still replace as substrings, so a host embedded in
    /// a longer token ("lesley-pc") is still scrubbed.
    /// </summary>
    [Fact]
    public void LocalIdentifierRegex_LongNames_StillSubstring()
    {
        var re = BugReport.LocalIdentifierRegex("lesley");
        Assert.Equal("<user>-pc", re.Replace("lesley-pc", "<user>"));
    }

    /// <summary>
    /// The cap is enforced on the ENCODED body with room reserved for the truncation
    /// note, so an escape-heavy report cannot push the URL over the limit.
    /// </summary>
    [Fact]
    public void IssueUrl_EncodingHeavyBody_StaysUnderMaxWithNote()
    {
        // Every newline costs three encoded chars, so the cap must be measured
        // on the encoded text and must reserve room for the truncation note.
        var body = string.Concat(Enumerable.Repeat("line — one\n", 4000));
        var url = BugReport.IssueUrl("bug: test", body);
        Assert.True(url.Length <= BugReport.MaxUrlChars, $"url was {url.Length} chars");
        Assert.Contains(Uri.EscapeDataString("truncated"), url);
        Assert.DoesNotContain("\n", url);
        Assert.DoesNotContain(" ", url);
    }

    /// <summary>
    /// A title long enough to fill the cap on its own is clipped too. Trimming only
    /// the body would return a URL the browser refuses, dropping the user on the
    /// plain issues list instead of a pre-filled draft.
    /// </summary>
    [Fact]
    public void IssueUrl_LongTitle_StaysUnderMax()
    {
        var url = BugReport.IssueUrl(new string('t', 20_000), "hello body");
        Assert.True(url.Length <= BugReport.MaxUrlChars, $"url was {url.Length} chars");
        Assert.StartsWith(BugReport.NewIssueBase, url);
        Assert.Contains("labels=bug", url);
        Assert.Contains("&body=", url);

        // Encoding-heavy title: every space costs three encoded chars.
        var spaced = BugReport.IssueUrl(string.Concat(Enumerable.Repeat("bug title ", 2000)), "body");
        Assert.True(spaced.Length <= BugReport.MaxUrlChars, $"url was {spaced.Length} chars");
        Assert.DoesNotContain(" ", spaced);
    }

    /// <summary>
    /// A rolled 1 MB log returns exactly the last LogTailLines entries, proving the
    /// reader streams the file instead of materialising all of it.
    /// </summary>
    [Fact]
    public void ReadLogTail_LargeLog_ReturnsExactTail()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mm-bug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "debug.log");
        try
        {
            using (var writer = new StreamWriter(path))
                for (var i = 0; i < 5_000; i++)
                    writer.WriteLine($"line {i}");
            var lines = BugReport.ReadLogTail(path, BugReport.LogTailLines)
                .Split(Environment.NewLine);
            Assert.Equal(BugReport.LogTailLines, lines.Length);
            Assert.Equal($"line {5_000 - BugReport.LogTailLines}", lines[0]);
            Assert.Equal("line 4999", lines[^1]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }
}
