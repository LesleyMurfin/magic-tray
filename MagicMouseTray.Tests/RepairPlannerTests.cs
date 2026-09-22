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
        bool filterPackagePresent = false,
        bool filterServiceRunning = false,
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
        int? lastBatteryPct = null,
        // batteryProbe: the ONE correlated 0x90 observation. null - no probe
        // this sweep - is the default, so no truncation rule can fire in a test
        // that is not about it.
        BatteryProbe? batteryProbe = null,
        // wheel: the PASSIVE wheel observation. Feeds the device row's
        // observational scroll line only; no rule reads it.
        WheelObservation? wheel = null)
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
            lastBatteryPct,
            BatteryProbe: batteryProbe,
            Wheel: wheel);

    // ---- Fixtures for the two filter defects ----------------------------
    //
    // Both defects are measured facts about the shipped KMDF filter, and both
    // are invisible to every registration-level check: the battery answer comes
    // back STATUS_SUCCESS with a zeroed percent, and the wheel simply never
    // emits. So these fixtures are the records those seams hand over - the
    // battery probe's Diag pair, taken either side of one HID read, and a
    // raw-input wheel sink driven through IWheelSink - built exactly the way
    // the battery read and DeviceSnapshotReader build them.

    private const string DiagService = BoundKmdfVariant;
    private static readonly DateTimeOffset T0 =
        new(2026, 9, 16, 23, 37, 42, TimeSpan.Zero);

    // The truncating control-channel frame as it was measured on the wire:
    // A1 (ACL HID input header), 90 (the battery report id), 04, 16 = 22 %.
    private static readonly byte[] TruncatedWireFrame = [0xA1, 0x90, 0x04, 0x16];

    private static FilterDiagSnapshot Diag(
        ulong? rid12,
        DiagAvailability availability = DiagAvailability.Ok,
        uint? lastAclReceived = null,
        uint? lastAclCapacity = null,
        uint? lastOutHdr = null,
        byte[]? lastAclBytes = null,
        DateTimeOffset? takenAt = null)
        => new(
            takenAt ?? T0,
            availability,
            DiagService,
            rid12, rid12, null, null,
            lastAclReceived, lastAclCapacity, lastOutHdr, null,
            lastAclBytes, null, null, null);

    // The planner judges the probe RECORD, so the fixture is the pair as the
    // battery read hands it over: one Diag sample taken immediately before the
    // GET_REPORT, one immediately after, and the fact that read produced. Both
    // samples default to T0, i.e. one read's worth of interval apart, because
    // that is the only interval a delta may be measured across
    // (DeviceDiagReader.PairMaxSpan).
    private static BatteryProbe Probe(
        FilterDiagSnapshot? before,
        FilterDiagSnapshot after,
        bool? zeroReport,
        DateTimeOffset? takenAt = null)
        => new(takenAt ?? T0, zeroReport, before, after);

    // The whole measured fingerprint: a well-formed 0x90 report whose percent
    // byte is 0 and a touch counter that really climbed across the read.
    // corroborate:false is the COMMON live case - the filter's last-inbound
    // slot is shared with the ~65/s interrupt channel, so a sample normally
    // shows capacity 9 and not capacity 1.
    private static BatteryProbe TruncationProbe(bool corroborate = true)
        => Probe(
            Diag(rid12: 2_095_000),
            corroborate
                ? Diag(
                    rid12: 2_095_323,
                    lastAclReceived: 78,
                    lastAclCapacity: 1,
                    lastOutHdr: 0x41,
                    lastAclBytes: TruncatedWireFrame)
                : Diag(rid12: 2_095_323, lastAclReceived: 23, lastAclCapacity: 9),
            zeroReport: true);

    private static DeviceSnapshot V3WithProbe(BatteryProbe? probe, int? lastBatteryPct = -2)
        => Snap(
            pid: "0323",
            bthenumLiveCount: 2,
            boundFilterName: BoundKmdfVariant,
            filterPackagePresent: true,
            filterServiceRunning: true,
            filterCandidates: [BoundKmdfVariant],
            lastBatteryPct: lastBatteryPct,
            batteryProbe: probe);

    // The naive rule this batch replaced: "-2 and the touch stream looks
    // alive". Asserted directly in the loop-hazard test so that test cannot be
    // satisfied by a planner that simply stopped reading the battery.
    private static bool NaiveTruncationPredicate(DeviceSnapshot s) =>
        s.LastBatteryPct == -2
        && IFilterDiagReader.TouchStreamAdvanced(
            s.BatteryProbe?.DiagBefore, s.BatteryProbe?.DiagAfter);

    private static WheelObservation Leg(
        int wheelEvents = 0,
        int hWheelEvents = 0,
        int mouseRecords = 694,
        bool decoderValidated = true,
        bool @void = false,
        string? targetDevicePath = TargetPath,
        int activeSeconds = 10)
        => new(
            WallDuration: TimeSpan.FromSeconds(10),
            ActiveDuration: TimeSpan.FromSeconds(activeSeconds),
            MouseRecords: mouseRecords,
            WheelEvents: wheelEvents,
            HWheelEvents: hWheelEvents,
            ButtonEvents: decoderValidated ? 2 : 0,
            AbsMotionSum: 12_485,
            TargetDevicePath: targetDevicePath,
            DecoderValidated: decoderValidated,
            Void: @void);

    private const string TargetPath =
        @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0323&Col01";

    // A wheel sink with two scripted legs, driven through IWheelSink.
    private sealed class FakeWheelSink(params WheelObservation[] legs) : IWheelSink
    {
        private int _next;

        public Task<WheelObservation> ObservePromptedAsync(TimeSpan window, CancellationToken ct) =>
            Task.FromResult(legs[Math.Min(_next++, legs.Length - 1)]);
    }

    private static async Task<(WheelObservation Control, WheelObservation Probe)> RunLegsAsync(
        IWheelSink sink)
    {
        var control = await sink.ObservePromptedAsync(
            RepairPlanner.ScrollProbeLeg, CancellationToken.None);
        var probe = await sink.ObservePromptedAsync(
            RepairPlanner.ScrollProbeLeg, CancellationToken.None);
        return (control, probe);
    }

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

    // ================= Battery: the truncated answer =====================

    [Fact]
    public void PlanOne_ZeroPercentWhileTouchStreamAdvancing_NamesTheMitigationScript()
    {
        // The measured defect. The filter gates its inbound scratch diversion
        // on BufferSize > 0, HidBth reads the control channel header-first with
        // BufferSize == 1, so the filter takes the whole GET_REPORT response
        // onto a 78-byte scratch and copies ONE byte back. HIDCLASS pre-zeroes
        // the caller's buffer and writes the report id into byte 0, so the read
        // succeeds and userspace sees 90 00 00 while the percentage (22 %) was
        // on the wire.
        var finding = RepairPlanner.PlanOne(V3WithProbe(TruncationProbe()));

        Assert.NotNull(finding);
        Assert.Equal("0323", finding.Pid);
        Assert.Equal(RepairProblem.BatteryResponseTruncated, finding.Problem);
        Assert.Equal(RepairAction.RunChannelRepairScript, finding.Action);
        Assert.False(finding.AutoFixable);

        // What a user has to be able to act on: that the mouse answered, that
        // the answer was cut off, and the name of the one thing that helps.
        Assert.Contains("scripts/repair-magicmouse-channel.ps1", finding.Detail);
        Assert.Contains("1 byte long while 78 bytes", finding.Detail);
        Assert.Contains("22%", finding.Detail);
        // It must not imply the pointer or the wheel are broken too.
        Assert.Contains("only the battery reading is", finding.Detail);
    }

    [Fact]
    public void PlanOne_ZeroPercentWithNoCorroboratingSlotSample_StillFires()
    {
        // The COMMON live case, and the reason corroboration may never gate:
        // the filter's last-inbound slot is shared with the interrupt channel
        // at ~65 reports/s, so a sweep-time sample shows capacity 9. Both the
        // broken and the fixed binary read received 23 / capacity 9 there, so
        // that shape separates nothing - while the zero percent plus a live
        // touch stream still does.
        var uncorroborated = RepairPlanner.PlanOne(
            V3WithProbe(TruncationProbe(corroborate: false)));

        Assert.NotNull(uncorroborated);
        Assert.Equal(RepairProblem.BatteryResponseTruncated, uncorroborated.Problem);
        Assert.Contains("scripts/repair-magicmouse-channel.ps1", uncorroborated.Detail);
        // Nothing may be asserted about bytes that were not sampled.
        Assert.DoesNotContain("bytes had come back", uncorroborated.Detail);
    }

    [Fact]
    public void PlanOne_ZeroPercentOnAnIdleMouse_RaisesNothing()
    {
        // The whole point of the touch-stream precondition. An idle mouse stops
        // sending its vendor report, so a zero answer from one is silence, not
        // a fault - and a rule that fired here would nag every Magic Mouse on
        // every desk. Counter identical between the two sweeps = the stream did
        // not advance.
        var stalled = Probe(
            Diag(rid12: 2_095_323),
            Diag(rid12: 2_095_323),
            zeroReport: true);

        Assert.Null(RepairPlanner.PlanOne(V3WithProbe(stalled)));
    }

    [Fact]
    public void PlanOne_ZeroPercentWithCountersResetBetweenSweeps_RaisesNothing()
    {
        // A driver reinstall zeroes every Diag counter, and a device mid-restart
        // collapses them too (measured: Rid12Count 2095323 -> 521 across one
        // restart). A lower number is a NEW BASELINE, never a negative delta
        // and never proof of touch - so this must read as no evidence.
        var reinstalled = Probe(
            Diag(rid12: 2_095_323),
            Diag(rid12: 0),
            zeroReport: true);
        var midRestart = Probe(
            Diag(rid12: 2_095_323),
            Diag(rid12: 521),
            zeroReport: true);

        Assert.Null(RepairPlanner.PlanOne(V3WithProbe(reinstalled)));
        Assert.Null(RepairPlanner.PlanOne(V3WithProbe(midRestart)));
    }

    [Fact]
    public void PlanOne_BatteryReadThatNeverAnswered_RaisesNothingAndCannotLoop()
    {
        // THE LOOP HAZARD. MOUSE_RID90_FAILED (the IOCTL failing three times,
        // observed as err=21 at 23:37:42 while the device was re-enumerating)
        // and MOUSE_RID90_BAD (a wrong report id) both return the SAME -2
        // sentinel as the truncation case. This finding's remediation restarts
        // the device, so a rule keyed on -2 would diagnose a mouse that is
        // already mid-restart and prescribe another restart - a loop inside the
        // exact window FindingGate exists to damp. Only the well-formed-zero
        // fact may fire, so both of these are null and raise nothing.
        //
        // This is the CONSUMER half only, and the fact below is hand-written -
        // so it cannot see the producer reclassifying an outcome. That half is
        // pinned by MouseBatteryDeviceTests
        // .ZeroReportFact_PinsEveryTerminalOutcomeOfA0x90Read.
        var neverAnswered = V3WithProbe(Probe(
            Diag(rid12: 2_095_000),
            Diag(rid12: 2_095_323),
            zeroReport: null));

        Assert.Null(RepairPlanner.PlanOne(neverAnswered));

        // And the fixture really is the trap: it satisfies the naive predicate
        // this rule replaced, so a planner that went back to reading -2 - or
        // that widened ZeroReport to a two-state bool - fails here instead of
        // passing by having stopped looking at the battery altogether.
        Assert.True(NaiveTruncationPredicate(neverAnswered));

        // Same shape with a real percentage: not a fault either.
        Assert.Null(RepairPlanner.PlanOne(V3WithProbe(Probe(
            Diag(rid12: 2_095_000),
            Diag(rid12: 2_095_323),
            zeroReport: false))));
    }

    [Fact]
    public void PlanOne_ZeroPercentWithACounterThatClimbedAcrossPollCycles_RaisesNothing()
    {
        // The false positive the interval bound exists to stop. The pair used to
        // come from a static per-service store, so before was the PREVIOUS poll
        // cycle's sample - 5 minutes to 24 hours back - and any touch anywhere
        // in that window read as "the mouse is in use right now" on a mouse
        // nobody had touched for hours.
        var pollCycleApart = Probe(
            Diag(rid12: 169_000, takenAt: T0 - TimeSpan.FromMinutes(5)),
            Diag(rid12: 400_000),
            zeroReport: true);

        Assert.Null(RepairPlanner.PlanOne(V3WithProbe(pollCycleApart)));

        // The identical climb measured across the read itself still fires: the
        // discriminator is the interval, not the numbers.
        var acrossTheRead = Probe(
            Diag(rid12: 169_000, takenAt: T0 - TimeSpan.FromMilliseconds(120)),
            Diag(rid12: 400_000),
            zeroReport: true);

        Assert.Equal(
            RepairProblem.BatteryResponseTruncated,
            RepairPlanner.PlanOne(V3WithProbe(acrossTheRead))!.Problem);
    }

    // ================= Scroll: the prompted two-leg probe ================

    [Fact]
    public async Task PlanScrollProbe_ControlCarriesNotchesAndDiscriminatorDoesNot_Fires()
    {
        // The measured defect: the gesture engine emits notches only for the
        // lowest-id contact in drag state, so a resting finger holding a low
        // slot id kills scroll while every other finger's travel is discarded.
        // Control leg nonzero proves two-finger contact was registered and that
        // notches can reach Windows at all; the discriminator's zero is then
        // the defect and not a lifted finger.
        var (control, probe) = await RunLegsAsync(new FakeWheelSink(
            Leg(wheelEvents: 12, hWheelEvents: 2, mouseRecords: 5_714),
            Leg(wheelEvents: 0, hWheelEvents: 0, mouseRecords: 694)));

        Assert.Equal(
            RepairPlanner.ScrollProbeVerdict.NotchesMissing,
            RepairPlanner.JudgeScrollProbe(control, probe));

        var finding = RepairPlanner.PlanScrollProbe(V3WithProbe(probe: null), control, probe);

        Assert.NotNull(finding);
        Assert.Equal(RepairProblem.ScrollNotchesNotDelivered, finding.Problem);
        Assert.Equal(RepairAction.RecommendFilterUpdate, finding.Action);
        Assert.False(finding.AutoFixable);

        // Reports in versus notches out, from THIS run - never a hardcoded
        // number - plus the cause and the fact that nothing here can fix it.
        Assert.Contains("694 input records", finding.Detail);
        Assert.Contains("not one scroll notch", finding.Detail);
        Assert.Contains("14", finding.Detail);
        Assert.Contains("resting on the surface", finding.Detail);
        Assert.Contains("scroll-step setting makes no difference", finding.Detail);
    }

    [Fact]
    public async Task PlanScrollProbe_BothLegsSilent_IsInconclusiveNotAFault()
    {
        // THE test that earns the two-leg design. Notch emission needs two
        // contacts registered (GestureEngine.c:152-156 re-anchors and skips the
        // notch path below that), so a resting finger the pad never felt looks
        // exactly like dead scroll. A single-leg rule FIRES here; this one must
        // not, because the control leg proved nothing.
        var (control, probe) = await RunLegsAsync(new FakeWheelSink(Leg(), Leg()));

        Assert.Equal(
            RepairPlanner.ScrollProbeVerdict.Inconclusive,
            RepairPlanner.JudgeScrollProbe(control, probe));
        Assert.Null(RepairPlanner.PlanScrollProbe(V3WithProbe(probe: null), control, probe));
    }

    [Fact]
    public async Task PlanScrollProbe_DiscriminatorCarriesNotches_IsNoFindingAndNoHealthClaim()
    {
        // Nonzero never asserts health: the broken rule still emits notches for
        // a symmetric drag (7 per 60 touch units, 14 on a correct build), so all
        // this rules out is this one fault.
        var (control, probe) = await RunLegsAsync(new FakeWheelSink(
            Leg(wheelEvents: 14), Leg(wheelEvents: 7)));

        Assert.Equal(
            RepairPlanner.ScrollProbeVerdict.NotchesDelivered,
            RepairPlanner.JudgeScrollProbe(control, probe));
        Assert.Null(RepairPlanner.PlanScrollProbe(V3WithProbe(probe: null), control, probe));
        Assert.DoesNotContain("working", RepairPlanner.ScrollProbeNoFaultFound);
        Assert.Contains("not a clean bill of health", RepairPlanner.ScrollProbeNoFaultFound);
    }

    [Fact]
    public async Task PlanScrollProbe_NoTouchInALeg_IsInconclusive()
    {
        // A user who ignored the prompt must never be scored as broken, and a
        // sink that never saw the 0323 has measured nothing about it - a second
        // mouse may not fill in for this one's zero.
        var (voidControl, probe) = await RunLegsAsync(new FakeWheelSink(
            Leg(wheelEvents: 14, @void: true), Leg()));
        var (control, voidProbe) = await RunLegsAsync(new FakeWheelSink(
            Leg(wheelEvents: 14), Leg(@void: true)));
        var (control2, noTarget) = await RunLegsAsync(new FakeWheelSink(
            Leg(wheelEvents: 14), Leg(targetDevicePath: null)));

        Assert.Equal(
            RepairPlanner.ScrollProbeVerdict.Inconclusive,
            RepairPlanner.JudgeScrollProbe(voidControl, probe));
        Assert.Equal(
            RepairPlanner.ScrollProbeVerdict.Inconclusive,
            RepairPlanner.JudgeScrollProbe(control, voidProbe));
        Assert.Equal(
            RepairPlanner.ScrollProbeVerdict.Inconclusive,
            RepairPlanner.JudgeScrollProbe(control2, noTarget));
        Assert.Null(RepairPlanner.PlanScrollProbe(V3WithProbe(probe: null), control, voidProbe));
    }

    [Fact]
    public async Task PlanScrollProbe_ATouchTooShortToBeTheGesture_IsInconclusive()
    {
        // WheelObservation.Void is cleared by a single 250 ms tick of activity,
        // so without a floor a brush of the surface scored as a measured leg and
        // produced a full "Scroll wheel is dead" finding - whose own detail then
        // rendered that touch as "0 seconds". The verdict rests on the
        // deliberate ten-second slide the prompt asks for.
        var (control, brushed) = await RunLegsAsync(new FakeWheelSink(
            Leg(wheelEvents: 14), Leg(activeSeconds: 1)));

        Assert.Equal(
            RepairPlanner.ScrollProbeVerdict.Inconclusive,
            RepairPlanner.JudgeScrollProbe(control, brushed));
        Assert.Null(RepairPlanner.PlanScrollProbe(V3WithProbe(probe: null), control, brushed));
    }

    [Fact]
    public async Task PlanScrollProbe_DecoderNeverValidated_IsInconclusive()
    {
        // A zero read by a decoder that has never been proven is not a zero.
        // Validation is sticky per device path and can only be set by decoding a
        // real non-wheel button flag, which is why the first prompt asks for a
        // click when it is still unset.
        var (control, probe) = await RunLegsAsync(new FakeWheelSink(
            Leg(wheelEvents: 14, decoderValidated: false),
            Leg(decoderValidated: false)));

        Assert.Equal(
            RepairPlanner.ScrollProbeVerdict.Inconclusive,
            RepairPlanner.JudgeScrollProbe(control, probe));
        Assert.Null(RepairPlanner.PlanScrollProbe(V3WithProbe(probe: null), control, probe));
    }

    [Fact]
    public void ScrollProbePrompts_AskForMechanicsAndOnlyDemandAClickWhenUnproven()
    {
        // The wording is load-bearing. "Scroll normally" was rejected: most
        // people's natural two-finger scroll is one dominant finger with the
        // other trailing, which IS the asymmetric case and reads zero under the
        // broken rule - so the control would fail on exactly the population it
        // controls for. The discriminator must demand a still finger that is
        // still in contact.
        var unproven = RepairPlanner.ScrollProbeControlPrompt(decoderValidated: false);
        var proven = RepairPlanner.ScrollProbeControlPrompt(decoderValidated: true);

        Assert.StartsWith("Click once, then ", unproven);
        Assert.DoesNotContain("Click once", proven);
        foreach (var control in new[] { unproven, proven })
        {
            Assert.Contains("side by side", control);
            Assert.Contains("same distance at the same speed", control);
            Assert.DoesNotContain("normally", control);
        }

        Assert.Contains("still", RepairPlanner.ScrollProbeDiscriminatorPrompt);
        Assert.Contains("pressed down", RepairPlanner.ScrollProbeDiscriminatorPrompt);
        Assert.Contains("the other finger", RepairPlanner.ScrollProbeDiscriminatorPrompt);

        // The retry says what to CHANGE, and says the flow ends after it.
        Assert.NotEqual(RepairPlanner.ScrollProbeControlRetryPrompt, proven);
        Assert.Contains("side by side", RepairPlanner.ScrollProbeControlRetryPrompt);
        Assert.Contains("stops without reporting", RepairPlanner.ScrollProbeControlRetryPrompt);

        // Inconclusive names what was not established, and never passes.
        Assert.Contains("could not confirm two-finger contact", RepairPlanner.ScrollProbeInconclusive);
        Assert.Contains("Run it again", RepairPlanner.ScrollProbeInconclusive);
    }

    // ================= The rows the findings have to reach ===============

    [Fact]
    public void ScrollRow_MeasuredSilence_ReadsAsUnverifiedAndNotAsWorking()
    {
        // Reachability, not plumbing: the row is what the user sees, and before
        // this change it claimed "Scroll: working" from counter movement alone -
        // a statement about ACL frames being translated, not about a notch ever
        // reaching Windows, which is exactly the defect. A measured window on
        // this device that saw no notch must outrank that claim.
        var facts = new DeviceCapability.CapabilityFacts(
            Kind: DeviceKind.MagicMouseV3,
            Pid: "0323",
            LastPct: 47,
            BoundFilter: BoundKmdfVariant,
            FilterPackagePresent: true,
            FilterServiceRunning: true,
            FilterInStack: true,
            PointerChildLive: true,
            MultitouchAdvancing: true,
            Problem: null,
            EnabledInApp: true);

        // No observation: unchanged behaviour, the old line stands.
        Assert.Equal("Scroll: working", DeviceCapability.ScrollRow(facts));

        var observed = DeviceCapability.ScrollRow(facts with { Wheel = Leg() });
        Assert.Equal("Scroll: no scrolling seen yet (unverified)", observed);
        Assert.True(observed.Length <= DeviceCapability.RowMax);
        Assert.Contains(observed, DeviceCapability.Rows(facts with { Wheel = Leg() }));

        // Notches were seen: the positive line is allowed again.
        Assert.Equal(
            "Scroll: working",
            DeviceCapability.ScrollRow(facts with { Wheel = Leg(wheelEvents: 14) }));

        // An unmeasured window is not evidence and must not displace anything.
        Assert.Equal(
            "Scroll: working",
            DeviceCapability.ScrollRow(facts with { Wheel = Leg(@void: true) }));
        Assert.Equal(
            "Scroll: working",
            DeviceCapability.ScrollRow(facts with { Wheel = Leg(decoderValidated: false) }));
    }

    [Fact]
    public void Rows_MeasuredFaults_RenderTheirOwnLines()
    {
        // Both new findings have to be visible on the device row, not only in
        // the headline. A finding whose row still read "working" would be the
        // same out-claiming this change exists to remove.
        var facts = new DeviceCapability.CapabilityFacts(
            Kind: DeviceKind.MagicMouseV3,
            Pid: "0323",
            LastPct: -2,
            BoundFilter: BoundKmdfVariant,
            FilterPackagePresent: true,
            FilterServiceRunning: true,
            FilterInStack: true,
            PointerChildLive: true,
            MultitouchAdvancing: true,
            Problem: RepairProblem.BatteryResponseTruncated,
            EnabledInApp: true);

        var batteryRow = DeviceCapability.BatteryRow(facts);
        Assert.Equal("Battery: answer cut off by the scroll driver", batteryRow);
        Assert.True(batteryRow.Length <= DeviceCapability.RowMax);
        Assert.Contains(batteryRow, DeviceCapability.Rows(facts));

        var scrollFacts = facts with
        {
            Problem = RepairProblem.ScrollNotchesNotDelivered,
            LastPct = 47,
        };
        var scrollRow = DeviceCapability.ScrollRow(scrollFacts);
        Assert.Equal("Scroll: not working (driver drops scroll it reads)", scrollRow);
        Assert.Contains(scrollRow, DeviceCapability.Rows(scrollFacts));
    }
}
