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
    // data look identical. Hence null, and hence no automatic scroll finding
    // anywhere in PlanOne - scroll help is user-initiated in the tray instead.
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
    int? LastBatteryPct);

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
// battery) only pointer loss and the keyboard's blocked battery read have a
// signal that can tell "broken" from "idle", so only those two get automatic
// rules. A dead scroll wheel with a bound, running, attached filter has no
// such signal - every candidate was measured false-positive-prone (see
// DeviceSnapshot.MultitouchAdvancing) - so the tray offers scroll help on the
// user's own assertion instead of accusing a healthy mouse.
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
        // sc query output (DriverHealthChecker.cs:320-347): proof that the
        // driver image is loaded on this PC, never proof that the filter
        // attached to THIS mouse. The discriminator is DEVPKEY_Device_Stack -
        // the property this repo's own capture script reads on the live
        // BTHENUM instance, noting there that LowerFilters and sc query only
        // prove registration (scripts/capture-state.ps1:212-221, with the name
        // match against the returned string list in Test-StackHasFilter at
        // scripts/capture-state.ps1:146-155), and the one the recovery script
        // names the discriminator for this exact post-reboot case
        // (scripts/diagnose-and-recover.ps1:266-268). FilterInStack carries it
        // into the planner. The A5/A6 pass rule in
        // docs/TEST-PLAN.md had already named this exact shape - wheel dead
        // while the BOUND filter service reports RUNNING - as a failure with
        // no diagnosis attached.
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

    // The 0-findings arm says what PlanOne actually looked at, and no more.
    // The rules above read driver state (is the bound filter's service
    // running, is a rival build registered beside it, is the filter in the
    // live device stack, is the package installed) and connection state (are
    // there live BTHENUM instances, are the only entries charge-cable
    // phantoms, does the Bluetooth pointer child resolve). What they never
    // decide is whether the MOUSE battery percent is arriving, or whether the
    // wheel really scrolls: a mouse -2, -3 or -1 is deliberately silent, while
    // a connected keyboard reading -2 IS reported, by rule 2d. That rule reads
    // the sentinel and the PID, never the pairing record itself: a missing SDP
    // cap is the measured cause behind it and the one with a guided fix, but
    // an interface answering 0 reaches -2 as well (KB_BATTERY_ZERO), so the
    // finding is an inference the log can contradict.
    // DeviceSnapshot.MultitouchAdvancing is a tri-state that can raise no
    // problem row in any state: false never occurs and null means only "no
    // evidence", so a still counter is never a fault - nor is it necessarily
    // null, because DeviceDiagReader keeps a proven advance reading true for
    // DeviceDiagReader.AliveMemory (60 s) after the last movement (the
    // alive-memory arm of DeviceDiagReader.EvaluateCounterSample, pinned by
    // EvaluateCounterSample_UnchangedShortlyAfterMovement_StaysAdvancing).
    // Either way the wheel is not something this arm can speak for.
    //
    // Measured on the reference PC: one menu open wrote "REPAIR_FINDINGS
    // raw=0 confirmed=0 pending=0" beside "REPAIR_SNAPSHOT pid=0323 ...
    // batt=-2", so the old wording "No problems found" stood directly above a
    // device row reading "Battery unavailable". Both lines were true; only the
    // header's scope was wrong, because "no problems" is a claim about the
    // whole device made by a checker that never read that mouse's battery and
    // never proved its wheel. Naming the two areas the wording does claim
    // keeps the row reassuring where it is entitled to be and silent where it
    // measured nothing.
    //
    // The one- and many-finding arms are unchanged: a confirmed fault keeps
    // the bare fault voice the rest of the menu is ranked against (the
    // PRECEDENCE note in ConfigFactView).
    internal static string MenuLabel(IReadOnlyList<RepairFinding> findings) => findings.Count switch
    {
        0 => "No driver or connection problems found",
        1 => findings[0].Title,
        _ => $"{findings.Count} problems found",
    };

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
