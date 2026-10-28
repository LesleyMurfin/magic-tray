// SPDX-License-Identifier: MIT
using System.Globalization;
using System.IO;
using System.Security.Cryptography.Pkcs;
using Microsoft.Win32;

namespace MagicMouseTray;

// Is THIS PC configured to run the driver this mouse is on (or the one we
// recommend for it)? Five questions, each answered as its own ConfigFact:
// Test Mode, Memory integrity (HVCI), the driver package on disk, the driver
// package's F1 multitouch watcher, and whether a rival driver package that is
// also installed could take this mouse off the one it is using.
//
// READ-ONLY AND SECURITY-NEUTRAL BY CONSTRUCTION. This file never changes the
// security posture of the machine and contains no code that could: no bcdedit
// WRITE, no HVCI or Device Guard write, no Secure Boot WRITE (the Secure Boot
// state is read, and only read - it is firmware, and SigningFix restarts into
// the UEFI setup screen rather than ever attempting to change it), no signing
// or certificate change, no service install, no pnputil, no registry write of
// any kind.
// ActionLabel / ActionUrlOrScript may only NAME a
// documentation URL or a script that already ships in a known package - this
// file executes neither, and the caller is expected to open, not run, them.
// The only process this file ever starts is at most one `bcdedit /enum
// {current}` READ per Check call, and only when the registry could not answer
// the same question.
//
// Honesty rules, same as DeviceDiagReader:
//   - every reading is tri-state; null means "no evidence", never a fault;
//   - a reading we could not take can reach Advisory ("not claiming anything
//     either way") and NEVER Blocking;
//   - Blocking is reserved for a state this repo has measured to break the
//     driver that is actually in use - a bound driver whose file is SELF-SIGNED
//     while test signing is off, or while HVCI is on (DeviceRepair.cs:232-257,
//     docs/ENABLE-DISABLE.md:182). Both of those need a POSITIVE reading.
//
// A correctly configured PC must produce an empty list or all-Ok facts.
//
// WHICH DEVICES GET A SIGNING FACT IS DECIDED BY EVIDENCE, NEVER BY THE PID.
// The PID cannot answer it: the same service name (applewirelessmouse) and the
// same file name are used both by Apple's WHQL-signed Boot Camp binary - which
// loads with nothing switched off, however it was installed - and by a patched,
// self-signed one. Install provenance cannot answer it either: the sbagirici
// method installs the genuine Apple-signed file by hand, with no INF at all,
// and needs no Test Mode. Only the signature on the driver file the device
// actually depends on can answer it, so that is the reading the gate keys on
// (ReadBoundDriverSelfSigned). Measured on the 2026-09-15 reference PC:
//
//   applewirelessmouse.sys            self-issued WDK test signature PLUS two
//                                     CA-issued Microsoft WHCP signatures
//                                     -> NOT self-signed -> silent, correctly
//   MagicMouseDriver*.sys (v3 KMDF)   CN=MagicMouseFix, self-issued, only
//                                     signature -> self-signed -> in scope
//   HidBth.sys (stock Windows)        no embedded signature -> unknown -> silent
//
// The Apple file shows why the primary signature alone is not enough: Windows
// loads a file if ANY of its signatures is trusted, and on that PC the
// self-issued one comes first. Unknown (a read that failed, or a catalog-only
// file) may never claim a driver is self-signed and may never reach Blocking.
internal enum ConfigSeverity { Ok, Advisory, Blocking }

internal sealed record ConfigFact(
    string Id,
    string Title,
    ConfigSeverity Severity,
    string Detail,
    string? ActionLabel,
    string? ActionUrlOrScript);

internal static class SystemConfigChecker
{
    internal const string TestModeFactId = "test-mode";
    internal const string MemoryIntegrityFactId = "memory-integrity";
    internal const string DriverPackageFactId = "driver-package";
    internal const string WatcherFactId = "f1-watcher";
    internal const string RivalClaimantFactId = "rival-claimant";

    // Raised only when Secure Boot is ON and this device actually has a signing
    // story: Secure Boot is what makes `bcdedit /set testsigning on` fail with
    // "The value is protected by Secure Boot policy", so without it the Test
    // Mode fact would be telling the user to run a command that cannot work.
    internal const string SecureBootFactId = "secure-boot";

    const string ControlKey = @"SYSTEM\CurrentControlSet\Control";
    const string HvciScenarioKey =
        @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    // UEFI firmware publishes the Secure Boot state here and the value is
    // readable without elevation, unlike bcdedit.
    const string SecureBootStateKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";

    // Same page TrayApp.ShowFilterBlockedHelp opens for the blocked-filter
    // dialog, so the wording and the destination agree.
    const string V3DocUrl = "https://magictray.app/v3.html";

    // Model-neutral Test Mode explanation (docs/drivers.html id="safety"), for
    // a device the v3 page is not about.
    const string SafetyDocUrl = "https://magictray.app/drivers.html#safety";

    // docs/drivers.html id="v1v2" - the section about the driver a v1/v2 uses,
    // for a device the v3 page is not about.
    const string LegacyDocUrl = "https://magictray.app/drivers.html#v1v2";

    const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    // Authenticode puts extra signatures in this unauthenticated attribute of
    // the first one, so a file can carry a self-issued signature and a trusted
    // one at the same time - which is exactly what Apple's hand-installed
    // applewirelessmouse.sys looks like once a WDK test certificate has been
    // added to it. Windows loads a file when ANY of its signatures is trusted,
    // so all of them have to be read before calling a driver self-signed.
    const string NestedSignatureOid = "1.3.6.1.4.1.311.2.4.1";

    // The watcher writes one "heartbeat alive" line every 5 minutes
    // (DeviceDiagReader.cs:40-43). Four missed heartbeats is the threshold: a
    // single missed line is scheduler jitter, not a finding.
    internal static readonly TimeSpan WatcherStaleAfter = TimeSpan.FromMinutes(20);

    // What role a self-signed driver plays on this device right now. Test Mode
    // and HVCI only ever matter through this, and it is only ever reached when
    // the evidence in ConfigReadings.BoundDriverSelfSigned allows it.
    internal enum SelfSignedRole
    {
        NotRelevant,  // stock Windows, a driver file Windows trusts, or not a mouse we know
        InUse,        // a self-signed filter is bound to this mouse - signing policy decides whether it loads
        Prerequisite, // the self-signed package is present but not bound - signing policy is why it cannot load
    }

    internal enum PackagePath
    {
        None,
        Kmdf,
        AppleFilterBootCamp, // v1/v2 Apple Boot Camp binary - WHQL-signed, no Test Mode
        AppleFilterPatched,  // 0323 patched Apple filter - self-signed
    }

    // Everything Check reads from the machine, lifted out so the decision is
    // pure. NowUtc is injected for the same reason: watcher staleness must not
    // depend on the wall clock in a test. BoundDriverSelfSigned and Claims are
    // last and defaulted so that a reading nobody has taken stays "no
    // evidence".
    internal readonly record struct ConfigReadings(
        bool? TestSigningOn,
        bool? HvciOn,
        bool? PackagePresent,
        DeviceDiagReader.F1WatcherState Watcher,
        DateTime NowUtc,
        bool? BoundDriverSelfSigned = null,
        IReadOnlyList<DriverClaim>? Claims = null,
        // Trailing and defaulted for the same reason as the two above: a
        // caller that never took this reading gets "no evidence", and every
        // existing construction site keeps compiling.
        bool? SecureBootOn = null);

    internal static IReadOnlyList<ConfigFact> Check(DeviceKind kind, string? pid, DriverStatus? status)
    {
        try
        {
            // Signature evidence comes first, because it is what decides
            // whether the signing question exists at all for this device. It is
            // a file read - no process, no elevation, no registry write - so it
            // is cheap enough to take before the early exit.
            var selfSigned = ReadBoundDriverSelfSigned(kind, pid, status);
            var role = SigningRoleFor(kind, pid, status, selfSigned);
            var path = PackagePathFor(kind, pid, status);
            if (role == SelfSignedRole.NotRelevant && path == PackagePath.None)
                return [];

            // bcdedit is only ever reached from ReadTestSigningOn, and
            // ReadTestSigningOn is only called once the evidence above says a
            // self-signed driver is in play - so a mouse on a driver Windows
            // trusts, and a stock-Windows mouse, spawn nothing at all. Both
            // probes block on IO (a registry read, and at most one capped
            // bcdedit read), so Check belongs on the same background snapshot
            // read the other readers use, never on a UI thread.
            bool signingRelevant = role != SelfSignedRole.NotRelevant;
            var readings = new ConfigReadings(
                TestSigningOn: signingRelevant ? ReadTestSigningOn() : null,
                HvciOn: signingRelevant ? ReadHvciOn() : null,
                PackagePresent: ReadPackagePresent(path),
                Watcher: WatcherRelevant(kind, pid, status)
                    ? DeviceDiagReader.WatcherState()
                    : new DeviceDiagReader.F1WatcherState(null, null, null),
                NowUtc: DateTime.UtcNow,
                BoundDriverSelfSigned: selfSigned,
                Claims: ReadClaims(pid),
                // Same gate as the other two signing reads: a PC whose driver
                // Windows trusts never has the Secure Boot question asked about
                // it, so a healthy machine still takes no extra reading here.
                SecureBootOn: signingRelevant ? ReadSecureBootOn() : null);

            var facts = Evaluate(kind, pid, status, readings);
            Logger.Log($"SYSTEM_CONFIG kind={kind} pid={NormalizePid(pid)} status={(status is null ? "unknown" : status.Value.ToString())} "
                + $"role={role} path={path} selfsigned={Describe(selfSigned)} "
                + $"testsigning={Describe(readings.TestSigningOn)} hvci={Describe(readings.HvciOn)} "
                + $"secureboot={Describe(readings.SecureBootOn)} "
                + $"package={Describe(readings.PackagePresent)} claims={DescribeCount(readings.Claims)} "
                + $"facts={facts.Count} "
                + $"blocking={CountOf(facts, ConfigSeverity.Blocking)}");
            return facts;
        }
        catch (Exception ex)
        {
            // We learned nothing about this PC. Nothing is a better answer than
            // a fabricated finding.
            Logger.Log($"SYSTEM_CONFIG_FAILED err={ex.Message}");
            return [];
        }
    }

    // The whole decision, with the machine left outside. Everything that could
    // make this file lie lives here, and it is all driven by the readings.
    internal static IReadOnlyList<ConfigFact> Evaluate(
        DeviceKind kind, string? pid, DriverStatus? status, ConfigReadings readings)
    {
        var facts = new List<ConfigFact>(5);
        var role = SigningRoleFor(kind, pid, status, readings.BoundDriverSelfSigned);
        var path = PackagePathFor(kind, pid, status);
        bool v3 = IsV3Mouse(kind, pid);

        if (role != SelfSignedRole.NotRelevant)
        {
            facts.Add(TestModeFact(
                role, readings.TestSigningOn, readings.BoundDriverSelfSigned, v3,
                readings.SecureBootOn));
            // ReadHvciOn is already the row's own question (see HvciState):
            // a scenario it looked for and did not find is not on, so a
            // missing key can never escalate to Blocking, and only a probe
            // that actually failed arrives here as null.
            facts.Add(MemoryIntegrityFact(
                role, readings.HvciOn, readings.BoundDriverSelfSigned, v3));

            // Only a PC that both has a signing story AND cannot run the fix
            // gets this row. A PC where Test Mode is already on is not told
            // about Secure Boot at all: nothing is blocked there, and the whole
            // point of the relevance gate is to never nag a working machine.
            if (readings.SecureBootOn == true && readings.TestSigningOn != true)
                facts.Add(SecureBootFact(v3));
        }

        if (path != PackagePath.None)
            facts.Add(PackageFact(path, readings.PackagePresent));

        var rival = RivalClaimantFact(readings.Claims, v3);
        if (rival is not null)
            facts.Add(rival);

        if (WatcherRelevant(kind, pid, status))
            facts.Add(WatcherFact(readings.Watcher, readings.NowUtc));

        return facts;
    }

    // ---- relevance -------------------------------------------------------

    // Relevance is keyed on what the driver file says about itself, never on
    // the PID.
    //
    //   boundDriverSelfSigned == false  Windows trusts a signature on that
    //                                   file, so signing policy cannot be why
    //                                   anything is broken. Silent for EVERY
    //                                   PID - that covers Apple's WHQL-signed
    //                                   Boot Camp binary however it was
    //                                   installed, including the sbagirici
    //                                   hand-installed service on a 0323,
    //                                   which DriverHealthChecker reports as
    //                                   PathAPatched and which needs no Test
    //                                   Mode at all.
    //   boundDriverSelfSigned == true   a self-signed file, so signing policy
    //                                   decides whether it loads.
    //   null                            no evidence. The two 0323 packages
    //                                   THIS project builds are self-signed, so
    //                                   the question stays on the table there
    //                                   and the facts say out loud that they
    //                                   could not read it (capped at Advisory
    //                                   by SigningSeverity). For a v1/v2 the
    //                                   documented route is Apple's signed
    //                                   binary (README.md:389,
    //                                   docs/drivers.html:634), so with no
    //                                   evidence there is nothing to raise.
    //
    // StockKmdf hands the mouse back to HidBth, which Windows signs itself, so
    // Test Mode is not part of that story and must not be mentioned
    // (README.md:215).
    internal static SelfSignedRole SigningRoleFor(
        DeviceKind kind, string? pid, DriverStatus? status, bool? boundDriverSelfSigned)
    {
        if (status is null)
            return SelfSignedRole.NotRelevant;

        // A file Windows trusts ends the signing story for any device.
        if (boundDriverSelfSigned == false)
            return SelfSignedRole.NotRelevant;

        if (IsV3Mouse(kind, pid))
        {
            return status.Value switch
            {
                DriverStatus.PatchedKmdf => SelfSignedRole.InUse,
                DriverStatus.PathAPatched => SelfSignedRole.InUse,
                // NotBound on 0323 means the KMDF package is installed and
                // nothing is loading it - signing policy is the first thing to
                // check (docs/ENABLE-DISABLE.md:182), but nothing is proven
                // broken.
                DriverStatus.NotBound => SelfSignedRole.Prerequisite,
                _ => SelfSignedRole.NotRelevant,
            };
        }

        // v1/v2 need POSITIVE evidence: their documented driver is signed, and
        // nagging that majority about Test Mode would be a false alarm about a
        // security setting.
        if (!IsLegacyMouse(kind, pid) || boundDriverSelfSigned != true)
            return SelfSignedRole.NotRelevant;

        return status.Value switch
        {
            DriverStatus.Ok => SelfSignedRole.InUse,
            DriverStatus.NotBound => SelfSignedRole.Prerequisite,
            _ => SelfSignedRole.NotRelevant,
        };
    }

    // Blocking needs a driver we MEASURED to be self-signed - a signature we
    // could not read (null) is never more than Advisory, which is the
    // nagging-a-healthy-PC guarantee this file is shaped around - plus a
    // reading that measurably explains why it is not working:
    //
    //   role InUse        the self-signed driver is bound and the policy
    //                     reading in front of this call says Windows will not
    //                     load it. Measured-broken now.
    //   role Prerequisite the self-signed package is installed and NOTHING is
    //                     loading it, with Test Mode measured off. That is not
    //                     a hypothetical: it is the 0323 KMDF filter sitting at
    //                     Status=Error with testsigning off, the exact shape of
    //                     the owner's PC, and the reason the package is not
    //                     bound. It used to be Advisory, which buried the one
    //                     problem the tray can now fix end to end
    //                     (SigningFix.BuildPlan), so it blocks.
    //
    // prerequisiteBlocks is passed true by the Test Mode row only. That row is
    // the one that carries the signing fix, and it is the row whose reading
    // (test signing off) is what keeps an installed package unbound. The Memory
    // integrity row passes false: on a package that is not bound at all, HVCI
    // on is the second thing in the way, not the proof that something is
    // broken, and two Blocking rows for one unbound package would say the PC is
    // twice as broken as it is.
    static ConfigSeverity SigningSeverity(
        SelfSignedRole role, bool? selfSigned, bool prerequisiteBlocks)
    {
        if (selfSigned != true)
            return ConfigSeverity.Advisory;
        if (role == SelfSignedRole.InUse)
            return ConfigSeverity.Blocking;
        return role == SelfSignedRole.Prerequisite && prerequisiteBlocks
            ? ConfigSeverity.Blocking
            : ConfigSeverity.Advisory;
    }

    internal static PackagePath PackagePathFor(DeviceKind kind, string? pid, DriverStatus? status)
    {
        if (status is null)
            return PackagePath.None;

        if (IsV3Mouse(kind, pid))
        {
            return status.Value switch
            {
                DriverStatus.PathAPatched => PackagePath.AppleFilterPatched,
                // KMDF is the recommended path for 0323 (README.md:101), so it
                // is also the package we report on when the mouse is bound to
                // it, unbound, or back on stock Windows.
                DriverStatus.PatchedKmdf => PackagePath.Kmdf,
                DriverStatus.NotBound => PackagePath.Kmdf,
                DriverStatus.StockKmdf => PackagePath.Kmdf,
                _ => PackagePath.None,
            };
        }

        if (!IsLegacyMouse(kind, pid))
            return PackagePath.None;

        return status.Value switch
        {
            DriverStatus.Ok => PackagePath.AppleFilterBootCamp,
            DriverStatus.NotBound => PackagePath.AppleFilterBootCamp,
            DriverStatus.NotInstalled => PackagePath.AppleFilterBootCamp,
            _ => PackagePath.None,
        };
    }

    // The watcher only exists for the 0323 KMDF filter, and it is only the
    // known cause of a dead wheel while that filter is the one in use.
    internal static bool WatcherRelevant(DeviceKind kind, string? pid, DriverStatus? status) =>
        status == DriverStatus.PatchedKmdf && IsV3Mouse(kind, pid);

    // ---- facts -----------------------------------------------------------

    // The driver these two facts are about, described from the EVIDENCE we
    // have. With no evidence the wording says so out loud and never claims the
    // driver is self-signed - and SigningSeverity keeps that case at Advisory.
    static string SelfSignedClause(SelfSignedRole role, bool? selfSigned) =>
        selfSigned == true
            ? (role == SelfSignedRole.InUse
                ? "The driver this mouse is bound to is self-signed. "
                : "The driver package installed for this mouse is self-signed. ")
            : "The tray could not read the signature on this mouse's driver file, so it is not "
                + "claiming that driver is self-signed. The Magic Mouse 2024 packages this project "
                + "builds are self-signed, and Test Mode is what lets Windows load one. ";

    // Where the reader goes for the Test Mode story. The v3 page is written
    // about the 0323 packages, so any other model gets the model-neutral
    // safety section instead of being sent somewhere that is not about it.
    static (string Label, string Url) SigningDoc(bool v3) =>
        v3
            ? ("Open the Magic Mouse v3 page", V3DocUrl)
            : ("Read what Test Mode means", SafetyDocUrl);

    // Why Secure Boot belongs in the Test Mode wording: Secure Boot and test
    // signing are mutually exclusive by design. With Secure Boot on, bcdedit
    // rejects the write with "The value is protected by Secure Boot policy",
    // so a detail that tells the user to run that command - or a button that
    // offers to run it - would be telling them to do something that cannot
    // work. Secure Boot is firmware, so the only honest instruction is the
    // UEFI setup screen, and the tray must never attempt it (AGENTS.md:76).
    const string SecureBootBlocksTestMode =
        "Secure Boot is on, and that stops it: Windows refuses bcdedit /set testsigning on while "
        + "Secure Boot is enabled (\"The value is protected by Secure Boot policy\"). Secure Boot "
        + "is a firmware setting - it is changed in this PC's UEFI setup screen, not from Windows, "
        + "and Magic Tray will never touch it. ";

    static ConfigFact TestModeFact(
        SelfSignedRole role, bool? testSigningOn, bool? selfSigned, bool v3, bool? secureBootOn)
    {
        var (label, url) = SigningDoc(v3);
        var clause = SelfSignedClause(role, selfSigned);
        // Only a POSITIVE Secure Boot reading changes the instructions. Unknown
        // keeps the normal wording: the command is still worth trying, and
        // Windows refusing it is a clear message in itself.
        var secureBoot = secureBootOn == true ? SecureBootBlocksTestMode : string.Empty;

        if (testSigningOn is null)
        {
            return new ConfigFact(
                TestModeFactId,
                "Test Mode state is unknown",
                ConfigSeverity.Advisory,
                "The tray could not read the test signing state on this PC, so it is not claiming "
                + "anything either way. " + clause + secureBoot
                + "If the wheel is dead, Test Mode is the first thing worth checking.",
                label,
                url);
        }

        if (testSigningOn.Value)
        {
            return new ConfigFact(
                TestModeFactId,
                "Test Mode is on",
                ConfigSeverity.Ok,
                "Windows is allowed to load a self-signed driver on this PC. The Test Mode desktop "
                + "watermark is expected.",
                null,
                null);
        }

        var consequence = role == SelfSignedRole.InUse && selfSigned == true
            ? "Windows will not load it while Test Mode is off - the pointer keeps working and the "
                + "wheel does not. "
            : "Windows will not load a self-signed driver while Test Mode is off. ";

        // Two different endings, because under Secure Boot there is no step the
        // tray can offer at all. Memory integrity stays a manual step in both:
        // the tray never writes Device Guard policy.
        var remedy = secureBootOn == true
            ? secureBoot
                + "Until that changes, Test Mode cannot be turned on here, and Magic Tray will not "
                + "offer to try."
            : "Turning Test Mode on is a change to how this PC loads drivers, so Magic Tray only "
                + "does it when you ask: the Test Mode action on this row runs bcdedit /set "
                + "testsigning on after a confirmation that names the command, the reboot and the "
                + "BitLocker recovery-key risk, and it is reversible with bcdedit /set testsigning "
                + "off. Memory integrity has to be turned off by hand in Windows Security -> "
                + "Device security -> Core isolation - Magic Tray will not change that for you - "
                + "and the PC has to be rebooted before either takes effect.";
        return new ConfigFact(
            TestModeFactId,
            "Test Mode is off",
            // Measured test signing OFF, here, is what makes an installed
            // self-signed package that nothing is loading a problem and not a
            // note - see SigningSeverity.
            SigningSeverity(role, selfSigned, prerequisiteBlocks: true),
            clause + consequence + remedy,
            label,
            url);
    }

    // A row of its own, so the reason the Test Mode fix is unavailable is not
    // buried in another fact's paragraph. Advisory, never Blocking: Secure Boot
    // is not a broken state, it is a correctly secured PC whose owner has to
    // decide. The Blocking call stays with the Test Mode / Memory integrity
    // facts, where it is backed by a measured self-signed driver in use.
    static ConfigFact SecureBootFact(bool v3)
    {
        var (label, url) = SigningDoc(v3);
        return new ConfigFact(
            SecureBootFactId,
            "Secure Boot is on",
            ConfigSeverity.Advisory,
            SecureBootBlocksTestMode
            + "Turning Secure Boot off lowers this PC's boot security, and on a BitLocker-protected "
            + "PC it can ask for the 48-digit recovery key at the next boot - find that key before "
            + "changing anything. If you would rather not, the alternative is a driver Windows "
            + "already trusts: stock Windows HID keeps the pointer working without any of this.",
            label,
            url);
    }

    static ConfigFact MemoryIntegrityFact(
        SelfSignedRole role, bool? hvciOn, bool? selfSigned, bool v3)
    {
        var (label, url) = SigningDoc(v3);
        var clause = SelfSignedClause(role, selfSigned);

        if (hvciOn is null)
        {
            return new ConfigFact(
                MemoryIntegrityFactId,
                "Memory integrity state is unknown",
                ConfigSeverity.Advisory,
                "The tray could not read the Memory integrity (HVCI) policy on this PC, so it is "
                + "not claiming anything either way. " + clause
                + "Memory integrity must be off before Windows will load a self-signed driver.",
                label,
                url);
        }

        if (!hvciOn.Value)
        {
            return new ConfigFact(
                MemoryIntegrityFactId,
                "Memory integrity is off",
                ConfigSeverity.Ok,
                "Nothing here is blocking a self-signed driver. Memory integrity off does lower "
                + "your security for as long as it stays off.",
                null,
                null);
        }

        return new ConfigFact(
            MemoryIntegrityFactId,
            "Memory integrity is on",
            SigningSeverity(role, selfSigned, prerequisiteBlocks: false),
            "Memory integrity (HVCI) is enabled, and Windows refuses to load a self-signed driver "
            + "while it is on. " + clause
            + "Turn it off in Windows Security -> Device security -> Core isolation, then reboot "
            + "this PC. Magic Tray will not change it for you.",
            label,
            url);
    }

    // A package that is merely absent is never Blocking: nothing on the PC is
    // broken by it, the mouse simply does not have the driver that would add
    // scroll. The install offers live in DriverInstaller; this only names where
    // the package comes from.
    static ConfigFact PackageFact(PackagePath path, bool? present)
    {
        var (name, source, url) = path switch
        {
            PackagePath.Kmdf => (
                "KMDF driver package",
                $"the {DriverPackageCatalog.PatchedKmdfServiceName} service or "
                    + $"{DriverPackageCatalog.PatchedKmdfSysFileName}",
                DriverPackageCatalog.V3RepoUrl),
            PackagePath.AppleFilterPatched => (
                "Patched Apple driver package",
                $"the {DriverPackageCatalog.AppleFilterServiceName} service",
                DriverPackageCatalog.V3RepoUrl),
            _ => (
                "Boot Camp driver package",
                $"the {DriverPackageCatalog.AppleFilterServiceName} service",
                DriverPackageCatalog.TealtadpolePageUrl),
        };

        if (present is null)
        {
            return new ConfigFact(
                DriverPackageFactId,
                name + " state is unknown",
                ConfigSeverity.Advisory,
                $"The tray could not read whether {source} is installed, so it is not claiming "
                + "anything either way.",
                "Open the driver page",
                url);
        }

        if (present.Value)
        {
            return new ConfigFact(
                DriverPackageFactId,
                name + " is installed",
                ConfigSeverity.Ok,
                $"This PC has {source}.",
                null,
                null);
        }

        var missing = path == PackagePath.AppleFilterBootCamp
            ? "Scroll on this mouse needs Apple's Boot Camp AppleWirelessMouse driver, which is "
                + "signed, so Test Mode is not required for it. Nothing else on this PC is broken "
                + "by its absence - the pointer and the battery percent still work."
            : "Scroll on the Magic Mouse 2024 (0323) needs this package, and it is self-signed, so "
                + "it also needs Test Mode. Nothing else on this PC is broken by its absence - the "
                + "pointer works and the battery percent usually still reads.";
        return new ConfigFact(
            DriverPackageFactId,
            name + " is not installed",
            ConfigSeverity.Advisory,
            missing,
            "Open the driver page",
            url);
    }

    // Fault B in docs/ENABLE-DISABLE.md:93-99: the mouse only emits its
    // multitouch stream once the Apple enable feature report {0xF1, 0x02, 0x01}
    // has been sent, and the ONLY thing allowed to send it is the driver
    // package's own watcher. A second sender in the tray is an explicit
    // non-goal, so every fact here names the driver package as the owner of the
    // fix and offers a page to read, never an action to run.
    static ConfigFact WatcherFact(DeviceDiagReader.F1WatcherState watcher, DateTime nowUtc)
    {
        const string Owner =
            "That watcher, mm-auto-f1-watcher.ps1, ships with the "
            + DriverPackageCatalog.V3RepoName + " driver package and is the only thing that may "
            + "send the enable report. Magic Tray never sends it, so the fix belongs to the driver "
            + "package, not to the tray.";
        const string WhyItMatters =
            "The Magic Mouse only emits its multitouch stream after it receives the Apple enable "
            + "report, so the wheel can be dead after a reboot with a perfectly healthy driver. ";

        if (watcher.Installed is null)
        {
            return new ConfigFact(
                WatcherFactId,
                "Multitouch watcher state is unknown",
                ConfigSeverity.Advisory,
                "The tray could not read the driver package's multitouch watcher on this PC, so it "
                + "is not claiming anything either way. " + Owner,
                "Open the v3 driver repository",
                DriverPackageCatalog.V3RepoUrl);
        }

        if (!watcher.Installed.Value)
        {
            return new ConfigFact(
                WatcherFactId,
                "Multitouch watcher is not installed",
                ConfigSeverity.Advisory,
                WhyItMatters + Owner,
                "Open the v3 driver repository",
                DriverPackageCatalog.V3RepoUrl);
        }

        // Three states the tray used to collapse into one: running with a fresh
        // heartbeat, installed but silent, and installed but DEAD with a reason
        // it printed itself. The third is checked first because it is the
        // strongest evidence in the log - a self-declared death outranks both
        // the silence that follows it and any older heartbeat before it, and
        // DeviceDiagReader only reports FatalReason when nothing the watcher
        // wrote came after it.
        //
        // Severity is Advisory, not Blocking, on purpose. The watcher is dead
        // now and says so, which is a real finding - but this repo reserves
        // Blocking for states the TRAY can prove are breaking the mouse right
        // now, and it cannot: the mouse may well have been enabled before the
        // watcher died, and multitouch cannot be read back (the LastAclReceived
        // measurement was rejected, docs/ENABLE-DISABLE.md:155). The fix is
        // also not the tray's to make - it belongs to the driver package - and
        // Blocking would promise an action the tray must never take.
        if (watcher.FatalReason is not null)
        {
            return new ConfigFact(
                WatcherFactId,
                "Multitouch watcher stopped with an error",
                ConfigSeverity.Advisory,
                "The watcher is installed and it is not running: its log ends with the line it "
                + "printed as it quit, quoted here exactly - \"" + watcher.FatalReason + "\". "
                + "That is the watcher's own account of why it stopped, not a guess by the tray, "
                + "and nothing has been written to the log since. " + WhyItMatters + Owner,
                "Open the v3 driver repository",
                DriverPackageCatalog.V3RepoUrl);
        }

        if (watcher.LastHeartbeatUtc is null)
        {
            return new ConfigFact(
                WatcherFactId,
                "Multitouch watcher heartbeat was not found",
                ConfigSeverity.Advisory,
                "The watcher is installed, but its log carries no heartbeat line and no error of "
                + "its own, so the tray cannot confirm it is running and is not claiming it is "
                + "broken either - this is missing evidence, not a fault. " + Owner,
                "Open the v3 driver repository",
                DriverPackageCatalog.V3RepoUrl);
        }

        // A heartbeat stamped in the future is clock skew between the watcher's
        // local time and ours, not staleness. Never report it as a fault.
        var age = nowUtc - watcher.LastHeartbeatUtc.Value;
        if (age > WatcherStaleAfter)
        {
            var minutes = ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture);
            return new ConfigFact(
                WatcherFactId,
                "Multitouch watcher is not reporting",
                ConfigSeverity.Advisory,
                $"The watcher is installed but its last heartbeat is {minutes} minutes old, and it "
                + "writes one every 5 minutes. " + WhyItMatters + Owner,
                "Open the v3 driver repository",
                DriverPackageCatalog.V3RepoUrl);
        }

        if (watcher.LastF1Ok == false)
        {
            return new ConfigFact(
                WatcherFactId,
                "The last multitouch enable report failed",
                ConfigSeverity.Advisory,
                "The watcher is running, and the last enable report it sent this mouse did not "
                + "succeed, which is what a dead wheel looks like from here. " + Owner,
                "Open the v3 driver repository",
                DriverPackageCatalog.V3RepoUrl);
        }

        return new ConfigFact(
            WatcherFactId,
            "Multitouch watcher is running",
            ConfigSeverity.Ok,
            "The driver package's watcher is installed and reporting, so the mouse gets its Apple "
            + "multitouch enable report after every reconnect.",
            null,
            null);
    }

    // ---- rival driver packages -------------------------------------------

    // Windows keeps every driver package that was ever installed, and more
    // than one of them can declare the same hardware id. Measured on the
    // reference PC (2026-09-16): FOUR installed packages declare the 0323's
    // Bluetooth hardware id, and every one of them matches it EXACTLY, so they
    // all tie on how well they fit the device. Windows breaks that tie with
    // the package date in the INF's DriverVer, then the version. That is the
    // mechanism behind "my scroll was fine, then Windows put me back on the
    // wrong driver": nobody chose it, the newest-dated claimant simply won the
    // next rescan.
    //
    // Three things this fact must never become.
    //
    //   1. It is never Blocking. Nothing is broken - the mouse works, on a
    //      driver that is loaded and running. This is a risk the tray can see
    //      coming, not a defect it has measured, and Blocking in this file is
    //      reserved for a state proven broken NOW (see the header).
    //   2. Several claimants is not a fault. Millions of PCs carry two or
    //      three packages for one device and never rebind, so the mere count
    //      is not reportable; only the ACTIONABLE shape is - the package in
    //      use is not the one that wins the tie-break today, so a rescan can
    //      move the user off it.
    //   3. It never guesses. The claim in use must be identifiable AND dated
    //      before anything can be said to outrank it, because "we could not
    //      read a date" must not be allowed to read as "something newer is
    //      sitting there".
    //
    // SignerName and SignerScore are carried on DriverClaim and deliberately
    // take no part in this judgement: on the reference PC three of the four
    // claimants carry the same local publisher, so signer trust separates
    // nothing here, and the tie-break that was actually measured is the date.
    static ConfigFact? RivalClaimantFact(IReadOnlyList<DriverClaim>? claims, bool v3)
    {
        // null is a read that did not happen; one claimant is the whole story
        // of this device. Neither is a question worth a line.
        if (claims is null || claims.Count < 2)
            return null;

        DriverClaim? bound = null;
        foreach (var claim in claims)
        {
            if (!claim.IsBound)
                continue;
            // Two packages both reading as the one in use is a contradiction,
            // not a finding. Say nothing rather than pick one.
            if (bound is not null)
                return null;
            bound = claim;
        }

        // Nothing in the list is the package in use - a mouse on a stock
        // Windows driver, or an INF path that matched none of the claimants -
        // so there is no "yours" to rank anything against. And a bound package
        // with no date cannot be ranked at all.
        if (bound is null || bound.DriverDate is null)
            return null;

        DriverClaim? challenger = null;
        foreach (var claim in claims)
        {
            if (claim.IsBound || !Outranks(claim, bound))
                continue;
            if (challenger is null || Outranks(claim, challenger))
                challenger = claim;
        }

        var count = claims.Count.ToString(CultureInfo.InvariantCulture);
        var (label, url) = RivalDoc(v3);

        if (challenger is null)
        {
            // The bound package wins the tie-break, so a rescan lands back on
            // the same driver. This is an Ok fact rather than silence for the
            // reason ConfigFactView.cs:77-83 gives about the section as a
            // whole: an absent row is indistinguishable from a tray that never
            // looked, and this is a question users ask ("will Windows change
            // my driver again?") that the tray has just answered with a
            // measurement. It sorts last, renders as "OK: ...", and carries no
            // action - the same shape as "Test Mode is on" and "Multitouch
            // watcher is running", which are also shown only so the user can
            // see the check ran.
            return new ConfigFact(
                RivalClaimantFactId,
                "Other driver packages are installed, and yours is the one Windows picks",
                ConfigSeverity.Ok,
                $"{count} driver packages on this PC are installed for this mouse. That is normal "
                + "- Windows keeps every driver package that was ever installed for a device. When "
                + "it has to choose between them it takes the one with the newest package date, "
                + $"and today that is the one your mouse is already using ({DescribeClaim(bound)}), "
                + "so a reconnect or a reboot lands back on the same driver. Nothing to do.",
                null,
                null);
        }

        return new ConfigFact(
            RivalClaimantFactId,
            "Another installed driver package could take over this mouse",
            ConfigSeverity.Advisory,
            $"{count} driver packages on this PC are installed for this mouse, and Windows - not "
            + "you - decides which one it uses. It decides by package date: the newest-dated "
            + $"package wins. Your mouse is using {DescribeClaim(bound)}. The newest one installed "
            + $"is {DescribeClaim(challenger)}, so your mouse is not on the package Windows would "
            + "pick today. "
            + "Nothing is broken right now and the mouse keeps working, but the next time Windows "
            + "looks at this device from scratch - a reboot, a driver update, or re-pairing the "
            + "mouse - it can move you onto that package instead. That is worth knowing because "
            + "the packages do not behave the same: the Magic Mouse 2024 KMDF driver is the one "
            + "that gives you a scroll speed you can tune and a battery percent read straight from "
            + "the mouse, so being moved off it takes both of those away and usually shows up as "
            + "scrolling that changed overnight. Two ways to deal with it. If Windows does switch "
            + "you, pick the driver you want again from Magic Tray's driver step. To stop it "
            + "happening at all, remove the package you do not want yourself: in an administrator "
            + $"Command Prompt, pnputil /delete-driver {challenger.InfName}. Magic Tray will not "
            + "remove a driver package for you, and removing the wrong one costs you the driver it "
            + "belongs to, so check the name first.",
            label,
            url);
    }

    // The tie-break Windows applies once every claimant matches the hardware
    // id exactly: newer package date first, then the higher version. A claim
    // with no date can never be shown to outrank one that has a date - a
    // missing reading is not evidence of a newer package - and the version is
    // only consulted when both dates are known and identical, which is the
    // order the reference PC's four claimants were measured to resolve in.
    static bool Outranks(DriverClaim a, DriverClaim b)
    {
        if (a.DriverDate is null || b.DriverDate is null)
            return false;

        int byDate = a.DriverDate.Value.Date.CompareTo(b.DriverDate.Value.Date);
        if (byDate != 0)
            return byDate > 0;

        if (a.DriverVersion is null || b.DriverVersion is null)
            return false;
        return a.DriverVersion > b.DriverVersion;
    }

    // Enough to tell two packages apart and to type the right name into
    // pnputil: the oemNN.inf Windows knows it by, its date, and who published
    // it. No rank, no hardware id, no filter names - none of that helps a user
    // decide which package they want to keep.
    static string DescribeClaim(DriverClaim claim)
    {
        var text = claim.InfName;
        if (claim.DriverDate is not null)
            text += ", dated " + claim.DriverDate.Value.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        if (claim.DriverVersion is not null)
            text += ", version " + claim.DriverVersion.ToString();
        if (!string.IsNullOrWhiteSpace(claim.Provider))
            text += ", from " + claim.Provider.Trim();
        return text;
    }

    // Same destinations the signing facts use, because they are the pages that
    // describe which driver a given mouse should be on. This fact executes
    // nothing: the caller opens the page and that is all.
    static (string Label, string Url) RivalDoc(bool v3) =>
        v3
            ? ("Open the Magic Mouse v3 page", V3DocUrl)
            : ("Open the driver page for this mouse", LegacyDocUrl);

    // ---- readings --------------------------------------------------------

    // Registry first, and the registry answers this on a normally configured PC
    // with no elevation at all: the loader publishes the active boot options as
    // Control\SystemStartOptions, and TESTSIGNING appears there when test
    // signing is on. That is the same value DeviceRepair's blocked-filter check
    // reads (DeviceRepair.cs:236-240, docs/ENABLE-DISABLE.md:182).
    //
    // Only when that value is absent or empty - which is ambiguous, because a PC
    // with no boot options at all looks identical to one we could not read - do
    // we fall back to exactly one `bcdedit /enum {current}` READ. bcdedit needs
    // elevation, so an unelevated tray simply gets null, which can never become
    // Blocking.
    // internal, not private: the Test Mode confirmation dialog re-reads this at
    // click time rather than trusting the fact the panel was built from, so the
    // command it is about to run is decided from the state the PC is in NOW.
    internal static bool? ReadTestSigningOn()
    {
        try
        {
            using var control = Registry.LocalMachine.OpenSubKey(ControlKey, writable: false);
            if (control?.GetValue("SystemStartOptions") is string options
                && !string.IsNullOrWhiteSpace(options))
            {
                return options.Contains("TESTSIGNING", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SYSTEM_CONFIG_TESTSIGNING_REG_FAILED err={ex.Message}");
        }

        return ReadTestSigningFromBcdedit();
    }

    // The ONE process this file may start, and it is a read. `/enum {current}`
    // prints "testsigning Yes" only when the flag is set, so absence means off
    // - but absence also describes a run that failed, so the output has to
    // prove it really was a boot-entry dump before absence is believed.
    static bool? ReadTestSigningFromBcdedit()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "bcdedit.exe",
                Arguments = "/enum {current}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
                return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                try { p.WaitForExit(1000); } catch { }
                Logger.Log("SYSTEM_CONFIG_BCDEDIT_TIMEOUT");
                return null;
            }

            var text = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();

            // "Access is denied" / "could not be opened" leave no identifier
            // line, and an exit code we cannot trust.
            if (p.ExitCode != 0)
                return null;
            if (text.IndexOf("identifier", StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            int idx = text.IndexOf("testsigning", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return false;

            var line = text[idx..];
            int end = line.IndexOf('\n');
            if (end >= 0)
                line = line[..end];
            if (line.Contains("Yes", StringComparison.OrdinalIgnoreCase))
                return true;
            if (line.Contains("No", StringComparison.OrdinalIgnoreCase))
                return false;
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"SYSTEM_CONFIG_BCDEDIT_FAILED err={ex.Message}");
            return null;
        }
    }

    // HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\
    // HypervisorEnforcedCodeIntegrity : Enabled is the CONFIGURED HVCI
    // scenario, which is what a user turns on and off in Windows Security ->
    // Core isolation -> Memory integrity.
    //
    // It has THREE answers, not two, and that is why it is not a bool?. The
    // consumers resolve the third one in opposite directions:
    //
    //   On             Enabled is present and non-zero.
    //   Off            Enabled is present and zero. This is the only state
    //                  that proves somebody - this app's own fix, or the user
    //                  in Windows Security - turned Memory integrity off, and
    //                  it is what puts the undo on the menu (TrayApp).
    //   NotConfigured  no Control key, no scenario subkey, or no Enabled
    //                  value. We looked, and it is not there: for the fact row
    //                  that is "not on", and for the planner it is a PC whose
    //                  Enabled=0 write is a harmless no-op worth doing anyway.
    //   Unreadable     the probe itself failed, or Enabled held a type nothing
    //                  can read as a number. No evidence of anything, and the
    //                  one state the fact row must not report as a measurement.
    //
    // Collapsing NotConfigured and Unreadable into one null is what the first
    // version of this did, and it cost both callers something: the row claimed
    // "Memory integrity is off" on a PC it had failed to read, and the undo row
    // appeared on every PC that had simply never configured HVCI.
    internal enum HvciState { On, Off, NotConfigured, Unreadable }

    internal static HvciState ReadHvciConfigured()
    {
        try
        {
            using var control = Registry.LocalMachine.OpenSubKey(ControlKey, writable: false);
            if (control is null)
                return HvciState.NotConfigured;

            using var scenario = Registry.LocalMachine.OpenSubKey(HvciScenarioKey, writable: false);
            if (scenario is null)
                return HvciState.NotConfigured;

            return scenario.GetValue("Enabled") switch
            {
                int i => i != 0 ? HvciState.On : HvciState.Off,
                uint u => u != 0 ? HvciState.On : HvciState.Off,
                long l => l != 0 ? HvciState.On : HvciState.Off,
                null => HvciState.NotConfigured,
                _ => HvciState.Unreadable,
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"SYSTEM_CONFIG_HVCI_FAILED err={ex.Message}");
            return HvciState.Unreadable;
        }
    }

    // What SigningFix.BuildPlan is owed: "is Memory integrity going to block a
    // self-signed driver?" Anything short of a measured Off needs the Enabled=0
    // write, because writing 0 over 0 is a no-op and skipping it on a PC that
    // could not be read would leave the fix incomplete (SigningFix.cs).
    internal static bool? AsPlanReading(HvciState state) => state switch
    {
        HvciState.On => true,
        HvciState.Off => false,
        _ => null,
    };

    // What the fact row is owed: "is it on?" A state we looked for and did not
    // find is not on, so it can never escalate to Blocking; a state we failed
    // to read is null, and the row says so rather than claiming a measurement
    // it never took.
    internal static bool? ReadHvciOn() => ReadHvciConfigured() switch
    {
        HvciState.On => true,
        HvciState.Off or HvciState.NotConfigured => false,
        _ => null,
    };

    // HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State : UEFISecureBootEnabled
    // is published by the loader, is a plain DWORD, and reads without elevation
    // - unlike `bcdedit /enum`, and unlike Confirm-SecureBootUEFI, which throws
    // on a legacy-BIOS PC.
    //
    // Tri-state exactly like the two readings above. A MISSING key is a real
    // answer, not a failure: a legacy BIOS / CSM PC has no Secure Boot at all,
    // which for every decision this file makes is indistinguishable from Secure
    // Boot off. An exception is no evidence, and no evidence must never be read
    // as "blocked" - BuildPlan only refuses an Enable on a POSITIVE reading.
    internal static bool? ReadSecureBootOn()
    {
        try
        {
            using var state = Registry.LocalMachine.OpenSubKey(SecureBootStateKey, writable: false);
            if (state is null)
                return false;

            var value = state.GetValue("UEFISecureBootEnabled");
            if (value is null)
                return false;

            return value switch
            {
                int i => i != 0,
                uint u => u != 0,
                long l => l != 0,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"SYSTEM_CONFIG_SECUREBOOT_FAILED err={ex.Message}");
            return null;
        }
    }

    // Service key plus .sys on disk for KMDF, service key for the Apple filter:
    // the existing probes in DriverHealthChecker, which already build both paths
    // from DriverPackageCatalog. Their bool is widened to bool? so a probe that
    // threw is reported as no evidence rather than as an absent package.
    static bool? ReadPackagePresent(PackagePath path)
    {
        try
        {
            return path switch
            {
                PackagePath.Kmdf => (bool?)DriverHealthChecker.KmdfPackagePresent(),
                PackagePath.AppleFilterBootCamp => DriverHealthChecker.AppleFilterPackagePresent(),
                PackagePath.AppleFilterPatched => DriverHealthChecker.AppleFilterPackagePresent(),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"SYSTEM_CONFIG_PACKAGE_FAILED path={path} err={ex.Message}");
            return null;
        }
    }

    // Which installed driver packages declare this device's hardware id, and
    // which of them is the one currently bound. All of it lives in
    // DriverClaimReader: two reads under HKLM\SYSTEM\DriverDatabase plus the
    // bound INF name off the live devnode, no elevation, no process, nothing
    // that could provoke a rebind. Reached only after Check's early exit, so a
    // device with no driver story of ours never pays for it. A reader that
    // threw is no evidence, exactly like every other reading here.
    static IReadOnlyList<DriverClaim>? ReadClaims(string? pid)
    {
        try
        {
            return DriverClaimReader.ClaimsForPid(pid);
        }
        catch (Exception ex)
        {
            Logger.Log($"SYSTEM_CONFIG_CLAIMS_FAILED err={ex.Message}");
            return null;
        }
    }

    // Which driver file this device's scroll actually depends on. None means
    // there is no driver of ours in the story (stock Windows, no package, a
    // device we do not know), and nothing is read.
    enum SigningSubject { None, AppleFilter, Kmdf }

    static SigningSubject SubjectFor(DeviceKind kind, string? pid, DriverStatus? status)
    {
        if (status is null)
            return SigningSubject.None;

        if (IsV3Mouse(kind, pid))
        {
            return status.Value switch
            {
                DriverStatus.PatchedKmdf => SigningSubject.Kmdf,
                DriverStatus.NotBound => SigningSubject.Kmdf,
                DriverStatus.PathAPatched => SigningSubject.AppleFilter,
                _ => SigningSubject.None,
            };
        }

        if (!IsLegacyMouse(kind, pid))
            return SigningSubject.None;

        return status.Value switch
        {
            DriverStatus.Ok => SigningSubject.AppleFilter,
            DriverStatus.NotBound => SigningSubject.AppleFilter,
            _ => SigningSubject.None,
        };
    }

    // THE reading the whole signing gate hangs on: is the driver file this
    // device depends on signed by something Windows trusts, or only by itself?
    // A file read and a registry read - no process, no elevation, no write.
    //
    //   false  at least one signature is CA-issued, so Windows has a chain to
    //          follow and Test Mode is not what decides whether it loads
    //   true   every signature on the file is self-issued
    //   null   no signature in the file (a catalog-only driver such as stock
    //          HidBth.sys), no file, or a read that failed
    static bool? ReadBoundDriverSelfSigned(DeviceKind kind, string? pid, DriverStatus? status)
    {
        var subject = SubjectFor(kind, pid, status);
        if (subject == SigningSubject.None)
            return null;

        try
        {
            bool anyTrusted = false;
            foreach (var image in DriverImages(subject))
            {
                var verdict = SelfSignedVerdict(image);
                if (verdict == true)
                    return true;
                if (verdict == false)
                    anyTrusted = true;
            }

            return anyTrusted ? false : null;
        }
        catch (Exception ex)
        {
            Logger.Log($"SYSTEM_CONFIG_SIGNATURE_FAILED subject={subject} err={ex.Message}");
            return null;
        }
    }

    // Every installed service in the family, read from its own ImagePath,
    // because that is the file Windows loads and because Check is not told
    // which variant is bound: the KMDF package ships MagicMouseDriver204Scroll
    // beside MagicMouseDriver (RepairPlanner.cs:495-501). One self-signed file
    // is enough for signing policy to matter; it takes every file on this PC
    // reading as trusted to say that it does not.
    static List<string> DriverImages(SigningSubject subject)
    {
        var images = new List<string>(2);

        void Add(string? path)
        {
            if (path is not null && !images.Contains(path, StringComparer.OrdinalIgnoreCase))
                images.Add(path);
        }

        using (var services = Registry.LocalMachine.OpenSubKey(ServicesKey, writable: false))
        {
            if (services is not null)
            {
                foreach (var name in services.GetSubKeyNames())
                {
                    bool inFamily = subject == SigningSubject.Kmdf
                        ? RepairPlanner.IsKmdfFamily(name)
                        : RepairPlanner.IsAppleFamily(name);
                    if (inFamily)
                        Add(ServiceImagePath(name));
                }
            }
        }

        // No service key at all: the package may still be on disk under the
        // catalog file name, which is what DriverHealthChecker falls back to.
        if (images.Count == 0)
        {
            Add(DriversFile(subject == SigningSubject.Kmdf
                ? DriverPackageCatalog.PatchedKmdfSysFileName
                : DriverPackageCatalog.AppleFilterServiceName + ".sys"));
        }

        return images;
    }

    static string? ServiceImagePath(string service)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"{ServicesKey}\{service}", writable: false);
        if (key?.GetValue("ImagePath") is not string raw)
            return null;

        var path = ExpandImagePath(raw);
        return path is not null && File.Exists(path) ? path : null;
    }

    // Service ImagePath is a kernel path: \SystemRoot\System32\drivers\x.sys on
    // both packages here, but \??\C:\..., %SystemRoot%\... and a bare relative
    // path are all legal and all mean the same Windows directory.
    static string? ExpandImagePath(string raw)
    {
        var path = raw.Trim().Trim('"');
        if (path.Length == 0)
            return null;

        const string NtPrefix = @"\??\";
        const string SystemRoot = @"\SystemRoot\";
        const string SystemRootVar = @"%SystemRoot%\";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (path.StartsWith(NtPrefix, StringComparison.Ordinal))
            path = path[NtPrefix.Length..];
        if (path.StartsWith(SystemRoot, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windows, path[SystemRoot.Length..]);
        if (path.StartsWith(SystemRootVar, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windows, path[SystemRootVar.Length..]);
        if (path.Length > 1 && path[1] == ':')
            return path;
        return Path.Combine(windows, path.TrimStart('\\'));
    }

    static string? DriversFile(string fileName)
    {
        var path = Path.Combine(Environment.SystemDirectory, "drivers", fileName);
        return File.Exists(path) ? path : null;
    }

    // Self-signed means every Authenticode signature on the file was issued by
    // itself. That is the whole test: no chain building, and no opinion about
    // WHICH certificate authority a driver came from, because a locally
    // installed root would make a chain build succeed while the kernel still
    // refuses the driver without Test Mode - so trusting a chain would be the
    // one answer we must not give.
    static bool? SelfSignedVerdict(string path)
    {
        if (!File.Exists(path))
            return null;

        var pkcs7 = ReadCertificateTable(path);
        if (pkcs7 is null)
            return null;

        bool anySigner = false;
        bool anyCaIssued = false;
        CollectSignerTrust(pkcs7, 0, ref anySigner, ref anyCaIssued);
        return anySigner ? !anyCaIssued : null;
    }

    // The PKCS#7 blob in the PE certificate table
    // (IMAGE_DIRECTORY_ENTRY_SECURITY, whose VirtualAddress is a file offset).
    // Authenticode writes one WIN_CERTIFICATE entry, so the first is read.
    static byte[]? ReadCertificateTable(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);

        file.Position = 0x3C;
        int pe = reader.ReadInt32();
        if (pe <= 0 || pe + 24 >= file.Length)
            return null;

        file.Position = pe;
        if (reader.ReadUInt32() != 0x00004550)   // "PE\0\0"
            return null;

        file.Position = pe + 24;                 // optional header
        int directories = pe + 24 + (reader.ReadUInt16() == 0x20B ? 112 : 96);
        if (directories + 4 * 8 + 8 > file.Length)
            return null;

        file.Position = directories + 4 * 8;     // security directory
        uint offset = reader.ReadUInt32();
        uint size = reader.ReadUInt32();
        if (offset == 0 || size < 8 || (long)offset + size > file.Length)
            return null;

        file.Position = offset;
        uint length = reader.ReadUInt32();
        reader.ReadUInt16();                     // wRevision
        if (reader.ReadUInt16() != 0x0002)       // WIN_CERT_TYPE_PKCS_SIGNED_DATA
            return null;
        if (length <= 8 || length > size)
            return null;

        return reader.ReadBytes((int)(length - 8));
    }

    // Primary signature plus every nested one. Depth is capped because the
    // attribute is attacker-shaped data: a file we did not build decides how
    // deep it goes.
    static void CollectSignerTrust(byte[] pkcs7, int depth, ref bool anySigner, ref bool anyCaIssued)
    {
        if (depth > 4)
            return;

        var cms = new SignedCms();
        try
        {
            cms.Decode(pkcs7);
        }
        catch (Exception ex)
        {
            Logger.Log($"SYSTEM_CONFIG_SIGNATURE_DECODE_FAILED depth={depth} err={ex.Message}");
            return;
        }

        foreach (var signer in cms.SignerInfos)
        {
            var cert = signer.Certificate;
            if (cert is not null)
            {
                anySigner = true;
                if (!string.Equals(cert.Subject, cert.Issuer, StringComparison.Ordinal))
                    anyCaIssued = true;
            }

            foreach (var attribute in signer.UnsignedAttributes)
            {
                if (attribute.Oid.Value != NestedSignatureOid)
                    continue;
                foreach (var value in attribute.Values)
                    CollectSignerTrust(value.RawData, depth + 1, ref anySigner, ref anyCaIssued);
            }
        }
    }

    // ---- device identity -------------------------------------------------

    // The PID is authoritative when we have one - a 0323 is a 0323 whatever the
    // caller's DeviceKind says - and DeviceKind answers only when it is absent.
    static string NormalizePid(string? pid)
    {
        if (string.IsNullOrWhiteSpace(pid))
            return "";
        var text = pid.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return text.ToLowerInvariant();
    }

    internal static bool IsV3Mouse(DeviceKind kind, string? pid)
    {
        var p = NormalizePid(pid);
        if (p.Length == 4)
            return DriverHealthChecker.IsV3Pid(p);
        return kind == DeviceKind.MagicMouseV3;
    }

    static bool IsLegacyMouse(DeviceKind kind, string? pid)
    {
        var p = NormalizePid(pid);
        if (p.Length == 4)
        {
            return !DriverHealthChecker.IsV3Pid(p)
                && Array.Exists(DriverHealthChecker.KnownMousePids, known => known == p);
        }
        return kind is DeviceKind.MagicMouseV1 or DeviceKind.MagicMouseV2;
    }

    static int CountOf(IReadOnlyList<ConfigFact> facts, ConfigSeverity severity)
    {
        int n = 0;
        foreach (var f in facts)
            if (f.Severity == severity)
                n++;
        return n;
    }

    static string Describe(bool? value) =>
        value is null ? "unknown" : value.Value ? "true" : "false";

    static string DescribeCount(IReadOnlyList<DriverClaim>? claims) =>
        claims is null ? "unknown" : claims.Count.ToString(CultureInfo.InvariantCulture);
}
