// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class BugReportTests
{
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

    [Fact]
    public void TroubleshootHint_Stock0323_PointsAtKmdf()
    {
        var devices = new[] { new BugReportDevice("v3", "0323", "StockKmdf", "30%") };
        Assert.Contains("KMDF", BugReport.TroubleshootHint(devices));
    }

    [Fact]
    public void FormatFeatureMarkdown_HasIdeaAndVersion_NoLog()
    {
        var md = BugReport.FormatFeatureMarkdown("1.1.0", "Windows 11");
        Assert.Contains("### Idea", md);
        Assert.Contains("Magic Tray: 1.1.0", md);
        Assert.DoesNotContain("debug.log", md);
        Assert.Equal("feat: Magic Tray 1.1.0", BugReport.FeatureTitle("1.1.0"));
    }

    [Fact]
    public void IssueUrl_Feature_UsesEnhancementLabel()
    {
        var url = BugReport.IssueUrl("feat: test", "idea", "enhancement");
        Assert.Contains("labels=enhancement", url);
        Assert.DoesNotContain("labels=bug", url);
    }

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

    [Fact]
    public void IssueUrl_LongBody_StaysUnderMax()
    {
        var huge = new string('x', 50_000);
        var url = BugReport.IssueUrl("bug: test", huge);
        Assert.True(url.Length <= BugReport.MaxUrlChars);
        Assert.Contains("truncated", url);
    }

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
        if (user.Length >= 3)
            Assert.DoesNotContain(user, env, StringComparison.OrdinalIgnoreCase);
        if (host.Length >= 3)
            Assert.DoesNotContain(host, env, StringComparison.OrdinalIgnoreCase);
    }

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
