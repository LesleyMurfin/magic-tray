// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class RepairPlannerTests
{
    // The service name the 0323 device on the reference PC actually binds, read
    // verbatim out of its LowerFilters value. It is deliberately a literal and
    // not DriverPackageCatalog.PatchedKmdfServiceName: the whole point of the
    // corrected contract is that BoundFilterName reports what the registry says,
    // not what the catalog wishes were installed.
    private const string BoundKmdfVariant = "MagicMouseDriver204Scroll";

    // The older build of the same KMDF filter, still named in the BTHENUM
    // DEVICE key's LowerFilters on the reference PC while its service is
    // Stopped. Windows loads both keys' LowerFilters onto one stack.
    private const string StaleKmdfVariant = "MagicMouseDriver";

    // Named defaults: a healthy-looking v3 snapshot. Each test overrides only
    // the fields that define the incident it is about. configEnabled defaults to
    // true meaning "this PC has an explicit enabled_<pid>=true entry", i.e. the
    // device is owned; null means there is no entry for the PID at all.
    private static DeviceSnapshot Snap(
        string pid = "0323",
        int bthenumLiveCount = 0,
        int usbPhantomCount = 0,
        bool col01Present = false,
        bool col02Present = false,
        string? boundFilterName = null,
        // Driver-package and service evidence, both tri-state for the same
        // reason FilterInStack is: their producers can fail. false is a real
        // reading (the package is not on this PC / the service answered and is
        // not RUNNING), null is "the probe could not answer" and is never a
        // finding. false stays the default so every test written before the
        // tri-state still describes the incident it was written for.
        bool? filterPackagePresent = false,
        bool? filterServiceRunning = false,
        bool? configEnabled = true,
        // Every family filter named on the stack, in reader precedence order.
        // Default is "no family filter seen"; a test that cares about the
        // rival-filter rule spells the whole list out. Arrays cannot be
        // optional-parameter defaults, hence the null sentinel.
        string[]? filterCandidates = null,
        // DEVPKEY_Device_Stack evidence: true means the bound filter really is
        // in the live stack, which is the healthy shape and therefore the
        // default. false is the post-reboot incident (registered and RUNNING
        // but not attached), null is "the property could not be read".
        bool? filterInStack = true,
        // Capability evidence, all defaulted to the healthy/no-news shape so
        // every existing test still describes exactly the incident it was
        // written for.
        //
        // pointerChildLive: true means the Bluetooth COL01 HID child of this
        // PID resolves as present. false is "the child key is there and does
        // NOT resolve" (no pointer), null is "no such key, or the lookup
        // failed".
        bool? pointerChildLive = true,
        // multitouchAdvancing: true means the bound filter's ACL translate
        // counter moved between two samples, so the multitouch stream is
        // provably flowing. null is no evidence - first sample, counter
        // standing still (idle and broken are indistinguishable), nothing
        // bound, or a failure. It is never false, and no rule may consume it.
        bool? multitouchAdvancing = true,
        // scrollWatcherHealthy: the driver package's multitouch watcher,
        // machine-wide. Only wording depends on it, never a finding.
        bool? scrollWatcherHealthy = true,
        // lastBatteryPct: the poller's last reading with its sentinels (-1 no
        // reading, -2 present but blocked, -3 three consecutive no-readings).
        // null - never measured - is the default, so no battery rule can fire
        // in a test that is not about the battery.
        int? lastBatteryPct = null)
        => new(
            pid,
            bthenumLiveCount,
            usbPhantomCount,
            col01Present,
            col02Present,
            boundFilterName,
            filterPackagePresent,
            filterServiceRunning,
            configEnabled,
            filterCandidates ?? [],
            filterInStack,
            pointerChildLive,
            multitouchAdvancing,
            scrollWatcherHealthy,
            lastBatteryPct);

    [Fact]
    public void PlanOne_V3StoppedButBoundAfterCharge_IsAutoFixableParentRestart()
    {
        // v3 wheel-dead-after-charge shape: pointer + battery fine, wheel dead,
        // because the service named in LowerFilters is not running. The bound
        // name is the resolved one ("MagicMouseDriver204Scroll"), so the finding
        // must key off that service and not off the catalog default.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            usbPhantomCount: 7,
            col01Present: true,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: false));

        Assert.NotNull(finding);
        Assert.Equal("0323", finding.Pid);
        Assert.Equal(RepairProblem.FilterStoppedButBound, finding.Problem);
        Assert.Equal(RepairAction.RestartBtHidParent, finding.Action);
        Assert.True(finding.AutoFixable);
    }

    [Fact]
    public void PlanOne_BoundFilterRunningUnderVariantName_IsHealthy()
    {
        // Regression for the false positive measured on live hardware: the 0323
        // device binds MagicMouseDriver204Scroll and that service is RUNNING, so
        // there is no problem to report.
        //
        // The same PC also has an older, installed-but-UNBOUND MagicMouseDriver
        // service sitting in the Stopped state. Unbound means its name appears
        // in NO LowerFilters value, so the stack has exactly one candidate and
        // there is nothing to strip either: a service key that is merely
        // installed must never produce a finding. DeviceSnapshot carries the
        // RESOLVED bound service, so FilterServiceRunning is the state of
        // MagicMouseDriver204Scroll and nothing else. Reading the catalog
        // constant's service instead is exactly the misdiagnosis this test pins.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            usbPhantomCount: 7,
            col01Present: true,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant]));

        Assert.Null(finding);
    }

    [Fact]
    public void PlanOne_TwoRivalFiltersOnOneStack_IsAutoFixableStaleFilterRemoval()
    {
        // Measured on the reference PC for the 2024 Magic Mouse (PID 0323):
        //   BTHENUM device key   LowerFilters = [MagicMouseDriver]        (Stopped)
        //   BTHENUM instance key LowerFilters = [MagicMouseDriver204Scroll] (Running)
        // Windows applies both values to the same stack, so PnP is told to load
        // two rival builds of the same vendor filter. The wheel died and the
        // COL02 battery report came back zeroed. A restart cannot fix it - the
        // stale name stays registered - so the finding must be the removal one.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            usbPhantomCount: 7,
            col01Present: true,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [StaleKmdfVariant, BoundKmdfVariant]));

        Assert.NotNull(finding);
        Assert.Equal("0323", finding.Pid);
        Assert.Equal(RepairProblem.ConflictingFilters, finding.Problem);
        Assert.Equal(RepairAction.RemoveStaleFilter, finding.Action);
        Assert.True(finding.AutoFixable);

        // The user has to be told which name goes, which one stays, and that
        // the mouse survives the repair - the fear this whole flow answers.
        Assert.Contains(StaleKmdfVariant, finding.Detail);
        Assert.Contains(BoundKmdfVariant, finding.Detail);
        Assert.Contains("never unpaired", finding.Detail);
    }

    [Fact]
    public void StaleFilters_ReturnsEveryCandidateExceptTheRunningOne()
    {
        // Exactly what the elevated repair is allowed to strip out of
        // LowerFilters: the leftover, never the running filter.
        var conflicted = Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [StaleKmdfVariant, BoundKmdfVariant]);

        Assert.Equal([StaleKmdfVariant], RepairPlanner.StaleFilters(conflicted));
    }

    [Fact]
    public void StaleFilters_MatchesTheBoundNameCaseInsensitivelyAndKeepsStackOrder()
    {
        // The registry spells the same service several ways across keys. Casing
        // of the names that ARE returned stays verbatim, because the repair
        // writes them straight back into a LowerFilters value.
        var snapshot = Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates:
            [
                StaleKmdfVariant,
                BoundKmdfVariant.ToUpperInvariant(),
                DriverPackageCatalog.AppleFilterServiceName,
            ]);

        Assert.Equal(
            [StaleKmdfVariant, DriverPackageCatalog.AppleFilterServiceName],
            RepairPlanner.StaleFilters(snapshot));
    }

    [Fact]
    public void StaleFilters_IsEmptyForASingleCandidateOrNothingBound()
    {
        // Nothing may be stripped off a correctly installed stack, and a stack
        // with nothing bound must never lose its last family filter name.
        var single = Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant]);
        var unbound = Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: null,
            filterPackagePresent: true,
            filterCandidates: [StaleKmdfVariant, BoundKmdfVariant]);

        Assert.Empty(RepairPlanner.StaleFilters(single));
        Assert.Empty(RepairPlanner.StaleFilters(unbound));
    }

    [Fact]
    public void PlanOne_SingleRunningFilterCandidate_IsNotAConflict()
    {
        // The normal, correctly installed PC: one family filter, bound and
        // running. One filter is not a conflict, so the rival-filter rule must
        // stay silent instead of offering to strip the only driver there is.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            col01Present: true,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant]));

        Assert.Null(finding);
    }

    [Fact]
    public void PlanOne_TwoCandidatesWithNoneRunning_IsStillTheStoppedFilterRestart()
    {
        // Both rules see two names, but nothing is running: the stack has no
        // working filter to keep, so removing one would fix nothing. The
        // restart keeps its rank 1.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: StaleKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: false,
            filterCandidates: [StaleKmdfVariant, BoundKmdfVariant]));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.FilterStoppedButBound, finding.Problem);
        Assert.Equal(RepairAction.RestartBtHidParent, finding.Action);
    }

    [Fact]
    public void PlanOne_FilterRunningButNotInTheDeviceStack_IsAutoFixableParentRestart()
    {
        // The post-reboot field report: after a Windows restart the tray said
        // the mouse was on the right driver and the wheel was dead anyway, and
        // the repair menu said "No problems found". Every service-level input
        // is the healthy one - bound filter, package installed, service
        // RUNNING - and the only thing that differs is DEVPKEY_Device_Stack:
        // PnP rebuilt this mouse's stack without the filter. That one piece of
        // evidence has to be enough to offer the connection restart.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            usbPhantomCount: 7,
            col01Present: true,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: false));

        Assert.NotNull(finding);
        Assert.Equal("0323", finding.Pid);
        Assert.Equal(RepairProblem.FilterNotInStack, finding.Problem);
        Assert.Equal(RepairAction.RestartBtHidParent, finding.Action);
        Assert.True(finding.AutoFixable);
        Assert.Equal(
            "Scroll wheel is dead - the driver is not attached to this mouse",
            finding.Title);
    }

    [Fact]
    public void PlanOne_FilterNotInStack_ClaimsThePointerOnlyWhenItWasMeasured()
    {
        // The detail text used to open "Pointer movement, clicking and the
        // battery reading all still work" on every machine this rule fired on,
        // while the rule reads neither PointerChildLive nor LastBatteryPct.
        // The battery half is gone for good; the pointer half is allowed only
        // on a measured true, which is the same evidence rule 2c would use to
        // say the opposite.
        var measuredPointer = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: false,
            pointerChildLive: true));
        var unreadPointer = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: false,
            pointerChildLive: null));

        Assert.NotNull(measuredPointer);
        Assert.Contains("Pointer movement and clicking still work", measuredPointer.Detail);

        Assert.NotNull(unreadPointer);
        Assert.Equal(RepairProblem.FilterNotInStack, unreadPointer.Problem);
        Assert.DoesNotContain("Pointer movement", unreadPointer.Detail);
        Assert.StartsWith("The scroll wheel does nothing.", unreadPointer.Detail);

        // Never claimed by either: nothing in this rule reads the battery.
        Assert.DoesNotContain("battery", measuredPointer.Detail);
        Assert.DoesNotContain("battery", unreadPointer.Detail);
    }

    [Fact]
    public void PlanOne_StackEvidenceUnknown_IsNotAFinding()
    {
        // null is "the stack could not be read", not "the filter is missing".
        // Without this guard every PC whose property read fails would be
        // nagged about a wheel that works, which is worse than missing the
        // diagnosis on it.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            col01Present: true,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: null));

        Assert.Null(finding);
    }

    [Fact]
    public void PlanOne_FilterProvenInTheDeviceStack_IsHealthy()
    {
        // The third leg of the same evidence: the stack was readable and the
        // filter IS attached, so this is the working mouse the field report
        // was compared against and it must stay silent.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            col01Present: true,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: true));

        Assert.Null(finding);
    }

    [Fact]
    public void PlanOne_StoppedFilterOutranksMissingStackAttachment()
    {
        // A stopped service is the stronger, older-known cause (the USB-C
        // charge incident) and its wording names the dead service, so rule 1
        // keeps its rank even though the filter is also out of the stack.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: false,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: false));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.FilterStoppedButBound, finding.Problem);
        Assert.Equal(RepairAction.RestartBtHidParent, finding.Action);
    }

    [Fact]
    public void PlanOne_ConflictingFiltersOutranksMissingStackAttachment()
    {
        // Two rival filter names plus an unattached filter: stripping the
        // leftover is the better-evidenced repair and it restarts the stack
        // anyway, so it subsumes the plain restart. Reporting the restart here
        // would leave the leftover registered and the rebuild would hit the
        // same conflict.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [StaleKmdfVariant, BoundKmdfVariant],
            filterInStack: false));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.ConflictingFilters, finding.Problem);
        Assert.Equal(RepairAction.RemoveStaleFilter, finding.Action);
    }

    [Fact]
    public void PlanOne_NotInStackWithNoDriverPackage_IsGuidedInstall()
    {
        // Of course the filter is not in the stack - it is not installed. A
        // connection restart cannot attach a driver that is not on the PC, so
        // the user must be sent to the install step instead.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: false,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: false));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.FilterPackageMissing, finding.Problem);
        Assert.Equal(RepairAction.InstallDriver, finding.Action);
        Assert.False(finding.AutoFixable);
    }

    [Fact]
    public void PlanOne_NotInStackOnACatalogDeviceThisPcNeverOwned_IsNotAFinding()
    {
        // The 22-findings regression must survive the new evidence: a catalog
        // PID with no live BTHENUM instance and no enabled_<pid> entry was
        // never on this PC, so "the filter is not in its stack" is not news
        // about anything the user owns.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0267",
            bthenumLiveCount: 0,
            usbPhantomCount: 0,
            boundFilterName: DriverPackageCatalog.AppleFilterServiceName,
            filterPackagePresent: true,
            filterServiceRunning: true,
            configEnabled: null,
            filterCandidates: [DriverPackageCatalog.AppleFilterServiceName],
            filterInStack: false));

        Assert.Null(finding);
    }

    [Fact]
    public void PlanOne_PointerChildNotPresent_IsAutoFixableParentRestart()
    {
        // The mouse is connected over Bluetooth and every driver-level input is
        // the healthy one, yet the COL01 HID child Windows drives the cursor
        // from does not resolve as present: the pointer is dead. Note that
        // col01Present is true here on purpose - that flag is a substring match
        // over Enum\HID and a charge-cable phantom COL01 key satisfies it, so
        // it cannot see this fault and must not suppress the finding.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            usbPhantomCount: 7,
            col01Present: true,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            pointerChildLive: false));

        Assert.NotNull(finding);
        Assert.Equal("0323", finding.Pid);
        Assert.Equal(RepairProblem.PointerChildMissing, finding.Problem);
        Assert.Equal(RepairAction.RestartBtHidParent, finding.Action);
        Assert.True(finding.AutoFixable);
        Assert.Contains("cursor does not move", finding.Detail);
        Assert.Contains("nothing is unpaired", finding.Detail);
    }

    [Fact]
    public void PlanOne_PointerChildEvidenceUnknownOrPresent_IsNotAFinding()
    {
        // null is "no COL01 child key for this PID, or the lookup failed", not
        // "the pointer is gone": a PC whose devnode lookup fails must never be
        // told its working mouse has no pointer. true is the healthy shape.
        var unknown = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            pointerChildLive: null));
        var present = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            pointerChildLive: true));

        Assert.Null(unknown);
        Assert.Null(present);
    }

    [Fact]
    public void PlanOne_StoppedFilterOutranksMissingPointerChild()
    {
        // Both are true after a stack that came up wrong. The stopped service
        // is the cause the user can be told about by name, and its repair is
        // the same connection restart, so rule 1 keeps its rank.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: false,
            filterCandidates: [BoundKmdfVariant],
            pointerChildLive: false));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.FilterStoppedButBound, finding.Problem);
    }

    [Fact]
    public void PlanOne_ScrollEvidenceAbsent_RaisesNothing()
    {
        // No automatic scroll finding exists, and none may come back. Measured
        // on the reference PC while scrolling WORKED, sampling Diag every
        // 2.5 s: LastAclReceived read 23, 23, 9, 23 while AclTranslateCount
        // advanced 142271 -> 142827. A rule that read the "compact" 9 as
        // multitouch-off would have accused a healthy mouse on sample 3.
        // Counter movement is therefore the only positive proof, and its
        // absence (null) is not evidence of anything: an idle mouse and a
        // mouse that stopped sending touch data look identical.
        var noEvidence = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            col01Present: true,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            multitouchAdvancing: null));
        // Same shape with a stalled watcher and a blocked battery reading, the
        // two things a dead multitouch stream would drag along on a mouse:
        // still nothing, because neither can prove the stream stopped.
        var noEvidenceWithStalledWatcher = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            multitouchAdvancing: null,
            scrollWatcherHealthy: false,
            lastBatteryPct: -2));
        var flowing = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            multitouchAdvancing: true));

        Assert.Null(noEvidence);
        Assert.Null(noEvidenceWithStalledWatcher);
        Assert.Null(flowing);
    }

    [Fact]
    public void PlanOne_KeyboardBatteryBlocked_IsGuidedPairingRecordPatch()
    {
        // -2 from KeyboardBatteryDevice is "present but blocked": the pairing
        // record this PC holds is missing the SDP Feature 0x47 cap Windows
        // wants before it will read the battery level. That has a specific,
        // guided fix, so it is the one battery shape worth reporting. Nothing
        // is bound and the package is present, so no filter rule interferes -
        // a keyboard rides no scroll filter.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0267",
            bthenumLiveCount: 1,
            boundFilterName: null,
            filterPackagePresent: true,
            lastBatteryPct: -2));

        Assert.NotNull(finding);
        Assert.Equal("0267", finding.Pid);
        Assert.Equal(RepairProblem.BatteryReadBlocked, finding.Problem);
        Assert.Equal(RepairAction.InstallDriver, finding.Action);
        Assert.False(finding.AutoFixable);
        Assert.Contains("typing works", finding.Detail);
        Assert.Contains("nothing is unpaired", finding.Detail);
    }

    [Fact]
    public void PlanOne_KeyboardBatteryOtherSentinels_RaiseNothing()
    {
        // -1 (no reading) and -3 (three consecutive no-readings collapsed by
        // AdaptivePoller) are silence, not a diagnosis: a keyboard nobody has
        // touched produces them all day. null is "never measured".
        static DeviceSnapshot Keyboard(int? pct) => Snap(
            pid: "0267",
            bthenumLiveCount: 1,
            boundFilterName: null,
            filterPackagePresent: true,
            lastBatteryPct: pct);

        Assert.Null(RepairPlanner.PlanOne(Keyboard(-1)));
        Assert.Null(RepairPlanner.PlanOne(Keyboard(-3)));
        Assert.Null(RepairPlanner.PlanOne(Keyboard(null)));
        Assert.Null(RepairPlanner.PlanOne(Keyboard(64)));
    }

    [Fact]
    public void PlanOne_MouseBatteryBlocked_RaisesNothing()
    {
        // A mouse reporting -2 or -3 has no actionable cause behind it: its
        // vendor battery report simply stops arriving whenever the multitouch
        // stream stops, and that cannot be proven (see
        // PlanOne_ScrollEvidenceAbsent_RaisesNothing). There is no pairing
        // record to patch on a mouse, so a finding here would fire on every
        // idle Magic Mouse and tell the user nothing they can act on.
        var blocked = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            lastBatteryPct: -2));
        var collapsed = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            lastBatteryPct: -3));

        Assert.Null(blocked);
        Assert.Null(collapsed);
    }

    [Fact]
    public void PlanOne_CapabilityFaultsOnACatalogDeviceThisPcNeverOwned_AreNotFindings()
    {
        // The 22-findings regression, again, against the capability evidence:
        // a catalog PID with no live BTHENUM instance and no enabled_<pid>
        // entry was never on this PC, so a dead pointer child and a blocked
        // battery reading for it are not news about anything the user owns.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0267",
            bthenumLiveCount: 0,
            usbPhantomCount: 0,
            configEnabled: null,
            pointerChildLive: false,
            multitouchAdvancing: null,
            scrollWatcherHealthy: false,
            lastBatteryPct: -2));

        Assert.Null(finding);
    }

    [Fact]
    public void PlanOne_V1RemovedInSettings_IsGuidedPairingNotAutoFix()
    {
        // Live 2026-09-06 v1 incident: disable + Settings Remove left zero PnP
        // and zero Enum instances. pnputil /enable-device has nothing to enable.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "030d",
            bthenumLiveCount: 0,
            usbPhantomCount: 0,
            configEnabled: false));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.NoInstances, finding.Problem);
        Assert.Equal(RepairAction.PairInWindows, finding.Action);
        Assert.False(finding.AutoFixable);
        Assert.False(string.IsNullOrWhiteSpace(finding.Detail));
    }

    [Fact]
    public void PlanOne_CatalogDeviceThisPcNeverOwned_IsNotAFinding()
    {
        // The 22-findings regression: the catalog sweeps every Apple device the
        // app knows about, so a PC with two Apple devices used to be told it had
        // 22 problems. No live instance, no phantom and NO explicit enabled_<pid>
        // entry (ConfigEnabled null) means the device was never here at all.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0267",
            bthenumLiveCount: 0,
            usbPhantomCount: 0,
            configEnabled: null));

        Assert.Null(finding);
    }

    [Fact]
    public void PlanOne_NoInstancesWithExplicitConfigEntry_IsStillReported()
    {
        // An explicit entry is proof the device was on this PC once, so its
        // disappearance is real news whichever way the entry points.
        var disabled = RepairPlanner.PlanOne(Snap(pid: "030d", configEnabled: false));
        var enabled = RepairPlanner.PlanOne(Snap(pid: "030d", configEnabled: true));

        Assert.NotNull(disabled);
        Assert.Equal(RepairProblem.NoInstances, disabled.Problem);
        Assert.NotNull(enabled);
        Assert.Equal(RepairProblem.NoInstances, enabled.Problem);
    }

    [Fact]
    public void PlanOne_UsbPhantomsOnly_NeedsAnExplicitConfigEntryToo()
    {
        // Charge-cable leftovers under Enum\USB are reported for a device this
        // PC knows, and stay silent for one it has never had an entry for.
        var known = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 0,
            usbPhantomCount: 7,
            configEnabled: true));
        var unknown = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 0,
            usbPhantomCount: 7,
            configEnabled: null));

        Assert.NotNull(known);
        Assert.Equal(RepairProblem.UsbPhantomsOnly, known.Problem);
        Assert.Null(unknown);
    }


    [Fact]
    public void PlanOne_StoppedFilterOutranksConfigDisabled()
    {
        // Both rules match. The stopped filter is the cause; a disabled row is
        // the symptom the user already knows about.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: false,
            configEnabled: false));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.FilterStoppedButBound, finding.Problem);
        Assert.NotEqual(RepairProblem.ConfigDisabledButLive, finding.Problem);
        Assert.Equal(RepairAction.RestartBtHidParent, finding.Action);
    }

    [Fact]
    public void PlanOne_UsbPhantomsWithNoBthenum_IsNeverAutoFixable()
    {
        // Charge-cable leftovers only. Enabling or restarting a phantom is
        // exactly the wrong move, so this must stay guided.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 0,
            usbPhantomCount: 7,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.UsbPhantomsOnly, finding.Problem);
        Assert.Equal(RepairAction.PairInWindows, finding.Action);
        Assert.False(finding.AutoFixable);
    }

    [Fact]
    public void PlanOne_ConfigDisabledWhileLive_IsAutoFixableInApp()
    {
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "030d",
            bthenumLiveCount: 1,
            boundFilterName: DriverPackageCatalog.AppleFilterServiceName,
            filterPackagePresent: true,
            filterServiceRunning: true,
            configEnabled: false,
            filterCandidates: [DriverPackageCatalog.AppleFilterServiceName]));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.ConfigDisabledButLive, finding.Problem);
        Assert.Equal(RepairAction.EnableInApp, finding.Action);
        Assert.True(finding.AutoFixable);
    }

    [Fact]
    public void PlanOne_LiveWithoutFilterPackage_IsGuidedInstall()
    {
        // Nothing filter-like is bound, so BoundFilterName is null and the
        // package probe is what decides.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            col02Present: true,
            boundFilterName: null,
            filterPackagePresent: false,
            filterServiceRunning: false));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.FilterPackageMissing, finding.Problem);
        Assert.Equal(RepairAction.InstallDriver, finding.Action);
        Assert.False(finding.AutoFixable);
    }

    [Fact]
    public void PlanOne_NothingBoundButPackagePresent_IsHealthy()
    {
        // No bound filter and no missing package: FilterStoppedButBound must not
        // fire on a null bound name, whatever the (meaningless) run state says.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            col02Present: true,
            boundFilterName: null,
            filterPackagePresent: true,
            filterServiceRunning: false));

        Assert.Null(finding);
    }

    // --- "Could not read" is not "broken" ---------------------------------
    //
    // FilterPackagePresent and FilterServiceRunning are the two inputs whose
    // producers can fail outright: DriverHealthChecker.KernelServiceRunning is
    // sc.exe query behind catch { return false; } with a 2 s WaitForExit that
    // also yields false, and DeviceSnapshotReader.ServicePackagePresent ends
    // catch { return false; }. While they are plain bool, a probe that failed
    // reaches the planner as a measured negative and rule 1 tells a healthy
    // mouse "the scroll driver stopped" while offering an elevated Bluetooth
    // stack restart. They therefore carry the contract FilterInStack already
    // documents: false is a reading, null is no evidence and never a finding.

    [Fact]
    public void PlanOne_ServiceStateUnreadable_IsSilentAndAReadStopIsStillTheRestart()
    {
        // Same healthy-looking 0323 twice. The only difference is whether the
        // service query answered at all, and that difference decides between
        // "No problems found" and an elevated restart of the mouse's stack.
        var unreadable = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            usbPhantomCount: 7,
            col01Present: true,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: null,
            filterCandidates: [BoundKmdfVariant]));
        var stopped = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            usbPhantomCount: 7,
            col01Present: true,
            col02Present: true,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: false,
            filterCandidates: [BoundKmdfVariant]));

        Assert.Null(unreadable);

        // The real negative keeps its finding, wording and auto-fix offer: the
        // USB-C charge incident this rule exists for must survive the guard.
        Assert.NotNull(stopped);
        Assert.Equal(RepairProblem.FilterStoppedButBound, stopped.Problem);
        Assert.Equal(RepairAction.RestartBtHidParent, stopped.Action);
        Assert.True(stopped.AutoFixable);
        Assert.Contains(BoundKmdfVariant, stopped.Detail);
        Assert.Contains("is not running", stopped.Detail);
    }

    [Fact]
    public void PlanOne_PackageEvidenceUnreadable_IsSilentAndAReadAbsenceIsStillTheInstall()
    {
        // The package probe is a registry read that can throw; today it says
        // "not installed" when it does, and the user is sent to install a
        // driver that may well already be there.
        var unreadable = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            col02Present: true,
            boundFilterName: null,
            filterPackagePresent: null,
            filterServiceRunning: null));
        var absent = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            col02Present: true,
            boundFilterName: null,
            filterPackagePresent: false,
            filterServiceRunning: false));

        Assert.Null(unreadable);

        Assert.NotNull(absent);
        Assert.Equal(RepairProblem.FilterPackageMissing, absent.Problem);
        Assert.Equal(RepairAction.InstallDriver, absent.Action);
        Assert.False(absent.AutoFixable);
        Assert.Equal("Scroll driver is not installed", absent.Title);
    }

    [Fact]
    public void PlanOne_PackageUnreadableWhileTheServiceReadsStopped_IsNotAFinding()
    {
        // The service really was read as stopped, but whether the driver is on
        // this PC at all could not be read. Rule 1 ("the scroll driver
        // stopped, restart the connection") and rule 6 ("it is not installed,
        // install it") contradict each other here and nothing in the snapshot
        // says which is true, so neither may speak.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: null,
            filterServiceRunning: false,
            filterCandidates: [BoundKmdfVariant]));

        Assert.Null(finding);
    }

    [Fact]
    public void PlanOne_RivalFiltersNeedAProvenRunningService()
    {
        // Rule 2's whole promise is "the working one is kept, only the
        // leftover goes". An unreadable service state cannot support that
        // sentence: the name it would keep might be the dead one.
        var unreadable = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: null,
            filterCandidates: [StaleKmdfVariant, BoundKmdfVariant]));
        var running = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [StaleKmdfVariant, BoundKmdfVariant]));

        Assert.Null(unreadable);

        Assert.NotNull(running);
        Assert.Equal(RepairProblem.ConflictingFilters, running.Problem);
        Assert.Equal(RepairAction.RemoveStaleFilter, running.Action);
    }

    [Fact]
    public void PlanOne_UnattachedFilterSurvivesAnUnreadableServiceButNotAnUnreadablePackage()
    {
        // FilterInStack == false is a MEASURED absence from the live stack,
        // and it is the only evidence this fault has. An sc.exe query that did
        // not answer may not take it away: before the tri-state, a failed query
        // read as "stopped" and the very same machine state was offered the
        // same RestartBtHidParent repair by rule 1, so dropping the finding
        // here would silence a real fault on an unrelated probe failing.
        //
        // The package read is different: rule 6 ("it is not installed, install
        // it") contradicts this rule, and an unknown package cannot say which
        // of the two is true, so that one still drops the finding.
        var serviceUnknown = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: null,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: false));
        var packageUnknown = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: null,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: false));
        // The stack evidence itself unknown, with the service unknown too:
        // nothing was measured at all, so nothing may be claimed.
        var stackUnknown = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: null,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: null));
        var measured = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: false));

        Assert.Null(packageUnknown);
        Assert.Null(stackUnknown);

        Assert.NotNull(serviceUnknown);
        Assert.Equal(RepairProblem.FilterNotInStack, serviceUnknown.Problem);
        Assert.Equal(RepairAction.RestartBtHidParent, serviceUnknown.Action);
        // The detail text may not assert the answer the query never gave.
        Assert.DoesNotContain("reports it as running", serviceUnknown.Detail);

        Assert.NotNull(measured);
        Assert.Equal(RepairProblem.FilterNotInStack, measured.Problem);
        Assert.Equal(RepairAction.RestartBtHidParent, measured.Action);
    }

    [Fact]
    public void Plan_WhenEveryDriverProbeFailed_ReportsNoProblems()
    {
        // The user-visible payoff of the tri-state: a PC where sc.exe and the
        // services registry could not be read is a PC this app knows nothing
        // about, so the repair row says so by saying nothing - instead of
        // accusing both connected devices of a dead driver.
        var mouse = Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: null,
            filterServiceRunning: null,
            filterCandidates: [BoundKmdfVariant],
            filterInStack: null);
        var keyboard = Snap(
            pid: "0267",
            bthenumLiveCount: 1,
            boundFilterName: null,
            filterPackagePresent: null,
            filterServiceRunning: null);

        var findings = RepairPlanner.Plan([mouse, keyboard]);

        Assert.Empty(findings);
        Assert.Equal("No problems found", RepairPlanner.MenuLabel(findings));
    }

    // --- A device with no wheel is never told to install a scroll driver ---
    //
    // Rule 6 fires on "live BTHENUM node + no filter package" with no
    // device-family guard, and DeviceSnapshotReader.CatalogPids sweeps
    // MouseBatteryDevice.KnownMice (which carries all three trackpads) and
    // KeyboardBatteryDevice.KnownKeyboards as well as the mice. So on any PC
    // that has no Apple mouse filter package - the normal state of a PC whose
    // owner has a Magic Keyboard and no Magic Mouse - every keyboard and every
    // trackpad gets an orange "Scroll driver is not installed" row offering to
    // install a driver for a wheel the device does not have. The same devices'
    // own driver advice already reads "Scroll: n/a"
    // (DriverAdvisor KeyboardOptions / TrackpadOptions).

    [Theory]
    [InlineData("0267")] // Magic Keyboard with Touch ID
    [InlineData("0239")] // Apple Wireless Keyboard (2011)
    [InlineData("029c")] // Magic Keyboard (2021)
    [InlineData("0320")] // Magic Keyboard (2024)
    [InlineData("030e")] // Magic Trackpad
    [InlineData("0265")] // Magic Trackpad 2
    [InlineData("0324")] // Magic Trackpad 2024
    public void PlanOne_DeviceWithNoScrollWheel_IsNeverOfferedTheScrollDriverInstall(string pid)
    {
        var finding = RepairPlanner.PlanOne(Snap(
            pid: pid,
            bthenumLiveCount: 1,
            boundFilterName: null,
            filterPackagePresent: false,
            filterServiceRunning: false));

        Assert.Null(finding);
    }

    [Theory]
    [InlineData("0323")] // Magic Mouse 2024, rides the patched KMDF filter
    [InlineData("030d")] // Magic Mouse v1, rides the Boot Camp filter
    [InlineData("0310")] // Apple Wireless Mouse (MouseBatteryDevice.cs:34-44)
    [InlineData("0269")] // Magic Mouse v2
    public void PlanOne_MouseWithoutTheScrollDriverPackage_IsStillTheInstallOffer(string pid)
    {
        // The family guard must narrow rule 6, not delete it: a connected
        // Apple mouse with no filter package really does have a dead wheel and
        // really is the device this app installs a driver for.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: pid,
            bthenumLiveCount: 1,
            boundFilterName: null,
            filterPackagePresent: false,
            filterServiceRunning: false));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.FilterPackageMissing, finding.Problem);
        Assert.Equal(RepairAction.InstallDriver, finding.Action);
        Assert.False(finding.AutoFixable);
    }

    [Fact]
    public void Plan_CatalogSweepWithNoFilterPackage_OnlyReportsTheMouse()
    {
        // The shape the user actually meets: one Magic Keyboard and one Magic
        // Mouse paired, no filter package on the PC, and the planner sweeping
        // every catalog PID. Every keyboard in KeyboardBatteryDevice's own
        // table is walked here, because that table is what CatalogPids walks.
        var snapshots = new List<DeviceSnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in KeyboardBatteryDevice.KnownKeyboards)
        {
            var pid = entry.PidPattern[^4..].ToLowerInvariant();
            if (!seen.Add(pid))
                continue;
            snapshots.Add(Snap(
                pid: pid,
                bthenumLiveCount: 1,
                boundFilterName: null,
                filterPackagePresent: false,
                filterServiceRunning: false));
        }
        snapshots.Add(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: null,
            filterPackagePresent: false,
            filterServiceRunning: false));

        var findings = RepairPlanner.Plan(snapshots);

        var only = Assert.Single(findings);
        Assert.Equal("0323", only.Pid);
        Assert.Equal(RepairProblem.FilterPackageMissing, only.Problem);
    }

    [Fact]
    public void PlanOne_KeyboardWithNoFilterPackage_StillReportsTheBlockedBatteryRead()
    {
        // The guard narrows one rule, it does not mute keyboards: the
        // SDP-blocked battery read is the keyboard fault this planner
        // diagnoses, and it has nothing to do with any scroll filter.
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0267",
            bthenumLiveCount: 1,
            boundFilterName: null,
            filterPackagePresent: false,
            filterServiceRunning: false,
            lastBatteryPct: -2));

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.BatteryReadBlocked, finding.Problem);
        Assert.Equal(RepairAction.InstallDriver, finding.Action);
    }

    [Fact]
    public void FilterServiceFor_V3IsKmdf_V1IsAppleFilter()
    {
        Assert.Equal(DriverPackageCatalog.PatchedKmdfServiceName, RepairPlanner.FilterServiceFor("0323"));
        Assert.Equal(DriverPackageCatalog.AppleFilterServiceName, RepairPlanner.FilterServiceFor("030d"));
        Assert.Equal(DriverPackageCatalog.AppleFilterServiceName, RepairPlanner.FilterServiceFor("030D"));
    }

    [Fact]
    public void FilterServiceFor_Snapshot_PrefersTheBoundServiceOverThePidDefault()
    {
        // Repairs must act on the service the device really binds. The per-PID
        // catalog name is only the fallback for a device with nothing bound.
        var bound = Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: false);

        Assert.Equal(BoundKmdfVariant, RepairPlanner.FilterServiceFor(bound));
        Assert.NotEqual(DriverPackageCatalog.PatchedKmdfServiceName, RepairPlanner.FilterServiceFor(bound));
    }

    [Fact]
    public void FilterServiceFor_Snapshot_FallsBackToPidDefaultWhenNothingIsBound()
    {
        var v3 = Snap(pid: "0323", bthenumLiveCount: 2, boundFilterName: null);
        var v1 = Snap(pid: "030d", bthenumLiveCount: 1, boundFilterName: null);

        Assert.Equal(DriverPackageCatalog.PatchedKmdfServiceName, RepairPlanner.FilterServiceFor(v3));
        Assert.Equal(DriverPackageCatalog.AppleFilterServiceName, RepairPlanner.FilterServiceFor(v1));
    }

    [Fact]
    public void IsKmdfFamily_AcceptsSuffixedVariantsAndTheBareConstant()
    {
        Assert.True(RepairPlanner.IsKmdfFamily(BoundKmdfVariant));
        Assert.True(RepairPlanner.IsKmdfFamily(DriverPackageCatalog.PatchedKmdfServiceName));
        Assert.True(RepairPlanner.IsKmdfFamily(
            DriverPackageCatalog.PatchedKmdfServiceName.ToUpperInvariant()));
    }

    [Fact]
    public void IsKmdfFamily_RejectsUnrelatedServicesAndNull()
    {
        Assert.False(RepairPlanner.IsKmdfFamily("HidBth"));
        Assert.False(RepairPlanner.IsKmdfFamily(DriverPackageCatalog.AppleFilterServiceName));
        Assert.False(RepairPlanner.IsKmdfFamily(null));
        Assert.False(RepairPlanner.IsKmdfFamily(""));
    }

    [Fact]
    public void IsAppleFamily_AcceptsTheBootCampFilterAndRejectsTheKmdfFamily()
    {
        Assert.True(RepairPlanner.IsAppleFamily(DriverPackageCatalog.AppleFilterServiceName));
        Assert.True(RepairPlanner.IsAppleFamily(
            DriverPackageCatalog.AppleFilterServiceName.ToUpperInvariant()));
        Assert.False(RepairPlanner.IsAppleFamily(BoundKmdfVariant));
        Assert.False(RepairPlanner.IsAppleFamily("HidBth"));
        Assert.False(RepairPlanner.IsAppleFamily(null));
    }

    [Fact]
    public void Plan_KeepsInputOrder_AndDropsHealthySnapshots()
    {
        var healthy = Snap(
            pid: "0269",
            bthenumLiveCount: 1,
            boundFilterName: DriverPackageCatalog.AppleFilterServiceName,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [DriverPackageCatalog.AppleFilterServiceName]);
        var v1Gone = Snap(pid: "030d", configEnabled: false);
        var v3Stopped = Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: false,
            filterCandidates: [BoundKmdfVariant]);

        var findings = RepairPlanner.Plan([healthy, v1Gone, v3Stopped]);

        Assert.Equal(2, findings.Count);
        Assert.Equal("030d", findings[0].Pid);
        Assert.Equal(RepairProblem.NoInstances, findings[0].Problem);
        Assert.Equal("0323", findings[1].Pid);
        Assert.Equal(RepairProblem.FilterStoppedButBound, findings[1].Problem);
    }

    [Fact]
    public void Plan_AllHealthy_IsEmpty()
    {
        var healthy = Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant]);

        Assert.Empty(RepairPlanner.Plan([healthy]));
        Assert.Empty(RepairPlanner.Plan([]));
    }

    [Fact]
    public void MenuLabel_NoFindings_SaysNoProblemsFound()
    {
        Assert.Equal("No problems found", RepairPlanner.MenuLabel([]));
    }

    [Fact]
    public void MenuLabel_SingleFinding_IsThatFindingsTitle()
    {
        var finding = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: false));

        Assert.NotNull(finding);
        Assert.Equal(finding.Title, RepairPlanner.MenuLabel([finding]));
    }

    [Fact]
    public void MenuLabel_SeveralFindings_IsSummaryNotTheEmptyLabel()
    {
        var v3 = RepairPlanner.PlanOne(Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: false));
        var v1 = RepairPlanner.PlanOne(Snap(pid: "030d", configEnabled: false));

        Assert.NotNull(v3);
        Assert.NotNull(v1);

        var label = RepairPlanner.MenuLabel([v3, v1]);
        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.NotEqual(RepairPlanner.MenuLabel([]), label);
    }
}
