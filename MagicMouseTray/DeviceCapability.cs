// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// Per-device copy for the tray menu: which of pointer, scroll and battery is
// actually working, in plain words, plus the battery sentinel wording shared
// with the row label.
//
// Driver SELECT pulls 0323 KMDF from magic-mouse-v3-windows-fix - not from
// magic-tray/driver/.
//
// Menu width is part of the contract here. Every row below is one line of at
// most RowMax characters: a tray menu item is as wide as its text, so a row
// written as a sentence renders as a screen-wide line over the rest of the
// menu. The reason lives in brackets after the verdict ("Scroll: not working
// (driver service not running)") rather than in a clause, and
// DeviceCapabilityTests proves the cap over every fact combination.
//
// Tri-state discipline: every capability fact here may be null, and null means
// "no evidence". A null MUST read as unknown and MUST NEVER read as broken -
// nagging a healthy PC is the failure this file exists to avoid. All text is
// plain ASCII (hyphens only, no em dashes, no smart quotes): this repo has been
// bitten by mojibake in tray strings.
internal static class DeviceCapability
{
    // The width every row below is written to. See the header note.
    internal const int RowMax = 60;

    // The user-initiated scroll help item. A question, not a verdict: the row
    // above it may well read "Scroll: working", and the point of this item is
    // that the user - not a counter in a registry key - is asserting the
    // symptom. Phrasing it as a claim ("Scroll is not working") put the menu in
    // the position of contradicting its own reading two lines earlier.
    internal const string ScrollHelpItemLabel = "Scroll problems? Get help";

    // What the battery reads as when the user has switched this device off in
    // this app. Not a sentinel: nothing was polled, so there is nothing to
    // report and no reading to claim.
    internal const string BatteryOffLabel = "Off in this app";

    internal static DeviceKind? KindForName(string name)
    {
        foreach (var m in MouseBatteryDevice.KnownMice)
            if (string.Equals(m.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                return m.Kind;
        foreach (var k in KeyboardBatteryDevice.KnownKeyboards)
            if (string.Equals(k.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                return DeviceKind.MagicKeyboard;
        return null;
    }

    internal static string BatteryLabel(int lastPct) => lastPct switch
    {
        >= 0 => $"{lastPct}%",
        -2 => "Battery unavailable",
        -3 => "Battery unavailable",
        _ => "No reading",
    };

    // Same label, told what the user did. A device switched off in this app is
    // not polled at all, so "No reading" would claim a failed read that never
    // happened. Null means the app has no preference recorded for the device,
    // which is not the same as off and must not read as it.
    internal static string BatteryLabel(int lastPct, bool? enabledInApp) =>
        enabledInApp == false ? BatteryOffLabel : BatteryLabel(lastPct);

    internal static string DriverLabel(DriverStatus driver, string? boundFilter)
    {
        if (!string.IsNullOrEmpty(boundFilter))
            return boundFilter;
        return driver switch
        {
            DriverStatus.Ok => "Connected",
            DriverStatus.NotBound => "Scroll driver not bound",
            DriverStatus.NotInstalled => "Scroll driver not detected",
            DriverStatus.UnknownAppleMouse => "Unknown Apple mouse",
            DriverStatus.Error => "Driver status unavailable",
            DriverStatus.StockKmdf => DriverPackageCatalog.StockHidServiceName,
            DriverStatus.PatchedKmdf => DriverPackageCatalog.PatchedKmdfServiceName,
            _ => "Unknown",
        };
    }

    // Everything the capability lines need about one device, gathered by the
    // caller from the live snapshot, the driver health read and the current
    // finding. Nulls are the "no snapshot / nothing readable" case.
    //
    // EnabledInApp is the user's own switch for this device (enabled_<pid> in
    // the config), and it is tri-state for a reason: false means the tray is
    // deliberately not polling, null means no preference is recorded. Only
    // false suppresses the battery reading, and it never invents one.
    //
    // Sdp is what SdpPatchReader last read for this device's Bluetooth record,
    // and it only ever means something for a Magic Keyboard: mice and trackpads
    // read their battery straight off a HID report and this patch has nothing
    // to do with them. Null and SdpPatchState.Unknown are the same statement -
    // the registry was not read, or could not be - and both keep the neutral
    // battery wording. The patch is never claimed without the marker.
    internal readonly record struct CapabilityFacts(
        DeviceKind Kind,
        string? Pid,
        int LastPct,
        string? BoundFilter,
        bool? FilterPackagePresent,
        bool? FilterServiceRunning,
        bool? FilterInStack,
        bool? PointerChildLive,
        bool? MultitouchAdvancing,
        RepairProblem? Problem,
        bool? EnabledInApp,
        SdpPatchState? Sdp = null);

    // A keyboard has no pointer of its own; every mouse and trackpad does.
    internal static bool PointerApplies(DeviceKind kind) =>
        kind != DeviceKind.MagicKeyboard;

    // The scroll filter family this app installs covers Apple mice only. A
    // keyboard has no wheel, and a trackpad scrolls through its own Boot Camp
    // trackpad filter which none of these checks read - so neither gets a
    // scroll-driver line it cannot be judged by.
    // TrayMenu.IsV3 also catches a 0323 that arrived with a different kind.
    internal static bool ScrollDriverApplies(DeviceKind kind, string? pid) =>
        kind is DeviceKind.MagicMouseV1 or DeviceKind.MagicMouseV2 or DeviceKind.MagicMouseV3
        || TrayMenu.IsV3(kind, pid);

    // The lines shown under a device row, in reading order: pointer, scroll,
    // battery. Lines that do not apply to this kind of device are absent
    // rather than rendered as unknown.
    internal static IReadOnlyList<string> Rows(CapabilityFacts f)
    {
        var rows = new List<string>(3);
        if (PointerApplies(f.Kind))
            rows.Add(PointerRow(f));
        if (ScrollDriverApplies(f.Kind, f.Pid))
            rows.Add(ScrollRow(f));
        rows.Add(BatteryRow(f));
        return rows;
    }

    internal static string PointerRow(CapabilityFacts f)
    {
        // A finding is positive evidence in its own right, so these two may say
        // "not working" even where the devnode probe found nothing to read.
        if (f.Problem == RepairProblem.PointerChildMissing)
            return "Pointer: not working (no pointer device created)";
        if (f.Problem == RepairProblem.NoInstances)
            return "Pointer: not working (Windows has no record of it)";

        return f.PointerChildLive switch
        {
            true => "Pointer: working",
            false => "Pointer: not working (registered but not started)",
            _ => "Pointer: unknown (no evidence either way)",
        };
    }

    internal static string ScrollRow(CapabilityFacts f)
    {
        // Registry and device-stack facts first: these are the states the
        // planner raises findings for, and they are readable without the mouse
        // being touched.
        switch (f.Problem)
        {
            case RepairProblem.FilterStoppedButBound:
                return ScrollServiceStopped;
            case RepairProblem.FilterNotInStack:
                return ScrollNotAttached;
            case RepairProblem.ConflictingFilters:
                return "Scroll: not working (two drivers registered)";
            case RepairProblem.FilterPackageMissing:
                return ScrollNoPackage;
        }

        // No snapshot at all: FilterPackagePresent is the reader's always-set
        // field, so a null here means nothing about the stack was readable.
        if (f.FilterPackagePresent is null)
            return "Scroll: unknown (driver state not readable)";

        if (f.FilterPackagePresent == false)
            return ScrollNoPackage;
        if (string.IsNullOrEmpty(f.BoundFilter))
            return "Scroll: not working (no driver bound)";
        if (f.FilterServiceRunning == false)
            return ScrollServiceStopped;
        if (f.FilterInStack == false)
            return ScrollNotAttached;

        // Counter movement in the driver's Diag key is the only positive proof
        // that the multitouch stream is flowing. Measured on the reference PC:
        // Diag\LastAclReceived oscillates 23/9 on a mouse whose wheel is
        // working (9 only means the last ACL frame was short), so it is never
        // read as a fault here. Counters standing still cannot tell an idle
        // mouse from a broken one, so it is not an accusation either - the user
        // asserts the symptom through the ScrollHelpItemLabel item instead.
        if (f.MultitouchAdvancing == true)
            return "Scroll: working";
        if (f.FilterInStack == true)
            return "Scroll: driver attached (verified when in use)";
        return "Scroll: unknown (cannot confirm it is attached)";
    }

    const string ScrollServiceStopped = "Scroll: not working (driver service not running)";
    const string ScrollNotAttached = "Scroll: not working (driver not attached)";
    const string ScrollNoPackage = "Scroll: not installed (no scroll driver on this PC)";

    internal static string BatteryRow(CapabilityFacts f)
    {
        // The user switched this device off in this app, so nothing was polled.
        // Saying "no reading" here would blame the device for a read the tray
        // never attempted; saying a percent would invent one.
        if (f.EnabledInApp == false)
            return "Battery: not polled (switched off in this app)";

        // A Magic Keyboard percent does not come from the device asking to be
        // read: Windows exposes no battery Feature cap on the stock SDP record
        // (marker 81 02 C0 C0), and this app's registry patch inserts the four
        // bytes 09 20 B1 02 that put one on COL02. Measured on the reference
        // PC, 2026-09-16: keyboard 0239, MAC e806884b0741, CachedServices value
        // 00010000 carries 09 20 B1 02 once and no stock marker, and the
        // percent reads 0..100; without it KeyboardBatteryDevice returns -2.
        // So where the patch state is READ, the row names it - and only there.
        // Unknown keeps the neutral wording below: no evidence is not a fault
        // and must never be written as one.
        if (f.Kind == DeviceKind.MagicKeyboard)
        {
            if (f.LastPct >= 0 && f.Sdp == SdpPatchState.Applied)
                return $"Battery: {f.LastPct}% (SDP patch)";
            // -2 is KeyboardBatteryDevice's "present but the report is not
            // arriving" sentinel, and it has two producers, not one: the
            // KB_BATTERY_BLOCKED return (no battery Feature cap, which IS the
            // stock SDP record) and the KB_FEATURE_BLOCKED return (the cap is
            // there but HidD_GetFeature failed, which a patched keyboard with a
            // wedged interface also reaches). A rejected zero or a junk byte is
            // -1 now (KeyboardBatteryDevice.ClassifyFeatureByte), so those no
            // longer land here - but the two blocked arms are still not
            // distinguishable from the sentinel alone. That is what the
            // f.Sdp == SdpPatchState.NotApplied gate is for: the patch state is
            // independent evidence (SdpPatchReader reads the Bluetooth service
            // cache and only answers NotApplied on a record that carries RID
            // 0x47 with the untouched COL02 close), not something inferred from
            // the read that just failed, so the row names the patch only where
            // the record is known to be missing it. Unknown - no MAC, no
            // readable subtree, no candidate record - and Applied both fall
            // through to the neutral wording below.
            if (f.LastPct == -2 && f.Sdp == SdpPatchState.NotApplied)
                return $"{BatteryLabel(f.LastPct)} (needs the SDP patch)";
        }

        return f.LastPct switch
        {
            // BatteryLabel is the wording the row label and tooltip already
            // use; reusing it keeps one convention for the sentinels.
            >= 0 => $"Battery: {BatteryLabel(f.LastPct)}",
            -2 or -3 => $"{BatteryLabel(f.LastPct)} (Windows sends no report)",
            _ => "Battery: no reading yet",
        };
    }

    // Whether to offer ScrollHelpItemLabel. Offered wherever every readable
    // fact says the scroll driver is in place, which is exactly the case no
    // automatic check can settle: the wheel may still be dead because the mouse
    // is not sending its multitouch stream. It is deliberately NOT gated on the
    // observed scroll row - "Scroll: working" is a statement about counters
    // moving, and the user reporting that scrolling is wrong anyway is the
    // whole point of this path. The driver-side gate is what keeps it off PCs
    // where the menu already has a real fault to show instead.
    internal static bool OfferScrollHelp(CapabilityFacts f) =>
        TrayMenu.IsV3(f.Kind, f.Pid)
        && !string.IsNullOrEmpty(f.BoundFilter)
        && f.FilterServiceRunning == true
        && f.FilterInStack == true;
}
