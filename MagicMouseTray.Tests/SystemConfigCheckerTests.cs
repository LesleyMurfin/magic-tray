// SPDX-License-Identifier: MIT
using System.Globalization;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// SystemConfigChecker.Evaluate is the whole configuration decision with the
// machine lifted out: Check reads the signature on the driver file this device
// depends on, test signing, HVCI, the driver package and the watcher, then
// hands those readings plus an injected clock to Evaluate. Everything that
// could make this file lie - what counts as Blocking, what an unreadable fact
// is allowed to say, and which devices the signing story even applies to -
// lives in Evaluate, so that is what is tested here.
//
// Signing relevance is keyed on the SIGNATURE, never on the PID: the same
// applewirelessmouse service and file name carry Apple's WHQL-signed Boot Camp
// binary on one PC and a self-signed patched one on the next, so both the
// false-nag case and the silent-blind-spot case are pinned below.
//
// Check itself is deliberately untested: it needs HKLM boot options, the Device
// Guard policy key and C:\ProgramData\MagicMouseDriver on disk, and faking those
// would only test the fake.
//
// The failure mode this whole file is shaped around is nagging a healthy PC, so
// the healthy machine has its own test and the "we could not read it" machine
// must stay at Advisory.
//
// The rival-claimant fact is the one reading here that is about the FUTURE -
// another installed package could win the next rescan - so it is held to the
// strictest version of the same rule: it may never be Blocking, it stays
// silent unless the tray can prove the package in use is not the one that
// wins today, and the healthy reference-PC shape must not produce a warning.
public class SystemConfigCheckerTests
{
    static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    static readonly DeviceDiagReader.F1WatcherState UnknownWatcher = new(null, null, null);

    static DeviceDiagReader.F1WatcherState HealthyWatcher =>
        new(Installed: true, LastHeartbeatUtc: T0.AddMinutes(-2), LastF1Ok: true);

    static SystemConfigChecker.ConfigReadings Readings(
        bool? testSigning = null,
        bool? hvci = null,
        bool? package = null,
        DeviceDiagReader.F1WatcherState? watcher = null,
        DateTime? nowUtc = null,
        bool? selfSigned = null,
        IReadOnlyList<DriverClaim>? claims = null) =>
        new(testSigning, hvci, package, watcher ?? UnknownWatcher, nowUtc ?? T0, selfSigned,
            claims);

    static ConfigFact? Fact(IReadOnlyList<ConfigFact> facts, string id)
    {
        foreach (var f in facts)
            if (f.Id == id)
                return f;
        return null;
    }

    static IReadOnlyList<ConfigFact> Blocking(IReadOnlyList<ConfigFact> facts)
    {
        var list = new List<ConfigFact>();
        foreach (var f in facts)
            if (f.Severity == ConfigSeverity.Blocking)
                list.Add(f);
        return list;
    }

    [Fact]
    public void HealthyKmdfPc_HasNoBlockingFactAndNothingToNagAbout()
    {
        // The reference PC: 0323 bound to the KMDF filter, Test Mode on, HVCI
        // off, package installed, watcher reporting. This machine is correctly
        // configured and the checker must have nothing to complain about.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            Readings(testSigning: true, hvci: false, package: true, watcher: HealthyWatcher,
                selfSigned: true));

        Assert.Empty(Blocking(facts));
        Assert.All(facts, f => Assert.Equal(ConfigSeverity.Ok, f.Severity));
        Assert.All(facts, f => Assert.Null(f.ActionUrlOrScript));
    }

    [Fact]
    public void SelfSignedBound_WithTestSigningOff_IsExactlyOneBlockingTestModeFact()
    {
        // Measured cause of a dead wheel on a bound self-signed filter
        // (DeviceRepair.cs:232-257): test signing off means Windows refuses to
        // load it. Only Test Mode blocks - HVCI is off and the package is there.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            Readings(testSigning: false, hvci: false, package: true, watcher: HealthyWatcher,
                selfSigned: true));

        var blocking = Assert.Single(Blocking(facts));
        Assert.Equal(SystemConfigChecker.TestModeFactId, blocking.Id);
        Assert.Contains("Test Mode", blocking.Title, StringComparison.Ordinal);
        Assert.Contains("bcdedit /set testsigning on", blocking.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfSignedBound_WithHvciOn_IsBlockingOnMemoryIntegrityOnly()
    {
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PathAPatched,
            Readings(testSigning: true, hvci: true, package: true, selfSigned: true));

        var blocking = Assert.Single(Blocking(facts));
        Assert.Equal(SystemConfigChecker.MemoryIntegrityFactId, blocking.Id);
        Assert.Contains("Memory integrity", blocking.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfSignedPackagePresentButNotBound_WithTestSigningOff_IsAdvisoryNotBlocking()
    {
        // Blocking is reserved for a driver that is in use. NotBound means the
        // package is installed and nothing is loading it: Test Mode is the first
        // thing to check, but nothing is proven broken by it.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.NotBound,
            Readings(testSigning: false, hvci: true, package: true, selfSigned: true));

        Assert.Empty(Blocking(facts));
        Assert.Equal(ConfigSeverity.Advisory,
            Fact(facts, SystemConfigChecker.TestModeFactId)!.Severity);
        Assert.Equal(ConfigSeverity.Advisory,
            Fact(facts, SystemConfigChecker.MemoryIntegrityFactId)!.Severity);
    }

    [Fact]
    public void StockWindows0323_WithTestSigningOff_SaysNothingAboutSigning()
    {
        // Stock Windows hands the mouse back to HidBth, which Microsoft signs.
        // Test Mode is not part of that story and must never be mentioned.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.StockKmdf,
            Readings(testSigning: false, hvci: true, package: true));

        Assert.Null(Fact(facts, SystemConfigChecker.TestModeFactId));
        Assert.Null(Fact(facts, SystemConfigChecker.MemoryIntegrityFactId));
        Assert.Empty(Blocking(facts));
    }

    [Fact]
    public void BootCampMouse_WithTestSigningOff_SaysNothingAboutSigning()
    {
        // Apple's Boot Camp binary is WHQL-signed, however it was installed, so
        // a v1 owner with Test Mode off is a perfectly healthy machine. This is
        // the false-nag case: widening the gate to all v1/v2 mice would tell
        // every one of them to change a security setting for no reason.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV1, "030d", DriverStatus.Ok,
            Readings(testSigning: false, hvci: true, package: true, selfSigned: false));

        Assert.Null(Fact(facts, SystemConfigChecker.TestModeFactId));
        Assert.Null(Fact(facts, SystemConfigChecker.MemoryIntegrityFactId));
        Assert.All(facts, f => Assert.Equal(ConfigSeverity.Ok, f.Severity));
    }

    [Fact]
    public void SelfSignedFilterOnAV1_WithTestSigningOff_IsExactlyOneBlockingTestModeFact()
    {
        // The patched-binary route on a v1: the file bound to this mouse is
        // self-signed, so Test Mode is load-bearing for it exactly as it is on
        // a 0323. While relevance was keyed on the PID this was silent, and the
        // wheel died with nothing said.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV1, "030d", DriverStatus.Ok,
            Readings(testSigning: false, hvci: false, package: true, selfSigned: true));

        var blocking = Assert.Single(Blocking(facts));
        Assert.Equal(SystemConfigChecker.TestModeFactId, blocking.Id);
        Assert.Contains("Test Mode", blocking.Title, StringComparison.Ordinal);
        Assert.Contains("bcdedit /set testsigning on", blocking.Detail, StringComparison.Ordinal);

        // And it must not send a v1 owner to the page about the 2024 mouse.
        Assert.DoesNotContain("v3.html", blocking.ActionUrlOrScript!, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfSignedFilterOnAV1_WithHvciOn_IsBlockingOnMemoryIntegrity()
    {
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV1, "030d", DriverStatus.Ok,
            Readings(testSigning: true, hvci: true, package: true, selfSigned: true));

        var blocking = Assert.Single(Blocking(facts));
        Assert.Equal(SystemConfigChecker.MemoryIntegrityFactId, blocking.Id);
    }

    [Fact]
    public void SelfSignedFilterInstalledButNotBoundOnAV1_IsAdvisoryNotBlocking()
    {
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV1, "030d", DriverStatus.NotBound,
            Readings(testSigning: false, hvci: true, package: true, selfSigned: true));

        Assert.Empty(Blocking(facts));
        Assert.Equal(ConfigSeverity.Advisory,
            Fact(facts, SystemConfigChecker.TestModeFactId)!.Severity);
    }

    [Fact]
    public void WhqlSignedAppleFilterOn0323_WithTestSigningOff_SaysNothingAboutSigning()
    {
        // Apple's signed applewirelessmouse.sys can be bound to a 0323 by hand,
        // which DriverHealthChecker reports as PathAPatched. That driver loads
        // with nothing switched off, so a Blocking Test Mode fact here would be
        // a false alarm about a security setting on a PC where scroll works.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PathAPatched,
            Readings(testSigning: false, hvci: true, package: true, selfSigned: false));

        Assert.Null(Fact(facts, SystemConfigChecker.TestModeFactId));
        Assert.Null(Fact(facts, SystemConfigChecker.MemoryIntegrityFactId));
        Assert.Empty(Blocking(facts));
    }

    [Fact]
    public void UnknownSignatureOnAV1_SaysNothingAboutSigning()
    {
        // No evidence about a v1's driver file means no signing story at all:
        // the documented route for these mice is Apple's signed binary, so a
        // guess here would nag every owner of one.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV1, "030d", DriverStatus.Ok,
            Readings(testSigning: false, hvci: true, package: true));

        Assert.Null(Fact(facts, SystemConfigChecker.TestModeFactId));
        Assert.Null(Fact(facts, SystemConfigChecker.MemoryIntegrityFactId));
        Assert.All(facts, f => Assert.Equal(ConfigSeverity.Ok, f.Severity));
    }

    [Fact]
    public void UnknownSignatureOnABound0323_NeverBlocksAndNeverClaimsSelfSigned()
    {
        // The signature read can fail on any PC. The question stays on the table
        // for the packages this project builds and self-signs, but an unread
        // signature may not be reported as a fault and may not be described as
        // self-signed.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            Readings(testSigning: false, hvci: true, package: true, watcher: HealthyWatcher));

        Assert.Empty(Blocking(facts));
        var testMode = Fact(facts, SystemConfigChecker.TestModeFactId)!;
        Assert.Equal(ConfigSeverity.Advisory, testMode.Severity);
        Assert.Contains("could not read the signature", testMode.Detail, StringComparison.Ordinal);
        Assert.Equal(ConfigSeverity.Advisory,
            Fact(facts, SystemConfigChecker.MemoryIntegrityFactId)!.Severity);
    }

    [Fact]
    public void EveryReadingUnknown_NeverExceedsAdvisory()
    {
        // An unelevated tray on a locked-down PC can fail every probe. Absence
        // of evidence is never a fault.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            Readings());

        Assert.NotEmpty(facts);
        Assert.Empty(Blocking(facts));
        Assert.All(facts, f => Assert.NotEqual(ConfigSeverity.Blocking, f.Severity));
    }

    [Fact]
    public void MissingWatcherOnBoundKmdf_IsAdvisoryOwnedByTheDriverPackage()
    {
        // Fault B in docs/ENABLE-DISABLE.md, "Which dead-wheel fault is this -
        // read the stack before you chase filters". The tray must name the owner
        // of the fix and offer something to read - never an action that sends F1.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            Readings(testSigning: true, hvci: false, package: true,
                watcher: new DeviceDiagReader.F1WatcherState(false, null, null)));

        var watcher = Fact(facts, SystemConfigChecker.WatcherFactId)!;
        Assert.Equal(ConfigSeverity.Advisory, watcher.Severity);
        Assert.Empty(Blocking(facts));
        Assert.Contains(DriverPackageCatalog.V3RepoName, watcher.Detail, StringComparison.Ordinal);
        Assert.Contains("Magic Tray never sends it", watcher.Detail, StringComparison.Ordinal);
        Assert.StartsWith("https://", watcher.ActionUrlOrScript, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleWatcherOnBoundKmdf_IsAdvisoryWithTheAge()
    {
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            Readings(testSigning: true, hvci: false, package: true,
                watcher: new DeviceDiagReader.F1WatcherState(true, T0.AddMinutes(-95), true)));

        var watcher = Fact(facts, SystemConfigChecker.WatcherFactId)!;
        Assert.Equal(ConfigSeverity.Advisory, watcher.Severity);
        Assert.Contains("95 minutes old", watcher.Detail, StringComparison.Ordinal);
        Assert.Contains(DriverPackageCatalog.V3RepoName, watcher.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WatcherHeartbeatInTheFuture_IsNotStale()
    {
        // The watcher stamps its log in machine-local time; a skewed clock must
        // not be reported as a fault.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            Readings(testSigning: true, hvci: false, package: true,
                watcher: new DeviceDiagReader.F1WatcherState(true, T0.AddMinutes(90), true)));

        Assert.Equal(ConfigSeverity.Ok, Fact(facts, SystemConfigChecker.WatcherFactId)!.Severity);
    }

    // ---- the three watcher states (tray issue #138) -----------------------
    //
    // Driven from synthetic logs through the real reader so the rendered text
    // is asserted against what the watcher actually writes, with no file
    // system involved. Every case also proves nothing reaches Blocking: a dead
    // watcher is the driver package's to fix, not a state the tray can prove
    // is breaking the mouse right now.
    static ConfigFact WatcherFactFromLog(IEnumerable<string> log, bool? installed = true)
    {
        var state = DeviceDiagReader.ScanWatcherLog(installed, log);
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            Readings(testSigning: true, hvci: false, package: true, watcher: state,
                // Relative to the log's own newest heartbeat, so the test does
                // not depend on the machine timezone ParseStamp converts from.
                nowUtc: state.LastHeartbeatUtc?.AddMinutes(1) ?? T0, selfSigned: true));

        Assert.All(facts, f => Assert.NotEqual(ConfigSeverity.Blocking, f.Severity));
        return Fact(facts, SystemConfigChecker.WatcherFactId)!;
    }

    [Fact]
    public void WatcherLogWithAFreshHeartbeat_RendersAsRunning()
    {
        var watcher = WatcherFactFromLog(
            ["[2026-09-14 08:05:00] heartbeat alive", "[2026-09-14 08:05:01] SetFeature ok=True"]);

        Assert.Equal("Multitouch watcher is running", watcher.Title);
        Assert.Equal(ConfigSeverity.Ok, watcher.Severity);
    }

    [Fact]
    public void WatcherLogEndingInFatal_RendersAsDeadWithItsOwnReason()
    {
        // Tray issue #138: this used to render as "heartbeat was not found",
        // which reads as "we could not see it" for a watcher that said out loud
        // why it quit. A self-declared death must never be flattened into
        // silence.
        var watcher = WatcherFactFromLog(
            ["[2026-09-14 08:05:00] heartbeat alive",
                "[2026-09-14 08:10:00] FATAL missing F1 script"]);

        Assert.Equal("Multitouch watcher stopped with an error", watcher.Title);
        Assert.Equal(ConfigSeverity.Advisory, watcher.Severity);
        Assert.Contains("\"FATAL missing F1 script\"", watcher.Detail, StringComparison.Ordinal);
        Assert.Contains("it is not running", watcher.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot confirm", watcher.Detail, StringComparison.Ordinal);

        // Still the driver package's fix, and the tray still sends nothing.
        Assert.Contains(DriverPackageCatalog.V3RepoName, watcher.Detail, StringComparison.Ordinal);
        Assert.Contains("Magic Tray never sends it", watcher.Detail, StringComparison.Ordinal);
        Assert.StartsWith("https://", watcher.ActionUrlOrScript, StringComparison.Ordinal);
    }

    [Fact]
    public void WatcherLogWithAFatalThenALaterHeartbeat_RendersAsRunning()
    {
        var watcher = WatcherFactFromLog(
            ["[2026-09-14 08:10:00] FATAL missing F1 script",
                "[2026-09-14 08:15:00] heartbeat alive"]);

        Assert.Equal("Multitouch watcher is running", watcher.Title);
        Assert.Equal(ConfigSeverity.Ok, watcher.Severity);
        Assert.DoesNotContain("FATAL", watcher.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WatcherLogWithNoLinesAtAll_RendersAsMissingEvidence()
    {
        // An empty log and an absent log are the same read: no lines. The
        // watcher is installed, nothing is known about it, and that is not a
        // fault - so the text must claim neither health nor breakage.
        var watcher = WatcherFactFromLog([]);

        Assert.Equal("Multitouch watcher heartbeat was not found", watcher.Title);
        Assert.Equal(ConfigSeverity.Advisory, watcher.Severity);
        Assert.Contains("missing evidence, not a fault", watcher.Detail,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FATAL", watcher.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WatcherProbeThatCouldNotRun_StaysUnknown()
    {
        var watcher = WatcherFactFromLog([], installed: null);

        Assert.Equal("Multitouch watcher state is unknown", watcher.Title);
        Assert.Equal(ConfigSeverity.Advisory, watcher.Severity);
        Assert.Contains("not claiming anything either way", watcher.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WatcherIsNotReportedForAPathOtherThanBoundKmdf()
    {
        // The watcher only exists for the 0323 KMDF filter. A patched-Apple or
        // stock mouse must not be told about it.
        var pathA = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PathAPatched,
            Readings(testSigning: true, hvci: false, package: true, watcher: UnknownWatcher));
        var stock = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.StockKmdf,
            Readings(package: true, watcher: UnknownWatcher));

        Assert.Null(Fact(pathA, SystemConfigChecker.WatcherFactId));
        Assert.Null(Fact(stock, SystemConfigChecker.WatcherFactId));
    }

    [Fact]
    public void MissingPackage_IsAdvisoryWithADocumentationUrlAndNeverBlocking()
    {
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.StockKmdf,
            Readings(package: false));

        var package = Fact(facts, SystemConfigChecker.DriverPackageFactId)!;
        Assert.Equal(ConfigSeverity.Advisory, package.Severity);
        Assert.Equal(DriverPackageCatalog.V3RepoUrl, package.ActionUrlOrScript);
        Assert.Empty(Blocking(facts));
    }

    [Fact]
    public void BootCampMissingPackage_PointsAtTheBootCampPageNotTheV3Repo()
    {
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV1, "030d", DriverStatus.NotInstalled,
            Readings(package: false));

        var package = Fact(facts, SystemConfigChecker.DriverPackageFactId)!;
        Assert.Equal(DriverPackageCatalog.TealtadpolePageUrl, package.ActionUrlOrScript);
        Assert.DoesNotContain("Test Mode is required", package.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeviceKind.MagicKeyboard, "0320")]
    [InlineData(DeviceKind.MagicTrackpadV2, "0265")]
    [InlineData(DeviceKind.LogitechMouse, null)]
    public void DevicesWithNoDriverStoryProduceNoFacts(DeviceKind kind, string? pid)
    {
        // These devices have no mouse driver package and no status to report on,
        // so the configuration section must be silent for them.
        Assert.Empty(SystemConfigChecker.Evaluate(kind, pid, null, Readings()));
        Assert.Empty(SystemConfigChecker.Evaluate(kind, pid, DriverStatus.Ok,
            Readings(testSigning: false, hvci: true)));
    }

    [Fact]
    public void UnknownDriverStatusProducesNoFacts()
    {
        // No classification means no claim, even on a 0323 with every reading in.
        Assert.Empty(SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", null,
            Readings(testSigning: false, hvci: true, package: false)));
    }

    [Theory]
    [InlineData("0323")]
    [InlineData("0x0323")]
    [InlineData("0X0323")]
    public void PidIsAuthoritativeOverDeviceKind(string pid)
    {
        // TrayApp hands the PID through from the registry walk, and it appears
        // in logs as 0x0323. A 0323 is a 0323 whatever the DeviceKind says.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV1, pid, DriverStatus.PatchedKmdf,
            Readings(testSigning: false, hvci: false, package: true, watcher: HealthyWatcher,
                selfSigned: true));

        Assert.Equal(SystemConfigChecker.TestModeFactId, Assert.Single(Blocking(facts)).Id);
    }

    // ---- rival driver packages -------------------------------------------

    // The four packages measured on the reference PC (2026-09-16) that all
    // declare the 0323's Bluetooth hardware id. Every one of them matches it
    // exactly, so Windows breaks the tie on the DriverVer date - which is why
    // these dates, not the version numbers, are what the tests turn on.
    static DriverClaim Claim(
        string inf, string date, string version, bool bound,
        string? provider = "MagicMouseFix", string? signer = "MagicMouseFix",
        uint? signerScore = 0x0F000000) =>
        new(inf, provider, signer, signerScore,
            DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            Version.Parse(version), bound);

    static IReadOnlyList<DriverClaim> ReferencePc(string boundInf) =>
    [
        Claim("oem50.inf", "2026-09-15", "2.0.4.3", boundInf == "oem50.inf"),
        Claim("oem26.inf", "2026-08-30", "23.14.8.22", boundInf == "oem26.inf"),
        Claim("oem16.inf", "2026-04-27", "4.47.14.717", boundInf == "oem16.inf",
            provider: "MagicMouseFix", signer: null, signerScore: 0x80000000),
        Claim("oem8.inf", "2026-04-21", "6.2.0.0", boundInf == "oem8.inf", provider: "Apple"),
    ];

    // Every reading healthy, so the only fact that can be anything but Ok is
    // the rival one.
    static SystemConfigChecker.ConfigReadings HealthyWith(IReadOnlyList<DriverClaim>? claims) =>
        Readings(testSigning: true, hvci: false, package: true, watcher: HealthyWatcher,
            selfSigned: true, claims: claims);

    static IReadOnlyList<ConfigFact> Advisories(IReadOnlyList<ConfigFact> facts)
    {
        var list = new List<ConfigFact>();
        foreach (var f in facts)
            if (f.Severity == ConfigSeverity.Advisory)
                list.Add(f);
        return list;
    }

    [Fact]
    public void ReferencePcWithTheNewestPackageBound_IsNotAWarning()
    {
        // The measured reference PC: four packages claim this mouse and the
        // one in use, oem50.inf, is also the newest-dated, so a rescan lands
        // back on it. Three rival packages sitting there is the normal state
        // of a PC that has had a driver reinstalled - warning about it would
        // nag every user this project has.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            HealthyWith(ReferencePc("oem50.inf")));

        var rival = Fact(facts, SystemConfigChecker.RivalClaimantFactId)!;
        Assert.Equal(ConfigSeverity.Ok, rival.Severity);
        Assert.Empty(Advisories(facts));
        Assert.Empty(Blocking(facts));

        // An Ok fact offers nothing to do and nothing to open.
        Assert.Null(rival.ActionLabel);
        Assert.Null(rival.ActionUrlOrScript);
        Assert.Contains("oem50.inf", rival.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("pnputil", rival.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferencePcWithAnOlderPackageBound_IsExactlyOneAdvisoryNamingTheRival()
    {
        // The actionable shape: the mouse is on oem8.inf (21 April) while
        // oem50.inf (15 September) is installed and would win the next
        // rescan. This is the mechanism behind "my scroll was fine, then
        // Windows put me back on the wrong driver".
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            HealthyWith(ReferencePc("oem8.inf")));

        var advisory = Assert.Single(Advisories(facts));
        Assert.Equal(SystemConfigChecker.RivalClaimantFactId, advisory.Id);
        Assert.Empty(Blocking(facts));

        // It names the winner, not just "another package" - the user has to be
        // able to tell which one, and to type it into pnputil.
        Assert.Contains("oem50.inf", advisory.Detail, StringComparison.Ordinal);
        Assert.Contains("oem8.inf", advisory.Detail, StringComparison.Ordinal);
        Assert.Contains("pnputil /delete-driver oem50.inf", advisory.Detail,
            StringComparison.Ordinal);

        // The mechanism, and what a switch costs.
        Assert.Contains("newest-dated", advisory.Detail, StringComparison.Ordinal);
        Assert.Contains("scroll speed you can tune", advisory.Detail, StringComparison.Ordinal);
        Assert.Contains("battery percent", advisory.Detail, StringComparison.Ordinal);
        Assert.Contains("Magic Tray's driver step", advisory.Detail, StringComparison.Ordinal);
        Assert.Contains("Magic Tray will not", advisory.Detail, StringComparison.Ordinal);

        // Driver-stack internals are not user text.
        Assert.DoesNotContain("LowerFilters", advisory.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("0x", advisory.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("rank", advisory.Detail, StringComparison.OrdinalIgnoreCase);

        // It opens a page and runs nothing.
        Assert.StartsWith("https://", advisory.ActionUrlOrScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleClaimantSaysNothing()
    {
        // One package for one device is the whole story. There is no choice
        // for Windows to make and nothing to report.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            HealthyWith([Claim("oem50.inf", "2026-09-15", "2.0.4.3", bound: true)]));

        Assert.Null(Fact(facts, SystemConfigChecker.RivalClaimantFactId));
    }

    [Fact]
    public void NoClaimantsAtAllSaysNothing()
    {
        // The database read fine and nothing claims this hardware id. That is
        // a statement about the driver database, not about this mouse.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf, HealthyWith([]));

        Assert.Null(Fact(facts, SystemConfigChecker.RivalClaimantFactId));
    }

    [Fact]
    public void AnUnreadableClaimReadSaysNothing()
    {
        // null is no evidence. An unelevated tray on a locked-down PC gets it,
        // and it may not turn into a fact of any severity.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf, HealthyWith(null));

        Assert.Null(Fact(facts, SystemConfigChecker.RivalClaimantFactId));
        Assert.Empty(Advisories(facts));
    }

    [Fact]
    public void ClaimantsWithNoneBoundSayNothing()
    {
        // Four packages claim the device and none of them is the one in use -
        // a mouse on a stock Windows driver, or an INF path that matched
        // nothing. There is no "yours" to rank a rival against, so ranking one
        // would be inventing the victim of the switch.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.StockKmdf,
            HealthyWith(ReferencePc("none")));

        Assert.Null(Fact(facts, SystemConfigChecker.RivalClaimantFactId));
    }

    [Fact]
    public void ABoundPackageWithNoDateSaysNothing()
    {
        // The date is the whole tie-break. Without one on the package in use,
        // "something newer is sitting there" is a guess - and this fact exists
        // precisely to avoid telling people their driver is about to change
        // when it is not.
        var undated = new DriverClaim("oem8.inf", "Apple", "MagicMouseFix", 0x0F000000,
            DriverDate: null, DriverVersion: new Version(6, 2, 0, 0), IsBound: true);
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            HealthyWith([Claim("oem50.inf", "2026-09-15", "2.0.4.3", bound: false), undated]));

        Assert.Null(Fact(facts, SystemConfigChecker.RivalClaimantFactId));
    }

    [Fact]
    public void ARivalWithTheSameDateAndNoVersionEvidenceIsNotReported()
    {
        // Same date, and the rival's version could not be decoded. Equal
        // evidence is not a reason to tell someone their driver is at risk.
        var rival = new DriverClaim("oem26.inf", "MagicMouseFix", "MagicMouseFix", 0x0F000000,
            new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc), DriverVersion: null,
            IsBound: false);
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            HealthyWith([Claim("oem50.inf", "2026-09-15", "2.0.4.3", bound: true), rival]));

        Assert.Equal(ConfigSeverity.Ok,
            Fact(facts, SystemConfigChecker.RivalClaimantFactId)!.Severity);
    }

    [Fact]
    public void OnTheSameDateTheHigherVersionIsTheOneThatWouldWin()
    {
        // Two packages rebuilt on the same day is the everyday case for anyone
        // iterating on this driver, and the version is what separates them.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            HealthyWith([
                Claim("oem50.inf", "2026-09-15", "2.0.4.3", bound: true),
                Claim("oem51.inf", "2026-09-15", "2.0.4.4", bound: false),
            ]));

        var advisory = Assert.Single(Advisories(facts));
        Assert.Equal(SystemConfigChecker.RivalClaimantFactId, advisory.Id);
        Assert.Contains("oem51.inf", advisory.Detail, StringComparison.Ordinal);

        // And the other way round: the higher version bound is not at risk.
        var safe = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            HealthyWith([
                Claim("oem50.inf", "2026-09-15", "2.0.4.4", bound: true),
                Claim("oem51.inf", "2026-09-15", "2.0.4.3", bound: false),
            ]));
        Assert.Empty(Advisories(safe));
    }

    [Fact]
    public void TheNamedRivalIsTheOneThatWouldActuallyWin()
    {
        // Three packages outrank the bound one. The user is told about the one
        // Windows would land on, not the first one in the list.
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            HealthyWith(ReferencePc("oem8.inf")));

        var advisory = Assert.Single(Advisories(facts));
        Assert.DoesNotContain("oem26.inf", advisory.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("oem16.inf", advisory.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoClaimShapeCanMakeThisFactBlocking()
    {
        // A rival driver package is not a broken state: the mouse works, on a
        // driver that is loaded. Blocking in this file means measured-broken
        // NOW, and no arrangement of claims may reach it - including the
        // contradictory ones (two packages both reading as bound) and the
        // half-read ones.
        var undated = new DriverClaim("oem16.inf", null, null, 0x80000000,
            DriverDate: null, DriverVersion: null, IsBound: false);
        var undatedBound = undated with { IsBound = true };

        IReadOnlyList<DriverClaim>?[] shapes =
        [
            null,
            [],
            ReferencePc("oem50.inf"),
            ReferencePc("oem26.inf"),
            ReferencePc("oem16.inf"),
            ReferencePc("oem8.inf"),
            ReferencePc("none"),
            [Claim("oem1.inf", "2026-01-01", "1.0.0.0", bound: true),
                Claim("oem2.inf", "2026-01-01", "1.0.0.0", bound: true)],
            [undated, undatedBound],
            [undatedBound, Claim("oem50.inf", "2026-09-15", "2.0.4.3", bound: false)],
            [Claim("oem50.inf", "2026-09-15", "2.0.4.3", bound: true), undated],
        ];

        foreach (var shape in shapes)
        {
            foreach (var status in new[]
            {
                DriverStatus.PatchedKmdf, DriverStatus.PathAPatched, DriverStatus.NotBound,
                DriverStatus.StockKmdf, DriverStatus.Ok, DriverStatus.NotInstalled,
            })
            {
                // Worst case for every other reading too: nothing readable, so
                // any escalation would have to come from the claims alone.
                var facts = SystemConfigChecker.Evaluate(
                    DeviceKind.MagicMouseV3, "0323", status, Readings(claims: shape));
                var rival = Fact(facts, SystemConfigChecker.RivalClaimantFactId);
                if (rival is not null)
                    Assert.NotEqual(ConfigSeverity.Blocking, rival.Severity);

                var legacy = SystemConfigChecker.Evaluate(
                    DeviceKind.MagicMouseV1, "030d", status,
                    Readings(selfSigned: true, claims: shape));
                var legacyRival = Fact(legacy, SystemConfigChecker.RivalClaimantFactId);
                if (legacyRival is not null)
                    Assert.NotEqual(ConfigSeverity.Blocking, legacyRival.Severity);
            }
        }
    }

    [Fact]
    public void ALegacyMouseIsSentToTheLegacyDriverPageNotTheV3Page()
    {
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV1, "030d", DriverStatus.Ok,
            Readings(testSigning: true, hvci: false, package: true, selfSigned: true,
                claims: ReferencePc("oem8.inf")));

        var advisory = Assert.Single(Advisories(facts));
        Assert.Equal(SystemConfigChecker.RivalClaimantFactId, advisory.Id);
        Assert.DoesNotContain("v3.html", advisory.ActionUrlOrScript!, StringComparison.Ordinal);
    }
}
