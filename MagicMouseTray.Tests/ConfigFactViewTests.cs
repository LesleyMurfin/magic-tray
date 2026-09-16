// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// ConfigFactView is the only thing that decides how a configuration fact is
// allowed to look. Two failures matter enough to be pinned here:
//
//   1. nagging a healthy PC - a machine whose every check passed must not be
//      handed a line that reads like a complaint at a glance;
//   2. a configuration advisory dressing itself up as a device fault - the
//      repair row owns the bare fault voice, so every line this view can emit
//      stays behind the "System config: " prefix, and Blocking sorts above
//      Advisory inside the section.
//
// The severity judgement itself belongs to SystemConfigChecker and is tested in
// SystemConfigCheckerTests; the healthy and blocked machines below are driven
// through the real Evaluate so the rendering is proven against facts the
// checker actually produces rather than against hand-written ones.
public class ConfigFactViewTests
{
    static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    static DeviceDiagReader.F1WatcherState HealthyWatcher =>
        new(Installed: true, LastHeartbeatUtc: T0.AddMinutes(-2), LastF1Ok: true);

    // The reference PC: 0323 on the KMDF filter, Test Mode on, HVCI off,
    // package installed, watcher reporting. Every fact is Ok.
    static IReadOnlyList<ConfigFact> HealthyFacts() =>
        SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            new SystemConfigChecker.ConfigReadings(
                TestSigningOn: true, HvciOn: false, PackagePresent: true,
                Watcher: HealthyWatcher, NowUtc: T0));

    // Same PC with test signing off, and the bound driver's signature read as
    // self-issued: exactly one Blocking fact (Test Mode). The signature
    // evidence is what makes it Blocking - the checker keys the signing gate on
    // BoundDriverSelfSigned, never on the PID - so it is spelled out here.
    static IReadOnlyList<ConfigFact> TestModeOffFacts() =>
        SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            new SystemConfigChecker.ConfigReadings(
                TestSigningOn: false, HvciOn: false, PackagePresent: true,
                Watcher: HealthyWatcher, NowUtc: T0, BoundDriverSelfSigned: true));

    static ConfigFact Fact(
        string id, ConfigSeverity severity, string title,
        string? actionLabel = null, string? actionUrl = null) =>
        new(id, title, severity, "Detail written by the checker.", actionLabel, actionUrl);

    [Fact]
    public void NoFacts_GetsNoSectionLineAtAll()
    {
        // Nothing was checked - a stock-Windows mouse, or a Check that failed
        // and correctly returned nothing. A row here would claim a
        // verification that never happened.
        Assert.Null(ConfigFactView.SectionLabel([]));
        Assert.Empty(ConfigFactView.Rows([]));
    }

    [Fact]
    public void HealthyPc_GetsAReassuringLineWithNothingWarningShapedInIt()
    {
        var facts = HealthyFacts();
        Assert.NotEmpty(facts);
        Assert.All(facts, f => Assert.Equal(ConfigSeverity.Ok, f.Severity));

        var label = ConfigFactView.SectionLabel(facts);
        Assert.NotNull(label);

        // Everything the user can see in the menu on this machine: the
        // collapsed line and every row under it.
        var shown = new List<string>(ConfigFactView.Rows(facts)) { label! };
        Assert.Equal(facts.Count + 1, shown.Count);
        foreach (var line in shown)
        {
            foreach (var shape in WarningShapes)
            {
                Assert.False(
                    line.Contains(shape, StringComparison.Ordinal),
                    $"healthy PC was shown warning-shaped text '{shape}' in: {line}");
            }
        }
    }

    // Substrings a healthy machine must never be shown. Case-sensitive on
    // purpose: "all checks passed" is fine, a row headed "Check:" is not.
    static readonly string[] WarningShapes =
        ["Blocking", "Check:", "problem", "failed", "unknown", "warning", "blocked", " not "];

    [Fact]
    public void BlockingSortsAboveAdvisoryAndOwnsTheCollapsedLine()
    {
        Assert.True(ConfigFactView.Rank(ConfigSeverity.Blocking) < ConfigFactView.Rank(ConfigSeverity.Advisory));
        Assert.True(ConfigFactView.Rank(ConfigSeverity.Advisory) < ConfigFactView.Rank(ConfigSeverity.Ok));

        // Checker order deliberately puts the advisory and the Ok fact first,
        // so a view that kept input order would fail both assertions below.
        IReadOnlyList<ConfigFact> facts =
        [
            Fact(SystemConfigChecker.WatcherFactId, ConfigSeverity.Advisory, "Multitouch watcher is not installed"),
            Fact(SystemConfigChecker.DriverPackageFactId, ConfigSeverity.Ok, "KMDF driver package is installed"),
            Fact(SystemConfigChecker.MemoryIntegrityFactId, ConfigSeverity.Blocking, "Memory integrity is on"),
        ];

        string[] expected =
        [
            "Blocking: Memory integrity is on",
            "Check: Multitouch watcher is not installed",
            "OK: KMDF driver package is installed",
        ];
        Assert.Equal(expected, ConfigFactView.Rows(facts));

        var label = ConfigFactView.SectionLabel(facts);
        Assert.Equal("System config: Memory integrity is on", label);
    }

    [Fact]
    public void SeveralOffendersAreCountedRatherThanSilentlyDroppingOne()
    {
        IReadOnlyList<ConfigFact> twoBlocking =
        [
            Fact(SystemConfigChecker.TestModeFactId, ConfigSeverity.Blocking, "Test Mode is off"),
            Fact(SystemConfigChecker.MemoryIntegrityFactId, ConfigSeverity.Blocking, "Memory integrity is on"),
        ];
        var blockingLabel = ConfigFactView.SectionLabel(twoBlocking);
        Assert.Equal("System config: 2 settings on this PC are blocking this driver", blockingLabel);

        IReadOnlyList<ConfigFact> twoAdvisory =
        [
            Fact(SystemConfigChecker.TestModeFactId, ConfigSeverity.Advisory, "Test Mode state is unknown"),
            Fact(SystemConfigChecker.WatcherFactId, ConfigSeverity.Advisory, "Multitouch watcher is not reporting"),
        ];
        Assert.Equal("System config: 2 things worth checking", ConfigFactView.SectionLabel(twoAdvisory));
    }

    [Fact]
    public void EverySectionLineStaysBehindThePrefixSoItCannotReadAsADeviceFault()
    {
        // The repair row (RepairPlanner.MenuLabel) owns the bare fault voice.
        // A config line that could say "2 problems found" would let a property
        // of the PC outrank a measured fault on the device.
        IReadOnlyList<ConfigFact>[] cases =
        [
            HealthyFacts(),
            TestModeOffFacts(),
            [Fact(SystemConfigChecker.WatcherFactId, ConfigSeverity.Advisory, "Multitouch watcher is not reporting")],
            [Fact(SystemConfigChecker.TestModeFactId, ConfigSeverity.Blocking, "Test Mode is off"),
             Fact(SystemConfigChecker.MemoryIntegrityFactId, ConfigSeverity.Blocking, "Memory integrity is on")],
        ];

        foreach (var facts in cases)
        {
            var label = ConfigFactView.SectionLabel(facts);
            Assert.NotNull(label);
            Assert.StartsWith("System config: ", label!, StringComparison.Ordinal);
            Assert.DoesNotContain("problems found", label!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WatcherDialog_NamesTheDriverPackageAsOwnerAndNeverTheTray()
    {
        var fact = Fact(
            SystemConfigChecker.WatcherFactId, ConfigSeverity.Advisory,
            "Multitouch watcher is not reporting",
            "Open the v3 driver repository", DriverPackageCatalog.V3RepoUrl);

        var text = ConfigFactView.DialogText(fact);

        Assert.Contains("Who fixes this:", text, StringComparison.Ordinal);
        Assert.Contains("The driver package, not Magic Tray.", text, StringComparison.Ordinal);
        Assert.Contains("mm-auto-f1-watcher.ps1", text, StringComparison.Ordinal);
        Assert.Contains(DriverPackageCatalog.V3RepoName, text, StringComparison.Ordinal);
        // A second sender of the Apple enable report inside the tray is an
        // explicit non-goal, so the dialog must disclaim it rather than leave
        // the reader expecting a tray button.
        Assert.Contains("Magic Tray never sends the report", text, StringComparison.Ordinal);
        Assert.Contains("will not add a second sender", text, StringComparison.Ordinal);
        // And it must never hand the fix to the user or to the tray.
        Assert.DoesNotContain("You, by hand", text, StringComparison.Ordinal);
        Assert.DoesNotContain("You, on this PC", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SigningAndPackageFactsNameTheirOwnOwners()
    {
        // Ownership is keyed off the fact id, so a reworded Detail cannot move
        // the fix to the wrong component.
        Assert.Contains(
            "never changes Test Mode", ConfigFactView.OwnerLine(SystemConfigChecker.TestModeFactId),
            StringComparison.Ordinal);
        Assert.Contains(
            "never changes Test Mode", ConfigFactView.OwnerLine(SystemConfigChecker.MemoryIntegrityFactId),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "The driver package.", ConfigFactView.OwnerLine(SystemConfigChecker.DriverPackageFactId),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "You, on this PC.", ConfigFactView.OwnerLine("some-fact-added-later"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void OkFactDialog_HasNoProblemStatementAndNoOwner()
    {
        var ok = Assert.Single(HealthyFacts(), f => f.Id == SystemConfigChecker.TestModeFactId);
        var text = ConfigFactView.DialogText(ok);

        Assert.StartsWith(ok.Title, text, StringComparison.Ordinal);
        Assert.Contains(ok.Detail, text, StringComparison.Ordinal);
        Assert.Contains("Nothing to do here", text, StringComparison.Ordinal);
        Assert.DoesNotContain("What is wrong", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Who fixes this", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockingDialog_CarriesTheCheckersStepsVerbatimAndSaysWhatOkDoes()
    {
        var blocking = Assert.Single(TestModeOffFacts(), f => f.Severity == ConfigSeverity.Blocking);
        var text = ConfigFactView.DialogText(blocking);

        // The steps are the checker's words; this view must not paraphrase them.
        Assert.Contains(blocking.Detail, text, StringComparison.Ordinal);
        Assert.Contains("bcdedit /set testsigning on", text, StringComparison.Ordinal);
        Assert.Contains("Who fixes this:", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "OK opens the Magic Mouse v3 page. Cancel changes nothing.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableSignatureDialog_ReadsAsSomethingToCheckAndNeverClaimsSelfSigned()
    {
        // Test Mode off on a bound 0323 whose driver signature could not be
        // read. Nothing here is proven broken, so the strongest thing the user
        // may be shown is a "Check:" row behind the prefix - a Blocking line
        // would be the false positive the evidence-keyed gate exists to avoid -
        // and no line may assert the driver is self-signed.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            new SystemConfigChecker.ConfigReadings(
                TestSigningOn: false, HvciOn: false, PackagePresent: true,
                Watcher: HealthyWatcher, NowUtc: T0, BoundDriverSelfSigned: null));

        Assert.All(facts, f => Assert.NotEqual(ConfigSeverity.Blocking, f.Severity));

        var label = ConfigFactView.SectionLabel(facts);
        Assert.Equal("System config: Test Mode is off", label);
        Assert.Contains("Check: Test Mode is off", ConfigFactView.Rows(facts));

        var testMode = Assert.Single(facts, f => f.Id == SystemConfigChecker.TestModeFactId);
        Assert.Equal(ConfigSeverity.Advisory, testMode.Severity);

        var text = ConfigFactView.DialogText(testMode);
        Assert.Contains("could not read the signature", text, StringComparison.Ordinal);
        Assert.DoesNotContain("is bound to is self-signed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("installed for this mouse is self-signed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FactWithNoActionGetsNoButtonPromise()
    {
        var text = ConfigFactView.DialogText(
            Fact(SystemConfigChecker.WatcherFactId, ConfigSeverity.Advisory, "Multitouch watcher is not installed"));

        Assert.DoesNotContain("OK opens", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Cancel", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProducedStringIsPlainAscii()
    {
        // The tray menu and the log both mangle anything outside plain ASCII,
        // and these strings are pasted into bug reports.
        foreach (var facts in AllFactSets())
        {
            AssertAscii(ConfigFactView.SectionLabel(facts) ?? "");
            foreach (var row in ConfigFactView.Rows(facts))
                AssertAscii(row);
            foreach (var fact in facts)
                AssertAscii(ConfigFactView.DialogText(fact), allowNewline: true);
        }
    }

    static IEnumerable<IReadOnlyList<ConfigFact>> AllFactSets()
    {
        // Every reading the checker can take, over the devices it says anything
        // about at all, so the ASCII sweep covers every fact it can build.
        bool?[] tristate = [null, true, false];
        DeviceDiagReader.F1WatcherState[] watchers =
        [
            new(null, null, null),
            new(false, null, null),
            new(true, null, null),
            new(true, T0.AddHours(-3), true),
            new(true, T0.AddMinutes(-2), false),
            HealthyWatcher,
        ];
        (DeviceKind Kind, string Pid)[] devices =
        [
            (DeviceKind.MagicMouseV3, "0323"),
            (DeviceKind.MagicMouseV2, "0269"),
            (DeviceKind.MagicKeyboard, "0239"),
        ];

        foreach (var (kind, pid) in devices)
        {
            foreach (var status in Statuses())
            {
                foreach (var testSigning in tristate)
                {
                    foreach (var hvci in tristate)
                    {
                        foreach (var package in tristate)
                        {
                            foreach (var watcher in watchers)
                            {
                                yield return SystemConfigChecker.Evaluate(
                                    kind, pid, status,
                                    new SystemConfigChecker.ConfigReadings(
                                        testSigning, hvci, package, watcher, T0));
                            }
                        }
                    }
                }
            }
        }
    }

    static IEnumerable<DriverStatus?> Statuses()
    {
        yield return null;
        foreach (DriverStatus s in Enum.GetValues<DriverStatus>())
            yield return s;
    }

    static void AssertAscii(string s, bool allowNewline = false)
    {
        foreach (var c in s)
        {
            if (allowNewline && c == '\n')
                continue;
            Assert.True(c >= 0x20 && c < 0x7F, $"non-ascii U+{(int)c:X4} in: {s}");
        }
    }
}
