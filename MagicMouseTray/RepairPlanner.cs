// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// What is wrong with one device, in priority order. First match wins in PlanOne.
internal enum RepairProblem
{
    None,
    FilterStoppedButBound,
    NoInstances,
    UsbPhantomsOnly,
    ConfigDisabledButLive,
    FilterPackageMissing,
    ConflictingFilters,
    FilterNotInStack,
    PointerChildMissing,
    BatteryReadBlocked,
    // The mouse answered its 0x90 battery request and the percent byte in the
    // answer was 0 while its multitouch stream was provably advancing. Ranked
    // and worded apart from BatteryReadBlocked because the cause is this PC's
    // scroll filter truncating the answer, not a missing pairing-record cap.
    BatteryResponseTruncated,
    // Touch activity accumulated across several sessions with not one wheel or
    // hwheel event reaching Windows, on a validated decoder.
    ScrollNotchesNotDelivered,
}

// What the user (or the app) has to do about it.
internal enum RepairAction
{
    None,
    RestartBtHidParent,
    PairInWindows,
    EnableInApp,
    InstallDriver,
    RemoveStaleFilter,
    // Guidance only. The Apple multi-touch enable FEATURE report {F1,02,01} is
    // owned by the driver package's own scripts (mm-f1-once.ps1, and the
    // MmAutoF1Watcher scheduled task around it); docs/ENABLE-DISABLE.md:93-97
    // names a second implementation in Magic Tray an explicit NON-GOAL. So this
    // action never sends F1, never elevates and never runs a script: it tells
    // the user which component switches multitouch back on.
    RecommendMultitouchWatcher,
    // Guidance only, and the only action that names a script. The mitigation
    // (scripts/repair-magicmouse-channel.ps1) re-opens the mouse's control
    // channel from this PC; the tray neither elevates nor runs it, because a
    // temporary workaround for a driver defect is the user's call to make.
    RunChannelRepairScript,
    // Guidance only, and deliberately not fixable at all: the defect is in the
    // scroll driver's gesture code and no setting on this PC changes it.
    RecommendFilterUpdate,
}

// One device as observed on the live machine. Built by DeviceSnapshotReader;
// this record itself is plain data so the planner can be tested off-Windows.
internal sealed record DeviceSnapshot(
    string Pid,
    int BthenumLiveCount,
    int UsbPhantomCount,
    bool Col01Present,
    bool Col02Present,
    string? BoundFilterName,
    bool FilterPackagePresent,
    bool FilterServiceRunning,
    bool? ConfigEnabled,
    // Every filter-family service named anywhere on this device's live stack,
    // in the reader's precedence order and with the registry's own casing.
    // Two or more entries mean Windows is told to load two rival builds of the
    // same vendor filter; BoundFilterName is the one that is actually running.
    string[] FilterCandidates,
    // DEVPKEY_Device_Stack evidence for this device: is the bound filter
    // actually in the live kernel stack Windows built for THIS mouse?
    //   true  - the bound filter name is in the stack, it really is attached.
    //   false - the stack was readable and the filter is NOT in it.
    //   null  - no evidence (nothing readable). Never a finding, ever.
    // This field exists because FilterServiceRunning cannot answer the
    // question: it is the word "RUNNING" in sc query output
    // (DriverHealthChecker.cs:324-345), which proves only that the driver
    // IMAGE is loaded on this PC, never that the filter attached to this
    // mouse's stack. After a Windows restart PnP can rebuild the BTHENUM
    // stack without the filter while LowerFilters still names it and the
    // SCM still reports it RUNNING.
    bool? FilterInStack,
    // Is there a live pointer device for this mouse at all? Evidence:
    // CM_Locate_DevNodeW(CM_LOCATE_DEVNODE_NORMAL) on the Bluetooth-transport
    // COL01 HID child devnode of this PID (DeviceDiagReader.PointerChildLive).
    //   true  - the COL01 child resolves as present, the pointer exists.
    //   false - a Bluetooth COL01 child key exists and does NOT resolve, so
    //           Windows has no working pointer for a mouse that is connected.
    //   null  - no such key at all, or the lookup failed. Never a finding.
    // USB / HID\VID_05AC charge-cable phantom COL01 keys are excluded by the
    // reader: a phantom COL01 exists on the reference PC, which is exactly why
    // the older substring flags (Col01Present/Col02Present) cannot answer this.
    bool? PointerChildLive,
    // Is the multitouch stream the scroll filter needs PROVABLY flowing right
    // now? Evidence: movement of the filter's own counter Diag\AclTranslateCount
    // (Rid12Count as fallback) under
    // HKLM\SYSTEM\CurrentControlSet\Services\<BoundFilterName>\Diag, sampled
    // between two reads by DeviceDiagReader.MultitouchAdvancing.
    //   true  - the counter advanced between samples, so ACL frames are being
    //           translated: scrolling really is working at this moment.
    //   null  - no evidence. First sample, counter unchanged, missing
    //           key/value, nothing bound, or any failure.
    //   false - never occurs, on purpose.
    // Two signals were measured and REJECTED for this field, and neither may
    // come back:
    //   Diag\LastAclReceived. Sampled 4x at 2.5 s on the reference PC while
    //   scrolling WORKED: 23, 23, 9, 23 (AclTranslateCount advancing 142271 ->
    //   142827 throughout). The "compact" 9 only means the last ACL frame was
    //   short, so reading 9 as "multitouch off" would raise a false finding
    //   whenever a sample landed on one. It is also last-seen, untimestamped
    //   and registry-persisted across reboot, which is why the driver repo's
    //   own watcher refuses to gate on it (mm-auto-f1-watcher.ps1:140-146).
    //   A usermode GET_REPORT(Input, 0x90) pull on COL02, which returned
    //   [90 00 00] 10/10 with a dead wheel AND with multitouch confirmed
    //   flowing.
    // A counter that is NOT advancing is equally worthless as a fault signal:
    // an idle mouse nobody is touching and a mouse that stopped sending touch
    // data look identical. Hence null, and hence no fault may ever be read out
    // of its absence. Its PRESENCE, though, is load-bearing for the two rules
    // added below: "the mouse is being used right now" is exactly what tells a
    // truncated battery answer apart from an idle mouse that sent none, and it
    // is what makes a zero wheel count an observation rather than a shrug.
    bool? MultitouchAdvancing,
    // Does the driver package's multitouch watcher look alive on this PC?
    // Evidence: C:\ProgramData\MagicMouseDriver\auto-f1-watcher.log plus the
    // presence of the installed mm-auto-f1-watcher.ps1 beside it
    // (DeviceDiagReader.WatcherState). Machine-wide, not per device.
    //   true  - installed and its 5-minute heartbeat is recent.
    //   false - installed but the newest heartbeat is stale, so it is not
    //           running (or the scheduled task never started).
    //   null  - not installed, or nothing readable. Never a finding on its
    //           own, and never a finding at all: it only sharpens the wording
    //           of the tray's user-initiated "Scroll is not working" help.
    bool? ScrollWatcherHealthy,
    // The battery reading the tray's poller last got for this PID, with the
    // existing MouseBatteryDevice / KeyboardBatteryDevice sentinels:
    //   0..100 - a real percentage.
    //   -1     - no reading (nothing arrived, or the HID open failed).
    //   -2     - present but blocked, e.g. the keyboard's pairing record is
    //            missing the SDP Feature 0x47 cap Windows needs.
    //   -3     - AdaptivePoller collapsed three consecutive -1s.
    //   null   - not measured (no poll has run for this PID). Never a finding.
    int? LastBatteryPct,
    // ONE correlated battery observation: the 0x90 read's own verdict, the Diag
    // samples taken around that same call, and when the touch stream last
    // provably advanced. null - nothing bound, no KMDF-family filter (the Apple
    // filter publishes no Diag key at all), or no probe taken this sweep - is no
    // evidence and can raise nothing.
    //
    // Why one record instead of loose fields: the halves of this evidence are
    // only meaningful TOGETHER. The battery number the tray normally holds is a
    // cache (TrayApp.cs:639, written :1241-1244) that AdaptivePoller may not
    // refresh for hours, so pairing it with a touch delta measured at some
    // other moment would accuse an idle mouse from an hours-old reading.
    // Keeping the set in one record makes that mis-pairing unrepresentable
    // rather than merely discouraged, and the probe travels with the HID
    // collection that won AdaptivePoller's ReadingRank collapse (:169-192) so
    // it can never be assembled from one collection's failure and another's
    // percentage.
    //
    // DiagBefore/DiagAfter are a PAIR, not a precomputed bool, for two
    // reasons. A driver reinstall zeroes the whole Diag block and a device
    // mid-restart collapses it too (measured: Rid12Count 2095323 -> 521 across
    // one restart), so a counter that is not strictly increasing has to read as
    // "no evidence", which is only decidable from before AND after. And the two
    // samples carry the INTERVAL the delta was measured across, which is what
    // makes the delta a claim about this read instead of about the poll cycle:
    // they are taken either side of the GET_REPORT itself
    // (MouseBatteryDevice.ReadV3Rid90). IFilterDiagReader.TouchStreamAdvanced
    // is the single implementation of both rules.
    //
    // Everything in those two samples except Availability, ServiceKeyName and
    // the counters is OPPORTUNISTIC, and one rule below depends on knowing why.
    // LastAclReceived / LastAclCapacity / LastAclBytes / LastOutHdr are a
    // single "last inbound" slot shared by the HID control channel and the
    // interrupt channel, and the mouse streams touch reports at roughly 65 per
    // second - so a sample taken any meaningful time after a control-channel
    // exchange shows the interrupt channel's capacity 9, never the control
    // channel's capacity 1. Its presence CONFIRMS; its absence says nothing
    // whatsoever, and no rule may gate on it. The same shared slot is why
    // LastAclReceived was rejected as a health signal outright
    // (DeviceDiagReader.cs:309-323): the broken binary and the fixed binary both
    // read LastAclReceived 23 with LastAclCapacity 9, so it separates nothing.
    BatteryProbe? BatteryProbe = null,
    // The PASSIVE wheel observation, and only that. It feeds the device row's
    // observational scroll line and nothing else: no rule may derive a scroll
    // fault from it, because one window cannot. Measured on the FIXED binary:
    // 84 active touch seconds of which 83 were zero-wheel, all 32 events inside
    // a single 1.34 s burst - so a zero here is the normal state of a healthy
    // mouse nobody happened to scroll on. The fault verdict needs the two
    // PROMPTED legs compared against each other (RepairPlanner.JudgeScrollProbe),
    // which the tray holds locally for the length of the probe flow and never
    // stores here. null is "never observed"; Void is "nobody touched the
    // surface", which is not a negative result either.
    WheelObservation? Wheel = null);

internal sealed record RepairFinding(
    string Pid,
    RepairProblem Problem,
    RepairAction Action,
    string Title,
    string Detail,
    bool AutoFixable);

// Pure decision layer. No registry, no processes, no logging, no Windows types:
// everything it needs is already in the DeviceSnapshot handed to it.
//
// Three real field incidents drive the priority order:
//   0323 after a USB-C charge: pointer + battery fine, wheel dead, because the
//   filter service actually named in LowerFilters is not running.
//   030D after tray-disable + "Remove device": no PnP nodes at all, so nothing
//   can be enabled or restarted; the user has to pair it again first.
//   0323 after a Windows restart: the filter is still registered and its
//   service still reports RUNNING, but PnP rebuilt the stack without it, so
//   the wheel is dead while every service-level check looks healthy.
//
// BoundFilterName is the service name as found on the live stack (for example
// "MagicMouseDriver204Scroll"), not a catalog constant, so this file compares
// service names with the family predicates below and never by equality.
//
// Ownership rule: the catalog covers every Apple device this app knows about,
// not the two or three the user actually has. A PID with no live BTHENUM
// instance and no explicit enabled_<pid> config entry (ConfigEnabled null) has
// never been seen on this PC, so it is not a problem and produces no finding.
// An explicit entry is proof the device was here once - only then is "Windows
// has no record of it" news.
//
// Capability coverage: of the three things a user notices (pointer, scroll,
// battery) pointer loss, the keyboard's blocked battery read and - since the
// filter's own diagnostic block exists - a battery answer the filter truncated
// have a signal that can tell "broken" from "idle", so those get automatic
// rules. A dead scroll wheel still has none, and not for want of trying: every
// passive candidate was measured false-positive-prone (see
// DeviceSnapshot.MultitouchAdvancing, and note that a hand resting on the
// surface emits touch reports at roughly 65 per second while legitimately
// producing no notches at all, so zero-wheel time on a HEALTHY mouse is
// unbounded). Scroll is therefore decided by a PROMPTED probe with known user
// intent - PlanScrollProbe below, offered from the tray - and never by a
// passive threshold.
//
// All user-facing text is plain ASCII on purpose (this repo has been bitten by
// mojibake): hyphens only, no em dashes, no smart quotes.
internal static class RepairPlanner
{
    internal static RepairFinding? PlanOne(DeviceSnapshot snapshot)
    {
        // 1. Filter driver is still named on the live stack but its service is dead.
        if (snapshot.BthenumLiveCount > 0
            && snapshot.BoundFilterName is not null
            && snapshot.FilterPackagePresent
            && !snapshot.FilterServiceRunning)
        {
            return new RepairFinding(
                snapshot.Pid,
                RepairProblem.FilterStoppedButBound,
                RepairAction.RestartBtHidParent,
                "Scroll wheel is dead - the scroll driver stopped",
                "Pointer movement, clicking and the battery reading all still work, so the mouse "
                + "itself is fine. The scroll driver service ("
                + FilterServiceFor(snapshot)
                + ") is not running, which usually happens after the mouse has been charged over a "
                + "USB-C cable. The mouse is still paired with this PC, so nothing has to be "
                + "removed and nothing has to be paired again. A scroll driver of this kind is "
                + "loaded by Windows while it builds the mouse's Bluetooth connection, so it "
                + "cannot be started by hand. The fix restarts the Bluetooth mouse connection on "
                + "this PC, so Windows loads the scroll driver again. It takes a few seconds and "
                + "the pointer may freeze briefly while it happens.",
                AutoFixable: true);
        }

        // 2. Two rival builds of the same vendor filter are registered on one
        // stack. Measured on the reference 0323: the BTHENUM device key still
        // carries "MagicMouseDriver" (stopped, left over from an older install)
        // while the instance key carries the running "MagicMouseDriver204Scroll".
        // Windows applies both LowerFilters values to the same stack, which is
        // what killed the wheel and zeroed the COL02 battery report. Ranked
        // below rule 1 because a stack whose chosen filter is dead needs the
        // restart first; this rule only fires while the chosen filter runs.
        if (snapshot.BthenumLiveCount > 0
            && snapshot.FilterCandidates.Length > 1
            && snapshot.FilterServiceRunning)
        {
            var stale = StaleFilters(snapshot);
            // Nothing to strip (every candidate is the bound one) is not a
            // conflict, and a finding naming no leftover would be unactionable.
            if (stale.Length > 0)
            {
                var working = FilterServiceFor(snapshot);
                var leftovers = string.Join(", ", stale);
                bool one = stale.Length == 1;
                var detail =
                    "Windows is set up to load more than one scroll driver for this single mouse. "
                    + "The working one (" + working + ") is running, and "
                    + (one ? "a leftover entry (" : "leftover entries (")
                    + leftovers + ") from an older install of the same driver "
                    + (one ? "is" : "are")
                    + " still registered on the same Bluetooth mouse. Two scroll drivers on one "
                    + "mouse get in each other's way: this can kill the scroll wheel and make the "
                    + "battery level read as unavailable. The fix removes ONLY the leftover "
                    + (one ? "entry (" : "entries (") + leftovers
                    + ") and then restarts the mouse connection, so Windows builds it again with "
                    + "a single scroll driver. The working driver (" + working + ") is kept, "
                    + "nothing is uninstalled, and the mouse is never unpaired. It needs "
                    + "administrator approval and takes a few seconds, and the pointer may freeze "
                    + "briefly while it happens.";
                return new RepairFinding(
                    snapshot.Pid,
                    RepairProblem.ConflictingFilters,
                    RepairAction.RemoveStaleFilter,
                    "Two scroll drivers are fighting over this mouse",
                    detail,
                    AutoFixable: true);
            }
        }

        // 2b. The reboot shape. The filter is registered on this stack, its
        // service reports RUNNING, and the filter is still not in the live
        // device stack. Field report after a Windows restart: the tray said
        // the mouse was on the right driver, the wheel was dead anyway, and
        // the repair menu said "No problems found" because every other input
        // was byte-identical to a healthy mouse.
        //
        // FilterServiceRunning cannot see this. It is the word "RUNNING" in
        // sc query output (DriverHealthChecker.cs:324-345): proof that the
        // driver image is loaded on this PC, never proof that the filter
        // attached to THIS mouse. The discriminator is DEVPKEY_Device_Stack -
        // the property this repo's own capture script already trusts
        // (scripts/capture-state.ps1:146-155), and the only reading that can
        // tell registration apart from attachment. FilterInStack
        // carries it into the planner. docs/TEST-PLAN.md:22 had already named
        // this exact shape - wheel dead while the BOUND filter service
        // reports RUNNING - as a failure with no diagnosis attached.
        //
        // Ranked below rule 2 because when two rival filter names are
        // registered, stripping the leftover is the better-evidenced repair
        // and it restarts the stack anyway, so it already covers this fix.
        // Numbered 2b, not 3, to keep the later rule numbers (and the docs
        // and tests that cite them) stable.
        //
        // FilterInStack null means the property could not be read: no
        // evidence, no finding. Nagging every PC whose stack read fails is
        // worse than missing the diagnosis on it.
        if (snapshot.BthenumLiveCount > 0
            && snapshot.BoundFilterName is not null
            && snapshot.FilterPackagePresent
            && snapshot.FilterServiceRunning
            && snapshot.FilterInStack == false)
        {
            return new RepairFinding(
                snapshot.Pid,
                RepairProblem.FilterNotInStack,
                RepairAction.RestartBtHidParent,
                "Scroll wheel is dead - the driver is not attached to this mouse",
                "Pointer movement, clicking and the battery reading all still work, but the "
                + "scroll wheel does nothing. The scroll driver ("
                + FilterServiceFor(snapshot)
                + ") is installed and Windows reports it as running, and yet Windows built this "
                + "mouse's Bluetooth connection without it - which is what a restart of Windows "
                + "can leave behind. The mouse is still paired with this PC: nothing is "
                + "unpaired, and nothing is installed or removed. The fix restarts this mouse's "
                + "Bluetooth connection, so Windows builds the connection again with the scroll "
                + "driver in place. It needs administrator approval, takes a few seconds, and "
                + "the pointer may pause while it happens. If the rebuilt connection still "
                + "comes up without the scroll driver, pairing the mouse again is the next "
                + "step, and this menu will say so the next time it checks.",
                AutoFixable: true);
        }

        // 2c. The mouse is connected over Bluetooth and yet Windows has no
        // working pointer device for it: the cursor does not move at all. That
        // is the most severe loss of the three capabilities this planner can
        // see (pointer, scroll, battery), so it is ranked above everything
        // that only costs the wheel or the battery reading.
        //
        // The discriminator is a devnode presence lookup on the Bluetooth
        // COL01 HID child (DeviceDiagReader.PointerChildLive), NOT the old
        // Col01Present substring flag: on the reference PC a charge-cable
        // phantom HID\VID_05AC&PID_0323&MI_01&COL01 key exists, so the
        // substring flag reads true even when the Bluetooth pointer child is
        // gone. Phantom keys are excluded there and must never be acted on.
        //
        // null is "no COL01 child key at all, or the lookup failed": no
        // evidence, no finding. Only an existing-but-not-present child is a
        // fault, and the repair for it is the same stack rebuild rules 1 and
        // 2b use, which is why it is auto-fixable.
        if (snapshot.BthenumLiveCount > 0 && snapshot.PointerChildLive == false)
        {
            return new RepairFinding(
                snapshot.Pid,
                RepairProblem.PointerChildMissing,
                RepairAction.RestartBtHidParent,
                "Pointer is dead - Windows has no mouse device for it",
                "The mouse is connected to this PC, but Windows has no working pointer device "
                + "for it, so the cursor does not move. The mouse itself is still paired: "
                + "nothing is unpaired, and nothing is installed or removed. The fix rebuilds "
                + "this mouse's Bluetooth connection so Windows creates the pointer device "
                + "again. It needs administrator approval and takes a few seconds.",
                AutoFixable: true);
        }

        // 2d. Battery. Only ONE battery shape has a specific, actionable cause
        // measured behind it: a keyboard reporting -2 ("present but blocked"),
        // which is what KeyboardBatteryDevice returns when the pairing record
        // is missing the SDP Feature 0x47 cap Windows needs before it will
        // read the battery level. The existing guided SDP patch is exactly the
        // fix, so this reuses the InstallDriver offer path.
        //
        // Deliberately NOT fired for a mouse -2 or for any -3: on a mouse the
        // vendor battery report simply stops arriving whenever the multitouch
        // stream stops, and -3 is only AdaptivePoller collapsing three missed
        // reads - which an idle mouse produces all day. A rule that fires on
        // every idle mouse is precisely the nag this planner exists to avoid,
        // and that symptom has no automatic diagnosis anyway (see
        // MultitouchAdvancing: counter movement proves scroll works, its
        // absence proves nothing). -1 and null are likewise no evidence.
        if (snapshot.BthenumLiveCount > 0
            && snapshot.LastBatteryPct == -2
            && DriverHealthChecker.IsKeyboardPid(snapshot.Pid))
        {
            return new RepairFinding(
                snapshot.Pid,
                RepairProblem.BatteryReadBlocked,
                RepairAction.InstallDriver,
                "Battery level cannot be read for this keyboard",
                "The keyboard is connected and typing works, but Windows is not allowed to read "
                + "its battery level: the pairing record this PC holds for it is missing the "
                + "entry Windows needs before it will ask for the battery percentage. This app "
                + "can walk you through patching that pairing record. The keyboard stays paired "
                + "- nothing is unpaired and nothing about typing changes.",
                AutoFixable: false);
        }

        // 2e. The one mouse battery shape with a measured cause behind it, and
        // the exception 2d's suppression above cannot cover. 2d is right that a
        // mouse -2 is normally silence: the vendor report stops whenever the
        // multitouch stream stops, so an idle mouse produces it all day. What
        // separates this case from that one is that the answer DID arrive and
        // was destroyed on the way up, and there are now two facts that say so
        // together:
        //
        //   BatteryProbe.ZeroReport - a well-formed 0x90 report whose percent
        //   byte is 0 (MouseBatteryDevice.IsBogusZeroReport:253-254). HIDCLASS
        //   pre-zeroes the caller's buffer and writes the report id into byte 0,
        //   so a truncated copy-back reads as a report of "0%" - which no Magic
        //   Mouse sends (MouseBatteryDevice.cs:244-248).
        //
        //   A touch-report counter that CLIMBED across the pair taken either
        //   side of that same read (MouseBatteryDevice.ReadV3Rid90), which is
        //   both the proof that the report was produced at all and the proof
        //   that it was produced NOW rather than at some point in the last day.
        //
        // Why this may fire where 2d must not: 2d's suppressed population is
        // "no touch, therefore no vendor report, therefore -2". This rule
        // requires the opposite - the mouse is being used, so the report was
        // sent - and it requires the report to have actually come back with a
        // zero in it. An idle mouse satisfies neither: TouchStreamAdvanced is
        // false with a still counter, and there is no answer to judge. That is
        // the whole reason the touch-stream precondition is here and not a
        // nicety.
        //
        // Why NOT LastBatteryPct == -2, which is what 2d reads: that sentinel
        // covers three different outcomes on the v3 path alone - the zero
        // report (MouseBatteryDevice.cs:183-195), a wrong report id
        // (:201-203), and an IOCTL that failed three times (:214-216) - and it
        // is additionally emitted by KeyboardBatteryDevice for the SDP case 2d
        // routes and by MouseBatteryDevice's v1/v2 path at :353. The failed
        // IOCTL is what a mouse mid-re-enumeration returns; this finding's
        // remediation restarts that device, so firing on the sentinel would
        // build a restart loop inside exactly the window FindingGate.cs:11-18
        // exists to damp. Only the zero-report fact may fire, and -2 keeps its
        // existing meaning untouched for 2d.
        if (snapshot.BthenumLiveCount > 0
            && DriverHealthChecker.IsV3Pid(snapshot.Pid)
            && BatteryAnswerTruncated(snapshot.BatteryProbe))
        {
            return new RepairFinding(
                snapshot.Pid,
                RepairProblem.BatteryResponseTruncated,
                RepairAction.RunChannelRepairScript,
                "Battery level is being cut off before Windows sees it",
                TruncatedBatteryDetail(snapshot.BatteryProbe!),
                AutoFixable: false);
        }

        // Ownership gate: nothing live, nothing in the config for it, so this
        // PC has never had this catalog device. Reporting it as missing turned
        // a 2-device PC into 22 "problems". Placed after rules 1, 2, 2b, 2c
        // and 2d, which all need live instances and are therefore untouched
        // by it.
        if (snapshot.BthenumLiveCount == 0 && snapshot.ConfigEnabled is null)
            return null;

        // 3. Windows has no record of the device at all - nothing to enable or restart.
        if (snapshot.BthenumLiveCount == 0 && snapshot.UsbPhantomCount == 0)
        {
            var detail =
                "Windows no longer has any record of this mouse, so there is nothing on this PC to "
                + "switch back on and no driver to restart. Turn the mouse over and slide the power "
                + "switch off and then on again, until the light blinks. Then open Windows Settings, "
                + "go to Bluetooth and devices, choose Add device, pick Bluetooth, and select the "
                + "mouse when it appears in the list.";
            if (snapshot.ConfigEnabled == false)
            {
                detail += " Once it is paired again, come back to this menu and turn "
                    + "\"Show in Magic Tray\" back on for this mouse.";
            }

            return new RepairFinding(
                snapshot.Pid,
                RepairProblem.NoInstances,
                RepairAction.PairInWindows,
                "Mouse is not paired with this PC any more",
                detail,
                AutoFixable: false);
        }

        // 4. Only charge-cable leftovers are present. They are not a working connection.
        if (snapshot.BthenumLiveCount == 0 && snapshot.UsbPhantomCount > 0)
        {
            return new RepairFinding(
                snapshot.Pid,
                RepairProblem.UsbPhantomsOnly,
                RepairAction.PairInWindows,
                "Mouse is not connected over Bluetooth",
                "The only entries Windows still has for this mouse are leftovers from when it was "
                + "plugged in with a charging cable. They are not a working connection, they do "
                + "nothing on their own, and they must be left alone - this app will never enable, "
                + "restart or remove them. Over Bluetooth the mouse is not connected right now. "
                + "Turn the mouse over and slide the power switch off and then on again, until the "
                + "light blinks. Then open Windows Settings, go to Bluetooth and devices, choose "
                + "Add device, pick Bluetooth, and select the mouse when it appears.",
                AutoFixable: false);
        }

        // 5. Connected, but the user (or an earlier disable) switched it off in the app.
        if (snapshot.ConfigEnabled == false && snapshot.BthenumLiveCount > 0)
        {
            return new RepairFinding(
                snapshot.Pid,
                RepairProblem.ConfigDisabledButLive,
                RepairAction.EnableInApp,
                "Mouse is switched off in this app",
                "The mouse is connected to this PC, but it is switched off in Magic Tray, so the "
                + "tray hides it and stops reading its battery level. The fix simply turns "
                + "\"Show in Magic Tray\" back on for this mouse. Nothing is installed, nothing is "
                + "removed, and the mouse stays paired.",
                AutoFixable: true);
        }

        // 6. Connected, but the scroll driver package was never installed (or was removed).
        if (snapshot.BthenumLiveCount > 0 && !snapshot.FilterPackagePresent)
        {
            return new RepairFinding(
                snapshot.Pid,
                RepairProblem.FilterPackageMissing,
                RepairAction.InstallDriver,
                "Scroll driver is not installed",
                "The mouse is connected, but the driver that makes the scroll wheel work is not "
                + "installed on this PC. On its own Windows only gives you pointer movement and "
                + "clicks for this mouse. Use the driver option in this menu to install the scroll "
                + "driver, then run this check again. Installing it needs administrator approval "
                + "and does not remove or re-pair the mouse.",
                AutoFixable: false);
        }

        return null;
    }

    internal static IReadOnlyList<RepairFinding> Plan(IReadOnlyList<DeviceSnapshot> snapshots)
    {
        var findings = new List<RepairFinding>();
        for (int i = 0; i < snapshots.Count; i++)
        {
            var finding = PlanOne(snapshots[i]);
            if (finding is not null)
                findings.Add(finding);
        }
        return findings;
    }

    internal static string MenuLabel(IReadOnlyList<RepairFinding> findings) => findings.Count switch
    {
        0 => "No problems found",
        1 => findings[0].Title,
        _ => $"{findings.Count} problems found",
    };

    // The report id the mouse answers the battery request on, as it appears on
    // the wire inside LastAclBytes (A1 90 04 16 on the reference PC: the ACL
    // HID-input header, the report id, a flags byte, then the percentage).
    const byte WireBatteryReportId = 0x90;
    const byte WireAclInputHeader = 0xA1;

    // Both halves of the truncation evidence, and nothing else: a well-formed
    // zero report, and a touch-report counter that climbed across the pair the
    // probe took around that very read. Every absent input reads as no
    // evidence - a null probe, a report that is not the well-formed zero, a
    // missing pre-read sample, an unreadable Diag key, a counter that stood
    // still or went BACKWARDS (a driver reinstall zeroes the whole Diag block,
    // and a device mid-restart collapses it too, measured 2095323 -> 521 across
    // one restart), or a pair too wide to be describing this read.
    //
    // ONE gate, deliberately. There used to be a second one here: the probe's
    // TakenAt against a remembered "last advance" timestamp, bounded by ten
    // seconds. It could reject nothing, because that timestamp was stamped with
    // the sample clock and the difference was therefore zero by construction.
    // The recency question it meant to ask is now answered where the
    // measurement is made - PairMaxSpan bounds the interval inside
    // TouchStreamAdvanced - so a second timing rule on top could only reject
    // pairs that one has already rejected.
    internal static bool BatteryAnswerTruncated(BatteryProbe? probe) =>
        probe is not null
        && probe.ZeroReport == true
        && IFilterDiagReader.TouchStreamAdvanced(probe.DiagBefore, probe.DiagAfter);

    // Corroboration is FilterDiagSnapshot.TruncationCorroborated
    // (DeviceDiagReader.cs:91-95) - one truth about that shape, owned by the
    // record that carries the bytes. Opportunistic, never a gate: the filter's
    // "last inbound" slot is shared with the 65-per-second interrupt channel,
    // so the shape is visible only when the slot was read immediately after the
    // probe, and its absence is evidence of nothing.

    // The percentage the mouse actually put on the wire, when the corroborating
    // sample happens to carry the frame. Only claimed for a frame that really is
    // a 0x90 input report and only for a value Apple can report (1..100), so a
    // truncated or unrelated frame produces null rather than a made-up number.
    internal static int? WireBatteryPercent(byte[]? lastAclBytes) =>
        lastAclBytes is { Length: >= 4 }
        && lastAclBytes[0] == WireAclInputHeader
        && lastAclBytes[1] == WireBatteryReportId
        && lastAclBytes[3] is >= 1 and <= 100
            ? lastAclBytes[3]
            : null;

    static string TruncatedBatteryDetail(BatteryProbe probe)
    {
        var detail =
            "The mouse is in use right now - Windows is receiving its touch reports - and it "
            + "answered the request for its battery level, but the percentage in the answer was "
            + "zero, and no Magic Mouse reports zero. The scroll driver on this PC hands Windows "
            + "only the first byte of that answer and Windows fills the rest with zeros, so the "
            + "real percentage is thrown away after the mouse has already sent it. ";

        if (probe.DiagAfter?.TruncationCorroborated == true)
        {
            var received = probe.DiagAfter!.LastAclReceived!.Value;
            var pct = WireBatteryPercent(probe.DiagAfter.LastAclBytes);
            detail += "The scroll driver's own counters caught it in the act: the answer it "
                + "copied up was 1 byte long while " + received.ToString()
                + " bytes had come back from the mouse"
                + (pct is null
                    ? ". "
                    : ", and the percentage on the wire was " + pct.Value.ToString() + "%. ");
        }

        return detail
            + "Pointer movement, clicking and scrolling are not affected by this - only the "
            + "battery reading is. Until a corrected scroll driver is installed, running "
            + ChannelRepairScript + " from this project re-opens the mouse's control channel on "
            + "this PC, which is the state in which the whole answer gets through. It installs "
            + "nothing, removes nothing and leaves the mouse paired, and it needs administrator "
            + "approval. This app does not run it for you: it is a temporary workaround for a "
            + "driver fault, so it stays your decision.";
    }

    // The mitigation a sibling ships in this repo. Named in one place so the
    // finding and its test cannot drift apart from the file on disk.
    internal const string ChannelRepairScript = "scripts/repair-magicmouse-channel.ps1";

    // ---- Scroll: the prompted probe -------------------------------------
    //
    // There is no passive scroll rule and there must not be one. A hand resting
    // on the surface emits TOUCH_STATE_DRAG reports at roughly 65 per second and
    // legitimately produces no notches, because a notch exists only per unit of
    // anchor travel - so "touch reports advancing and zero wheel events" is
    // TRUE for most of the time any healthy mouse spends being held. Measured on
    // the fixed binary: 84 active seconds of touch carried its first wheel event
    // at t=57 s and all 32 events fell inside a single 1.34 s burst. Healthy
    // zero-wheel time is bounded only by whether the user feels like scrolling,
    // so no accumulated-silence threshold can ever assert a fault.
    //
    // What removes the ambiguity is asking. The tray prompts two gestures and
    // judges the pair:
    //
    //   symmetric leg  - both fingers sliding together. POSITIVE CONTROL only.
    //     The broken rule still emits notches here (7 per 60 units), so nonzero
    //     proves two-finger contact was registered and the wheel path can carry
    //     a byte. It is never read as health.
    //   asymmetric leg - one finger held completely still, the other sliding.
    //     THE DISCRIMINATOR. The filter measures scroll from the lowest-id
    //     contact in drag state, so a still reference finger contributes zero
    //     travel and the whole gesture yields zero notches at every scroll step
    //     the driver accepts (1..224). A correct rule yields notches from the
    //     finger that moved.
    //
    // Both legs zero is INCONCLUSIVE, not a fault: notch emission needs two
    // contacts registered, and the tray cannot see a contact count, so "the
    // resting finger never pressed down" is indistinguishable from dead scroll
    // from the asymmetric leg alone.
    internal enum ScrollProbeVerdict
    {
        // Nothing may be claimed in either direction. The user is told exactly
        // that, and offered the probe again.
        Inconclusive,
        // The fault: the control leg carried notches and the discriminator leg
        // carried none.
        NotchesMissing,
        // The discriminator leg carried notches. That is NOT a clean bill of
        // health - the broken rule emits notches for a symmetric drag too - it
        // only means this probe found nothing to report.
        NotchesDelivered,
    }

    // The least touch a leg may carry and still be the gesture the verdicts
    // rest on. WheelObservation.Void is cleared by a single 250 ms tick of
    // activity, so without a floor a quarter-second brush of the surface
    // produced a full "Scroll wheel is dead" finding - whose own detail then
    // rendered that touch as "0 seconds" through Seconds(ActiveDuration)
    // (:770). The prompt asks for a deliberate ten-second slide
    // (ScrollProbeLeg, :798); five seconds is half of it, which is generous to
    // a user who started late and still nothing like a brush.
    internal static readonly TimeSpan MinMeasuredTouch = TimeSpan.FromSeconds(5);

    // A leg only counts when the sink actually measured the right device with a
    // decoder it has proven: no observation, no target device, nobody touching
    // the surface, an unvalidated decoder, or too little touch to have been the
    // gesture at all are all "no measurement", and a zero from any of them is
    // not a zero.
    static bool LegMeasured(WheelObservation? leg) =>
        leg is not null
        && !leg.Void
        && leg.TargetDevicePath is not null
        && leg.DecoderValidated
        && leg.ActiveDuration >= MinMeasuredTouch;

    static bool LegSilent(WheelObservation leg) =>
        leg.WheelEvents == 0 && leg.HWheelEvents == 0;

    internal static ScrollProbeVerdict JudgeScrollProbe(
        WheelObservation? symmetric, WheelObservation? asymmetric)
    {
        if (!LegMeasured(symmetric) || !LegMeasured(asymmetric))
            return ScrollProbeVerdict.Inconclusive;
        if (!LegSilent(asymmetric!))
            return ScrollProbeVerdict.NotchesDelivered;
        // Asymmetric silence with no positive control behind it cannot be told
        // apart from a resting finger that never registered.
        return LegSilent(symmetric!)
            ? ScrollProbeVerdict.Inconclusive
            : ScrollProbeVerdict.NotchesMissing;
    }

    // The finding for a probe the user was asked to perform. Guidance only, and
    // deliberately not fixable: the reference-finger rule yields zero notches at
    // every scroll step the driver accepts, so there is no setting on this PC -
    // in the tray or in the driver - that changes the outcome. Only a corrected
    // driver build does.
    internal static RepairFinding? PlanScrollProbe(
        DeviceSnapshot snapshot, WheelObservation? symmetric, WheelObservation? asymmetric)
    {
        if (JudgeScrollProbe(symmetric, asymmetric) != ScrollProbeVerdict.NotchesMissing)
            return null;

        var probe = asymmetric!;
        var control = symmetric!;
        var notches = control.WheelEvents + control.HWheelEvents;
        var detail =
            "You were asked to hold one finger still and slide the other one, and Windows "
            + "received " + probe.MouseRecords.ToString()
            + " input records from this mouse during those " + Seconds(probe.ActiveDuration)
            + " seconds of touch - and not one scroll notch. The same test with both fingers "
            + "moving together produced " + notches.ToString()
            + ", so the mouse, the Bluetooth connection and this PC's scroll path all work: "
            + "the mouse was reporting, and scroll notches can get through. "
            + "The scroll driver measures scrolling from one reference finger only, so a finger "
            + "resting on the surface stops every other finger's movement from counting - which "
            + "is why a hand parked on the mouse kills the wheel. Nothing on this PC changes "
            + "that: the driver's scroll-step setting makes no difference at any value it "
            + "accepts. The fix is a corrected scroll driver, and this app will not pretend it "
            + "can do it from here. Magic Tray has changed nothing on this PC or on the mouse "
            + "while running this test.";

        return new RepairFinding(
            snapshot.Pid,
            RepairProblem.ScrollNotchesNotDelivered,
            RepairAction.RecommendFilterUpdate,
            "Scroll wheel is dead - the driver drops the scroll it measures",
            detail,
            AutoFixable: false);
    }

    static string Seconds(TimeSpan span) =>
        ((int)Math.Round(span.TotalSeconds)).ToString();

    // How long each prompted leg is measured for. Ten seconds is ample: the
    // fixed binary emitted 32 wheel events inside 1.34 s, so a working mouse
    // cannot stay silent through a deliberate ten-second gesture.
    internal static readonly TimeSpan ScrollProbeLeg = TimeSpan.FromSeconds(10);

    // What the user is asked to do, in order. The control gesture runs FIRST so
    // an abandoned flow leaves the positive control rather than the ambiguous
    // half.
    //
    // decoderValidated is the sink's sticky per-device-path flag. It can only be
    // set by decoding a nonzero NON-wheel button flag - a real click - and in a
    // prompted scroll window the user has no reason to click, so when it is
    // still false the first prompt asks for one click up front. That validates
    // the decoder inside the measured window by construction instead of leaving
    // every probe inconclusive. Button state is read from the report header and
    // takes no part in the notch path, so clicking cannot influence the result.
    //
    // Both prompts describe MECHANICS, never intent, and that is load-bearing
    // rather than pedantry: a habit-dependent gesture cannot be a control.
    // "Scroll normally with two fingers" was rejected outright, because most
    // people's natural two-finger scroll is one dominant finger sliding with
    // the other trailing or resting - which IS the asymmetric case and emits
    // zero notches under the broken rule. That wording would therefore have
    // read zero on BOTH legs for exactly the users whose scroll is dead, and
    // handed them "could not confirm two-finger contact". Friendlier prose here
    // reopens that hole, so the mechanics stay.
    //
    // The property leg 1 relies on is only this: a deliberate symmetric drag is
    // NONZERO under both rules, with room to spare. It is not a calibration -
    // the counts differ by rule (7 notches per 60 touch units under the broken
    // rule, 14 under the healthy one, because a correct rule emits per finger
    // while the broken one emits from the reference contact alone) - so a count
    // near 14 is the HEALTHY signature and must never be read as anomalous.
    internal static string ScrollProbeControlPrompt(bool decoderValidated) =>
        (decoderValidated ? "" : "Click once, then ")
        + "place two fingers side by side on the top surface and slide them together - the same "
        + "distance at the same speed - up and down about 3 cm, repeatedly, for ten seconds. "
        + "This first step only checks that this mouse and this PC can carry scrolling at all.";

    // The single permitted retry, offered only when BOTH legs came back silent
    // and only for leg 1. It REPLACES the first control observation; it is
    // never added to it, because accumulating windows is the passive design
    // that cannot distinguish a healthy mouse nobody scrolled on. There is no
    // retry for leg 2 for the same reason. It restates the mechanics that
    // decide the outcome rather than repeating the prompt verbatim.
    internal const string ScrollProbeControlRetryPrompt =
        "One more try, and the way the fingers move is what matters: put them side by side, "
        + "keep them both pressed on the top surface, and move them the same distance at the "
        + "same time - up and down about 3 cm, repeatedly, for ten seconds. If this step stays "
        + "silent, this test stops without reporting anything.";

    internal const string ScrollProbeDiscriminatorPrompt =
        "Now rest one finger still on the top surface, pressed down firmly enough that the "
        + "mouse still feels it, and slide the other finger up and down about 3 cm, "
        + "repeatedly, for ten seconds. Keep the still finger down the whole time.";

    // Said when the pair could not decide. Same voice as the gate-pending
    // branch in the tray: name what was not established and offer the retry,
    // never a silent pass and never a fault. Both legs silent is the case this
    // exists for: with only one contact registered the driver emits zero
    // notches whether it is broken or healthy (GestureEngine.c:152-156 refreshes
    // the anchors and skips the notch path entirely below two contacts), so a
    // still finger the mouse never felt is indistinguishable from dead scroll.
    internal const string ScrollProbeInconclusive =
        "This test could not confirm two-finger contact, so nothing is being reported. Either "
        + "the mouse sent no touch reports during the ten seconds, or the resting finger was "
        + "not pressed down firmly enough for the mouse to register it - and both of those look "
        + "exactly like a working mouse from here, so this app will not call the mouse faulty on "
        + "them. Run it again and keep both fingers in contact with the top surface throughout.";

    // Said when the discriminator leg carried notches. Deliberately not "scroll
    // is working": the broken rule emits notches for a symmetric drag too, so a
    // nonzero result rules out this one fault and nothing else.
    internal const string ScrollProbeNoFaultFound =
        "Scroll notches reached Windows during this test, so this particular fault is not what "
        + "is happening here. That is not a clean bill of health for scrolling in general - it "
        + "only means this test found nothing to report.";

    // Fallback only: what this PID is EXPECTED to ride when nothing is bound.
    // 0323 rides the patched KMDF filter; every other Apple mouse rides the Boot Camp filter.
    internal static string FilterServiceFor(string pid) =>
        DriverHealthChecker.IsV3Pid(pid)
            ? DriverPackageCatalog.PatchedKmdfServiceName
            : DriverPackageCatalog.AppleFilterServiceName;

    // What a repair must act on: the service actually bound on this machine,
    // falling back to the expected one only when nothing filter-like is bound.
    internal static string FilterServiceFor(DeviceSnapshot snapshot) =>
        snapshot.BoundFilterName ?? FilterServiceFor(snapshot.Pid);

    // The filter names a repair may strip: every candidate on the stack except
    // the one that is actually bound and running. Verbatim casing and stack
    // order are preserved because the repair writes these names straight back
    // into a LowerFilters value. Nothing bound means nothing may be removed -
    // the last family filter must always survive.
    internal static string[] StaleFilters(DeviceSnapshot snapshot)
    {
        var bound = snapshot.BoundFilterName;
        if (bound is null)
            return [];

        var candidates = snapshot.FilterCandidates;
        List<string>? stale = null;
        for (int i = 0; i < candidates.Length; i++)
        {
            if (string.Equals(candidates[i], bound, StringComparison.OrdinalIgnoreCase))
                continue;
            (stale ??= new List<string>(candidates.Length - 1)).Add(candidates[i]);
        }

        return stale is null ? [] : [.. stale];
    }

    // The KMDF package ships several build variants under one name stem
    // (MagicMouseDriver, MagicMouseDriver204Scroll, ...). Family membership is a
    // prefix test on the catalog constant: string-only, no registry, no process.
    internal static bool IsKmdfFamily(string? service) =>
        !string.IsNullOrEmpty(service)
        && service.StartsWith(
            DriverPackageCatalog.PatchedKmdfServiceName, StringComparison.OrdinalIgnoreCase);

    internal static bool IsAppleFamily(string? service) =>
        !string.IsNullOrEmpty(service)
        && service.StartsWith(
            DriverPackageCatalog.AppleFilterServiceName, StringComparison.OrdinalIgnoreCase);
}
