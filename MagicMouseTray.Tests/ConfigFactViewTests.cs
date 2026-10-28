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
        var secureBoot = ConfigFactView.OwnerLine(SystemConfigChecker.SecureBootFactId);

        // The signing promise is no longer "the tray changes nothing": the tray
        // runs the whole sequence. What it promises instead is the shape of
        // that sequence, which is the whole of AGENTS.md:76 - one confirmation
        // listing every step, one administrator prompt, the reboot, and the
        // undo. Each is pinned separately because dropping any one of them
        // turns a confirmed action back into a silent one.
        Assert.Contains("You decide once", testMode, StringComparison.Ordinal);
        Assert.Contains(ConfigFactView.SigningFixLabel, testMode, StringComparison.Ordinal);
        Assert.Contains("one administrator prompt", testMode, StringComparison.Ordinal);
        Assert.Contains("reboot", testMode, StringComparison.Ordinal);
        Assert.Contains("BitLocker", testMode, StringComparison.Ordinal);
        Assert.Contains("never without that answer", testMode, StringComparison.Ordinal);
        Assert.Contains("never on a timer", testMode, StringComparison.Ordinal);
        Assert.Contains("undo is offered whenever there is anything to undo", testMode,
            StringComparison.Ordinal);
        Assert.DoesNotContain("never changes Test Mode", testMode, StringComparison.Ordinal);

        // Memory integrity: the one confirmed sequence now writes the HVCI
        // policy value, so the old blanket promise would be a lie and must stay
        // deleted. What replaces it names the user's answer, the single
        // sequence and the undo - and still names the by-hand route.
        Assert.DoesNotContain(
            "never changes Memory integrity", memoryIntegrity, StringComparison.Ordinal);
        Assert.Contains("with your permission", memoryIntegrity, StringComparison.Ordinal);
        Assert.Contains("one confirmed sequence", memoryIntegrity, StringComparison.Ordinal);
        Assert.Contains("never unprompted at startup", memoryIntegrity, StringComparison.Ordinal);
        Assert.Contains("undo turns it back on", memoryIntegrity, StringComparison.Ordinal);
        Assert.Contains("Core isolation", memoryIntegrity, StringComparison.Ordinal);

        // Secure Boot keeps the ABSOLUTE never-touch promise, because it is the
        // one setting here that no program can change: it lives in firmware.
        // This is the line that must not drift when the other two do.
        Assert.Contains("firmware", secureBoot, StringComparison.Ordinal);
        Assert.Contains("no program can", secureBoot, StringComparison.Ordinal);
        Assert.Contains("never tries", secureBoot, StringComparison.Ordinal);

        Assert.StartsWith(
            "The driver package.", ConfigFactView.OwnerLine(SystemConfigChecker.DriverPackageFactId),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "You, on this PC.", ConfigFactView.OwnerLine("some-fact-added-later"),
            StringComparison.Ordinal);
    }

    static SigningFixPlan Plan(SigningFixState state, params SigningStep[] steps) =>
        new(state, steps, "Prompt body written by the planner.",
            state == SigningFixState.Unavailable ? "This PC could not be read." : null);

    [Fact]
    public void OnlyAPlanWithRealStepsMayRun()
    {
        // SigningFixOffersRun is the single gate every caller asks before a
        // click may reach SigningFix.RunAsync. Looping the enum is what keeps a
        // state added later from defaulting to "runnable".
        foreach (var state in Enum.GetValues<SigningFixState>())
        {
            var runnable = state is SigningFixState.Ready or SigningFixState.NeedsFirmware;
            Assert.Equal(
                runnable,
                ConfigFactView.SigningFixOffersRun(
                    Plan(state, SigningStep.EnableTestSigning, SigningStep.Reboot)));

            // Runnable on paper but carrying no step is not an offer: it would
            // confirm nothing and then elevate for nothing.
            Assert.False(ConfigFactView.SigningFixOffersRun(Plan(state)));
        }
    }

    [Fact]
    public void BlockedAndNothingToDoDialogsNameNoOkThatRunsAnything()
    {
        var unavailable = Plan(SigningFixState.Unavailable);
        var blocked = ConfigFactView.SigningFixBlockedText(unavailable);
        // The planner's own reason, verbatim - this view never re-words a refusal.
        Assert.Contains(unavailable.BlockedReason!, blocked, StringComparison.Ordinal);
        Assert.Contains("Nothing has been changed", blocked, StringComparison.Ordinal);
        AssertAscii(blocked, allowNewline: true);

        // The one refusal with a next move: Secure Boot still on after the
        // firmware trip. The dialog has to name the toggle that was missed and
        // offer the way round again, or the fix dead-ends on a reason alone.
        var secureBoot = SigningFix.BuildPlan(true, true, false, resuming: true);
        var blockedBySecureBoot = ConfigFactView.SigningFixBlockedText(secureBoot);
        Assert.Contains("Secure Boot is still on", blockedBySecureBoot, StringComparison.Ordinal);
        Assert.Contains("To try again", blockedBySecureBoot, StringComparison.Ordinal);
        Assert.Contains(
            "Security or the Boot menu", blockedBySecureBoot, StringComparison.Ordinal);
        Assert.Contains("Set it to Disabled", blockedBySecureBoot, StringComparison.Ordinal);
        AssertAscii(blockedBySecureBoot, allowNewline: true);

        var nothing = ConfigFactView.SigningFixNothingToDoText(Plan(SigningFixState.NothingToDo));
        Assert.Contains("already configured", nothing, StringComparison.Ordinal);
        // The only thing its OK may do is open the undo's OWN confirmation, so
        // the one-confirmation rule survives this path as well.
        Assert.Contains(ConfigFactView.SigningFixRevertLabel, nothing, StringComparison.Ordinal);
        Assert.Contains("Nothing runs until you accept", nothing, StringComparison.Ordinal);
        AssertAscii(nothing, allowNewline: true);
    }

    [Fact]
    public void SecureBootOnTakesTheFirmwareRoute_AndTheTrayNeverClaimsItCanChangeIt()
    {
        // The reference PC as measured: Secure Boot on, HVCI on, test signing
        // off. Secure Boot is firmware and bcdedit is refused while it is on,
        // so the ONLY answer is the restart into firmware setup (AGENTS.md:76)
        // - and the plan has to say so itself rather than leave the view to
        // infer it.
        var plan = SigningFix.BuildPlan(
            secureBootOn: true, hvciOn: true, testSigningOn: false, resuming: false);

        Assert.Equal(SigningFixState.NeedsFirmware, plan.State);
        Assert.Contains(SigningStep.RebootToFirmware, plan.Steps);
        Assert.True(ConfigFactView.SigningFixOffersRun(plan));

        var text = ConfigFactView.SigningFixFirmwareText(plan);
        Assert.Contains("Secure Boot", text, StringComparison.Ordinal);
        Assert.Contains(
            "cannot be turned off from inside Windows", text, StringComparison.Ordinal);
        Assert.Contains("never touches the setting", text, StringComparison.Ordinal);
        // What she is about to see, and exactly what to change on it.
        Assert.Contains(
            "WHAT YOU NEED TO DO ON THE SCREEN THAT IS COMING UP", text, StringComparison.Ordinal);
        Assert.Contains("- Set it to Disabled.", text, StringComparison.Ordinal);
        Assert.Contains("Security, Boot or Authentication", text, StringComparison.Ordinal);
        // Exactly one question is left after the firmware screen, and the user
        // has to be told that before she walks into it.
        Assert.Contains("only question left", text, StringComparison.Ordinal);
        AssertAscii(text, allowNewline: true);
    }

    [Fact]
    public void ResumePromptCarriesThePlannersOwnPromptAndBothAnswers()
    {
        // The resumed fix gets ONE dialog, so the planner's prompt has to be
        // inside it verbatim: wrapping it is allowed, replacing its content is
        // consent to something the dialog never named.
        var plan = Plan(
            SigningFixState.Ready, SigningStep.DisableHvci, SigningStep.EnableTestSigning,
            SigningStep.Reboot);
        var text = ConfigFactView.SigningFixResumeText(plan);

        Assert.Contains(plan.Prompt, text, StringComparison.Ordinal);
        Assert.Contains("Finish it now?", text, StringComparison.Ordinal);
        // What is left, and what she has to answer, before the plan body.
        Assert.Contains("WHAT IS LEFT", text, StringComparison.Ordinal);
        Assert.Contains("Answer Yes to run it now", text, StringComparison.Ordinal);
        Assert.Contains("Yes runs the rest", text, StringComparison.Ordinal);
        Assert.Contains("No drops the fix and changes nothing", text, StringComparison.Ordinal);
        AssertAscii(text, allowNewline: true);
    }

    [Fact]
    public void OutcomeDialogsClaimOnlyWhatHappened()
    {
        var ready = Plan(
            SigningFixState.Ready, SigningStep.SuspendBitLocker, SigningStep.DisableHvci,
            SigningStep.EnableTestSigning, SigningStep.Reboot);
        var rebooting = ConfigFactView.SigningFixRebootingText(ready);
        Assert.Contains("rebooting now", rebooting, StringComparison.Ordinal);
        Assert.Contains("watermark", rebooting, StringComparison.Ordinal);
        Assert.Contains("BitLocker", rebooting, StringComparison.Ordinal);
        // The way back is named where the user is told she is in Test Mode.
        Assert.Contains(
            ConfigFactView.SigningFixRevertLabel, rebooting, StringComparison.Ordinal);

        // A plan that never suspended BitLocker must not claim it did. The undo
        // line still names BitLocker, because the undo resumes it either way -
        // what may not appear is the claim that this run suspended anything.
        var noBitLocker = Plan(
            SigningFixState.Ready, SigningStep.EnableTestSigning, SigningStep.Reboot);
        Assert.DoesNotContain(
            "BitLocker protection was suspended",
            ConfigFactView.SigningFixRebootingText(noBitLocker), StringComparison.Ordinal);

        // UAC declined: the elevated script never started, so no partial change
        // may be implied anywhere in this text.
        var cancelled = ConfigFactView.SigningFixCancelledText();
        Assert.Contains("Nothing was changed", cancelled, StringComparison.Ordinal);
        Assert.Contains("none of the steps ran", cancelled, StringComparison.Ordinal);
        Assert.Contains("no reboot is scheduled", cancelled, StringComparison.Ordinal);

        // The script's own reason is shown verbatim: it is the only evidence of
        // how far the sequence got, and a summary of it is a summary of the
        // only thing the user can act on.
        const string Reason = "bcdedit: The value is protected by Secure Boot policy.";
        var failed = ConfigFactView.SigningFixFailedText(Reason);
        Assert.Contains(Reason, failed, StringComparison.Ordinal);
        Assert.Contains("do not assume the settings moved", failed, StringComparison.Ordinal);

        // No reason is not an empty paragraph claiming the script said nothing.
        var noReason = ConfigFactView.SigningFixFailedText(null);
        Assert.DoesNotContain(
            "What the elevated step reported", noReason, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", noReason, StringComparison.Ordinal);
    }

    [Fact]
    public void RevertTextIsKeyedOffTheRevertStepsAndOffersTheFixAgain()
    {
        // The two legs carry different step values because the elevated script
        // has to know which direction each setting moves (SigningFix.cs), so a
        // revert text that read the forward names would describe nothing.
        var revert = Plan(
            SigningFixState.Ready, SigningStep.SuspendBitLocker, SigningStep.DisableTestSigning,
            SigningStep.EnableHvci, SigningStep.Reboot);
        var done = ConfigFactView.SigningFixRevertDoneText(revert);

        Assert.Contains("test signing is off", done, StringComparison.Ordinal);
        Assert.Contains("Memory integrity is back on", done, StringComparison.Ordinal);
        // The undo suspends for one restart exactly as the forward fix does; it
        // never resumes BitLocker, because resuming before the reboot that
        // rewrites the BCD is what triggers the recovery-key prompt.
        Assert.DoesNotContain("resumed", done, StringComparison.Ordinal);
        // Undoing the fix must not lock the user out of redoing it.
        Assert.Contains(ConfigFactView.SigningFixLabel, done, StringComparison.Ordinal);

        var forward = Plan(
            SigningFixState.Ready, SigningStep.EnableTestSigning, SigningStep.DisableHvci,
            SigningStep.Reboot);
        Assert.DoesNotContain(
            "Memory integrity is back on", ConfigFactView.SigningFixRevertDoneText(forward),
            StringComparison.Ordinal);

        var nothing = ConfigFactView.SigningFixNothingToRevertText(Plan(SigningFixState.NothingToDo));
        Assert.Contains("nothing to undo", nothing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was changed", nothing, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySigningFixStringIsPlainAscii()
    {
        // Same sweep the fact strings get, over every state and every step set
        // a plan can carry: these are pasted into bug reports and the tray's
        // own dialogs mangle anything outside plain ASCII.
        foreach (var state in Enum.GetValues<SigningFixState>())
        {
            foreach (var steps in StepSets())
            {
                var plan = new SigningFixPlan(state, steps, "Prompt body.", "A reason.");
                AssertAscii(ConfigFactView.SigningFixBlockedText(plan), allowNewline: true);
                AssertAscii(ConfigFactView.SigningFixNothingToDoText(plan), allowNewline: true);
                AssertAscii(ConfigFactView.SigningFixNothingToRevertText(plan), allowNewline: true);
                AssertAscii(ConfigFactView.SigningFixResumeText(plan), allowNewline: true);
                AssertAscii(ConfigFactView.SigningFixFirmwareText(plan), allowNewline: true);
                AssertAscii(ConfigFactView.SigningFixRebootingText(plan), allowNewline: true);
                AssertAscii(ConfigFactView.SigningFixRevertDoneText(plan), allowNewline: true);
            }
        }

        AssertAscii(ConfigFactView.SigningFixCancelledText(), allowNewline: true);
        AssertAscii(ConfigFactView.SigningFixFailedText(null), allowNewline: true);
        AssertAscii(ConfigFactView.SigningFixLabel);
        AssertAscii(ConfigFactView.SigningFixRevertLabel);
    }

    static IEnumerable<SigningStep[]> StepSets()
    {
        yield return Array.Empty<SigningStep>();
        yield return Enum.GetValues<SigningStep>();
        foreach (var step in Enum.GetValues<SigningStep>())
            yield return [step];
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
