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
        var testMode = ConfigFactView.OwnerLine(SystemConfigChecker.TestModeFactId);
        var memoryIntegrity = ConfigFactView.OwnerLine(SystemConfigChecker.MemoryIntegrityFactId);

        // The Test Mode promise is no longer "the tray changes nothing": the
        // tray CAN run bcdedit here. What it promises instead is the whole of
        // AGENTS.md:76 - the user's answer first, that answer covering one
        // action only, and the three things the dialog has to name. Each is
        // pinned separately because dropping any one of them turns a confirmed
        // action back into a silent one.
        Assert.Contains("You decide", testMode, StringComparison.Ordinal);
        Assert.Contains("only for the one action you confirm", testMode, StringComparison.Ordinal);
        Assert.Contains("reboot", testMode, StringComparison.Ordinal);
        Assert.Contains("BitLocker", testMode, StringComparison.Ordinal);
        Assert.Contains("never on startup", testMode, StringComparison.Ordinal);
        Assert.Contains("never on a timer", testMode, StringComparison.Ordinal);
        // The revert has to be promised where the action is promised, or the
        // user has no stated way back out of Test Mode.
        Assert.Contains("the way back is offered on this same row", testMode, StringComparison.Ordinal);
        // And the promise that is still true must not be widened back over Test
        // Mode: this line may never again claim the tray never changes it.
        Assert.DoesNotContain("never changes Test Mode", testMode, StringComparison.Ordinal);

        // Memory integrity and Secure Boot keep the original promise in full. A
        // programmatic HVCI change is a standing non-goal, so this is the line
        // that must not drift when the Test Mode one does.
        Assert.Contains(
            "never changes Memory integrity", memoryIntegrity, StringComparison.Ordinal);
        Assert.Contains("Secure Boot", memoryIntegrity, StringComparison.Ordinal);
        Assert.Contains("by hand", memoryIntegrity, StringComparison.Ordinal);

        Assert.StartsWith(
            "The driver package.", ConfigFactView.OwnerLine(SystemConfigChecker.DriverPackageFactId),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "You, on this PC.", ConfigFactView.OwnerLine("some-fact-added-later"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedPlan_OffersNoRunnableAction()
    {
        var fact = Assert.Single(TestModeOffFacts(), f => f.Id == SystemConfigChecker.TestModeFactId);

        // OffersRun is the single gate every caller asks before a click may
        // reach TestModeSwitch.RunAsync, so every reason a plan can be blocked
        // has to fail it. Looping the enum is what keeps a block added later
        // from defaulting to "runnable".
        foreach (var block in Enum.GetValues<TestModeBlock>())
        {
            var plan = new TestModePlan(
                TestModeAction.Enable, "bcdedit /set testsigning on", "prompt body", block);
            Assert.Equal(block == TestModeBlock.None, ConfigFactView.OffersRun(plan));
            if (block == TestModeBlock.None)
                continue;

            // A blocked row still explains itself and still offers the page -
            // and the only thing its OK may ever promise is to OPEN that page.
            var text = ConfigFactView.TestModeBlockedText(fact, plan);
            Assert.Contains(ConfigFactView.BlockReason(plan), text, StringComparison.Ordinal);
            Assert.Contains(fact.Detail, text, StringComparison.Ordinal);
            Assert.Contains("OK opens", text, StringComparison.Ordinal);
            Assert.DoesNotContain("OK runs", text, StringComparison.Ordinal);
            AssertAscii(text, allowNewline: true);
        }
    }

    [Fact]
    public void SecureBootOnBlocksTheAction_AndSaysWhereSecureBootIsChanged()
    {
        // The charter answer to Secure Boot is "report blocked, never attempt"
        // (AGENTS.md:76). BuildPlan is what decides that; this pins that the
        // decision survives into something the user can act on, and that the
        // tray never claims it could change Secure Boot itself.
        var plan = TestModeSwitch.BuildPlan(
            TestModeAction.Enable, secureBootOn: true, testSigningOn: false,
            bitLockerProtected: true);

        Assert.Equal(TestModeBlock.SecureBootOn, plan.Block);
        Assert.False(ConfigFactView.OffersRun(plan));

        var reason = ConfigFactView.BlockReason(plan);
        Assert.Contains("Secure Boot", reason, StringComparison.Ordinal);
        Assert.Contains("UEFI firmware", reason, StringComparison.Ordinal);
        Assert.Contains("Magic Tray never touches it", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AppliedDialog_LeavesTheRebootToTheUserAndNamesTheWayBack()
    {
        // Two promises the charter makes that only this text can keep: the tray
        // does not reboot the PC, and the revert is always stated.
        var enable = TestModeSwitch.BuildPlan(
            TestModeAction.Enable, secureBootOn: false, testSigningOn: false,
            bitLockerProtected: null);
        var done = ConfigFactView.TestModeDoneText(enable);

        Assert.Contains("reboot", done, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not reboot it", done, StringComparison.Ordinal);
        Assert.Contains("bcdedit /set testsigning off", done, StringComparison.Ordinal);
        AssertAscii(done, allowNewline: true);

        // The revert's own revert is the opposite command again, never the one
        // the user just ran.
        var disable = TestModeSwitch.BuildPlan(
            TestModeAction.Disable, secureBootOn: false, testSigningOn: true,
            bitLockerProtected: null);
        Assert.Equal("bcdedit /set testsigning on", ConfigFactView.RevertCommand(disable));
        Assert.Contains(
            "bcdedit /set testsigning on", ConfigFactView.TestModeDoneText(disable),
            StringComparison.Ordinal);

        // A declined administrator prompt changed nothing, and must say so
        // without hedging.
        var cancelled = ConfigFactView.TestModeCancelledText(enable);
        Assert.Contains("Nothing was changed", cancelled, StringComparison.Ordinal);
        Assert.Contains("never ran", cancelled, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockingFacts_AreTheOnesTheProblemRowCounts()
    {
        // The repair flow and the top-of-menu row count these. A healthy PC
        // must contribute nothing, or "No problems found" becomes a lie in the
        // other direction.
        Assert.Empty(ConfigFactView.Blocking(HealthyFacts()));
        Assert.Empty(ConfigFactView.ProblemTitles(HealthyFacts()));

        var blocking = ConfigFactView.Blocking(TestModeOffFacts());
        var fact = Assert.Single(blocking);
        Assert.Equal(SystemConfigChecker.TestModeFactId, fact.Id);

        // Every title stays behind the section prefix, so a configuration line
        // can never be read as the bare device-fault voice the repair row owns.
        var title = Assert.Single(ConfigFactView.ProblemTitles(TestModeOffFacts()));
        Assert.StartsWith(ConfigFactView.SectionPrefix + ": ", title, StringComparison.Ordinal);
        Assert.EndsWith(fact.Title, title, StringComparison.Ordinal);
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
