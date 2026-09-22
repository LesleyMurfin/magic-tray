// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// What each driver choice is EXPECTED to deliver for one device, plus which
// choice this app recommends and one line of advice for the state the device is
// in right now.
//
// This file is a pure decision layer. It reads no registry, starts no process,
// opens no HID handle and writes no log, so every cell below is testable off
// device. The live facts (is the filter bound, is it running, is it in the
// stack, did multitouch advance) belong to DriverHealthChecker and
// DeviceCapability: those describe the PRESENT. This file describes what a
// choice WOULD give you, which is the only way the menu can recommend anything.
//
// Honesty rules this file obeys, same as the rest of the app:
//   - CapabilityExpectation.Unknown means "cannot be predicted", never "broken".
//   - A driver that does not apply to a device gets NotApplicable, never Dead.
//     A keyboard has no wheel; saying its scroll is dead would be a lie.
//   - An unreadable or unrecognised DriverStatus never claims the device is on
//     the recommended driver, and never predicts what works from it.
// All text is plain ASCII (hyphens only, no em dashes, no smart quotes): this
// repo has been bitten by mojibake in tray strings.
//
// Every non-obvious cell cites the documentation line it came from. Quoted
// source lines are from README.md and docs/v3.html at the time of writing.
// Where the driver repo (magic-mouse-v3-windows-fix) has since moved past this
// repo's README, the driver repo wins and the cell cites it: its PR #19
// (docs/two-drivers) is the current capability matrix, and its issue #22 is
// the open question behind the v1/v2 battery cell below. A cell may never be
// upgraded from Unknown by a sentence in this repo's README that was written
// before the hardware was measured.
internal enum CapabilityExpectation
{
    Works,         // this choice is expected to deliver the capability
    Dead,          // this choice is expected NOT to deliver it
    EitherOrOnly,  // deliverable, but not at the same time as its rival
    NotApplicable, // the device has no such capability to deliver
    Unknown,       // no documented expectation either way
}

// One driver the user can be on, with the expectation for each capability.
// Label matches the shipped radio copy in TrayMenu so the advice line and the
// radios cannot drift apart.
internal sealed record DriverOption(
    string Id,
    string Label,
    CapabilityExpectation Pointer,
    CapabilityExpectation Scroll,
    CapabilityExpectation Battery,
    bool Recommended,
    string Why);

internal static class DriverAdvisor
{
    internal const string IdKmdf = "kmdf";
    internal const string IdPatchedApple = "patched-apple";
    internal const string IdStockWindows = "stock-windows";
    internal const string IdBootCamp = "boot-camp";

    // --- Magic Mouse v3 / 2024 / PID 0323: three choices -------------------
    //
    // README.md:99-103 "The three driver choices for this mouse":
    //   "KMDF - the recommended one. Scroll and battery together. Self-signed,
    //    so it needs Test Mode."
    //   "Patched Apple - an old experiment, kept only for the record. Scroll or
    //    battery, never both at the same time."
    //   "Stock Windows - Windows' own Bluetooth mouse driver. Pointer only: no
    //    scroll. Battery percent usually still reads. No Test Mode."
    //
    // README.md:165-167, the "what to pick" table (Wheel / Battery columns):
    //   KMDF           -> "Yes" / "Yes"
    //   Patched Apple  -> "Yes, but only in its scroll mode" /
    //                     "Yes, but only in its battery mode"
    //   Stock Windows  -> "No" / "Often yes"
    static readonly DriverOption[] V3Options =
    [
        new(IdKmdf,
            TrayMenu.V3RadioKmdf,
            // README.md:156 recommended driver row for 0x0323: Scroll "Yes,
            // with Test Mode", Battery "Yes". README.md:263 "Magic Tray's
            // recommended path is KMDF from that repo: scroll and battery
            // (Input 0x90 on COL02)."
            Pointer: CapabilityExpectation.Works,
            Scroll: CapabilityExpectation.Works,
            Battery: CapabilityExpectation.Works,
            Recommended: true,
            Why: "The only choice that gives scroll and battery together. "
               + "It is self-signed, so it needs Test Mode on and Memory integrity off."),
        new(IdPatchedApple,
            TrayMenu.V3RadioPatchedApple,
            // Capabilities: README.md:166 "Wheel and battery are mutually
            // exclusive - one or the other, never both." README.md:217 "on it
            // scroll and battery are mutually exclusive - you get one or the
            // other, never both." docs/v3.html:239 "A patched Apple driver can
            // hold scrolling but lose the battery percent." That pair of cells
            // is what EitherOrOnly exists for: neither is Dead, and neither is
            // Works.
            //
            // Test Mode is deliberately NOT asserted on this option, in either
            // direction. The driver repo's PR #19 (docs/two-drivers) capability
            // matrix gives this route "Test Mode | not needed": the recommended
            // v1-binary-patch route now installs Apple's unmodified,
            // Microsoft-countersigned binary, bound by service name plus
            // LowerFilters because Apple's INF carries no 0323 entry, so it
            // "patches nothing" - no Test Mode, no cert import, and Secure Boot
            // and memory integrity may stay ON. Only the legacy byte-patched,
            // re-signed variant (thumbprint 16940C0F, CN=MagicMouseFix) needs
            // test signing. Both binaries bind under the SAME
            // applewirelessmouse service name, so a Test Mode requirement is a
            // property of the FILE on disk, never of this option - which is
            // also why the README.md:172 "Required" cell and README.md:229
            // "self-signed too" line are stale here and must not be copied back
            // in. This layer is pure and cannot look at the file; the reading
            // that settles it already exists as
            // SystemConfigChecker.ConfigReadings.BoundDriverSelfSigned, taken
            // from the bound .sys Authenticode signers, so the Why sends the
            // user to that check instead of guessing on their behalf.
            Pointer: CapabilityExpectation.Works,
            Scroll: CapabilityExpectation.EitherOrOnly,
            Battery: CapabilityExpectation.EitherOrOnly,
            Recommended: false,
            Why: "Apple's own filter driver, bound by service name and LowerFilters because "
               + "Apple's INF has no entry for this mouse. Scroll and battery are mutually "
               + "exclusive here: one or the other, never both. Test Mode is not a property "
               + "of this choice: it depends on which binary is bound under that service "
               + "name, and this app reads that off the driver file's signatures."),
        new(IdStockWindows,
            TrayMenu.V3RadioStockWindows,
            // README.md:103 / :167 "Pointer only: no scroll." -> Scroll Dead is
            // a documented expectation, not an absence of evidence.
            // Battery: README.md:89 "Battery percent appears as soon as the
            // mouse is paired - no driver, no reboot, no admin", and
            // docs/v3.html:239 "The stock setup can keep the battery percent
            // but lose scrolling." The table hedge "Often yes" (README.md:167)
            // and "Battery percent usually still reads" (README.md:215) are
            // carried in Why rather than downgrading the cell.
            Pointer: CapabilityExpectation.Works,
            Scroll: CapabilityExpectation.Dead,
            Battery: CapabilityExpectation.Works,
            Recommended: false,
            Why: "Windows' own Bluetooth mouse driver: the pointer works and the battery "
               + "percent usually reads, but the wheel does nothing. No Test Mode needed."),
    ];

    // --- Magic Mouse v1 and v2 / Apple Wireless Mouse: two choices each -----
    //
    // README.md:157-159 recommended driver for 0x030D, 0x0269 and 0x0310 is
    // the Apple driver - the shipped radio label for it is still "Boot Camp"
    // (TrayMenu.V1V2RadioBootCamp) - with Scroll "Yes".
    // README.md:205 "Catalog-signed. Test Mode is not required."
    // README.md:211 "Two routes put the same Apple-signed
    // applewirelessmouse.sys on these mice ... neither needs Test Mode", and
    // the reference PC's live 030D confirms it: its .sys carries a WDKTestCert
    // self-signed signature PLUS two valid Microsoft Windows Hardware
    // Compatibility Publisher signatures, and it loads with Test Mode off.
    //
    // WHY THESE Why STRINGS DENY THE BOOT CAMP INSTALLER
    // --------------------------------------------------
    // "Boot Camp" names two different things, and only one of them works on a
    // PC that is not a Mac:
    //   - the Boot Camp DRIVER: Apple's applewirelessmouse.sys, shipped inside
    //     Apple's Windows support package. This is what makes the wheel
    //     scroll, and the reference PC proves it on ordinary PC hardware.
    //   - Apple's Boot Camp INSTALLER, which refuses to run when the machine
    //     is not a Mac. Nobody needs it for scroll, so no user-facing string
    //     here may read as "run the Boot Camp installer".
    // The driver repo sbagirici/apple-magic-mouse-scroll-fix-windows states
    // the whole problem in three steps, and the third is exactly that trap:
    //   1. "Driver doesn't exist on Windows" -> extract it from Apple's Boot
    //      Camp package.
    //   2. "Apple's INF doesn't list Magic Mouse 2's Bluetooth PID (0323)" ->
    //      register the driver as a LowerFilter manually.
    //   3. "Boot Camp installer checks for Mac hardware" -> "Skip the
    //      installer entirely - install only the driver".
    // Same repo, on signing and on system state: "It is digitally signed by
    // Apple and countersigned by Microsoft (WHQL)", "Secure Boot | Compatible
    // (driver is Microsoft-signed)" and "Does not modify system boot
    // configuration". That is the basis for the Why's promise that Test Mode
    // is not required and no security setting has to change.
    //
    // SAME BINARY, TWO ROUTES, ONE END STATE
    // --------------------------------------
    // The driver file is byte-identical across both routes, so neither route
    // ships a modified driver and neither is second-class:
    //   the repo's bundled driver/applewirelessmouse.sys is SHA256
    //   08F33D7E3ECE2C73..., 78,424 bytes - the same hash and the same byte
    //   count as the file installed on the reference PC, and the same hash the
    //   repo's PR #19 classifies as "Apple unmodified -> AppleSigned accepted".
    // The two routes:
    //   - INF package route. Declarative; the reference PC's
    //     C:\Windows\INF\oem8.inf does it:
    //       [Apple.NTamd64]
    //       ...=AppleWirelessMouse, BTHENUM\{00001124-...}_VID&000205ac_PID&030d
    //       ...=AppleWirelessMouse, BTHENUM\{00001124-...}_VID&0001004c_PID&0323
    //       [AppleWirelessMouse.NT.HW.AddReg]
    //       HKR,,"LowerFilters",0x00010000,"applewirelessmouse"
    //     (the 0323 line there is a LOCAL addition: Apple's shipped INF lists
    //     only 030D, 0310 and 0269.)
    //   - manual service route (install.ps1 in the driver repo). No INF at
    //     all: it matches the device by BTHENUM\{00001124-...} plus VID
    //     004C|05AC, runs sc.exe create applewirelessmouse, writes
    //     LowerFilters as a MultiString on
    //     HKLM\SYSTEM\CurrentControlSet\Enum\<instanceId>, then
    //     Disable-PnpDevice / Enable-PnpDevice.
    // HKR inside a .NT.HW section writes the device's HARDWARE key - the
    // instance key - which is the very location install.ps1 sets by hand. Both
    // routes therefore converge on the same registry value naming the same
    // Apple binary as a lower filter on the same device, so the resulting
    // capabilities are identical and the Why must not rank one above the
    // other.
    //
    // WHAT THIS APP DOES, WHICH IS LESS THAN EITHER ROUTE
    // ---------------------------------------------------
    // For v1/v2 this app only opens the documented download page
    // (DriverInstaller.OfferV1V2ScrollFix -> V1V2BootCampPageUrl ->
    // DriverPackageCatalog.TealtadpolePageUrl). It runs no installer, no
    // script, no pnputil, and binds nothing. The Why says that plainly so the
    // copy cannot be read as a one-click fix.
    //
    // BATTERY: the 030D is MEASURED, nothing else in this family is, so these
    // rows no longer share one table and the split is by PID rather than by
    // DeviceKind (#137 - see HasMeasuredBattery below).
    // Driver repo issue #22 asked whether the battery percent can be read on
    // these pre-0323 models at all, because the percent this app reads on the
    // v3 is HID input report 0x90 on the COL02 vendor collection and these
    // models pre-date that collection. Measured on the reference PC's paired
    // 030D, tray running with enabled_030d=true (2026-09-15 20:39:26):
    //
    //   MOUSE_BATTERY_OK device=Magic Mouse v1 pct=97% (unified Feature 0x47)
    //   REPAIR_SNAPSHOT pid=030d bt=2 usb=0 bound=applewirelessmouse
    //                   svc=running ... batt=97
    //
    // So issue #22 is answered for v1, but not the way it was framed. The v1
    // has NO COL02 and no Input 0x90: one collection-less HID node,
    // HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&000205AC_PID&030D\A&137E1BF2&9&0000,
    // with no &Col01 / &Col02 sibling under Enum\HID where the v3 has parent
    // plus &Col01 plus &Col02. Its descriptor declares the battery on that one
    // unified mouse collection instead - read-only HidP_GetValueCaps gives one
    // feature value cap, report id 0x47, usage page 0x0006 usage 0x0020,
    // 8 bits, logical 0..100, FeatureReportByteLength 2, and no 0xFF00 /
    // 0x0014 top level anywhere - which is exactly
    // MouseBatteryDevice.ReadV1V2Feature's unifiedApple branch. That is the
    // path the 97% came back on.
    //
    // The one limit on that measurement is the MODEL, and the model is the PID.
    // The percent is read by this app itself, off HID through
    // MouseBatteryDevice.ReadV1V2Feature's unifiedApple branch, so it does not
    // depend on which scroll filter is bound - the 030D's channel is a FEATURE
    // report the device answers, not the v3's COL02 input collection that the
    // Apple filter's presence appears and disappears with. Both rows of the
    // measured table below therefore carry Works, and only a 030D reaches it.
    //
    // WHY THE SPLIT IS BY PID AND NOT BY DeviceKind (#137)
    // ----------------------------------------------------
    // 0310 is the Apple Wireless Mouse: a different, older device that has
    // never been paired to this PC, and MouseBatteryDevice.KnownMice
    // deliberately files it under DeviceKind.MagicMouseV1. That mapping is
    // kept, because Kind drives battery CHEMISTRY elsewhere in the app -
    // BatteryAlertPolicy.cs:31-33 reads MagicMouseV1 as "AA cells, tell the
    // user to replace them", which is right for an Apple Wireless Mouse and
    // would become a lie if the kind were remapped to MagicMouseV2, whose
    // owners are told to plug in a Lightning cable
    // (BatteryAlertPolicy.cs:166). So the kind stays where the chemistry is
    // right, and the battery CLAIM - the only cell the 030D measurement backs
    // - is keyed on the PID here instead.
    //
    // 0310 and 0269 therefore both get the unconfirmed table below: no
    // hardware for either, and one model's Feature 0x47 result may never be
    // copied onto another, so driver repo issue #22 stays open for both. That
    // Unknown is untested hardware, never a missing capability - per the
    // honesty rules at the top of this file Unknown means "cannot be
    // predicted" and never "broken", so no option below claims a dead battery.
    // README.md:56 "Mouse battery in the tray | Yes (Bluetooth + USB HID when
    // Windows exposes it)" is a statement about this app, hedged on Windows
    // exposing the report, and still cannot promote an unmeasured cell on its
    // own.
    //
    // The scroll story, the Test Mode answer and the two install routes are
    // identical for every PID in this family: only the battery cell and its one
    // sentence differ between the two tables.
    static readonly DriverOption[] MeasuredV1Options =
    [
        new(IdBootCamp,
            TrayMenu.V1V2RadioBootCamp,
            Pointer: CapabilityExpectation.Works,
            Scroll: CapabilityExpectation.Works,
            // Works: measured on this exact route, on a 030D - see the
            // MOUSE_BATTERY_OK line quoted above. Driver repo issue #22 is
            // answered for that model and for no other.
            Battery: CapabilityExpectation.Works,
            Recommended: true,
            // Wording: see the "WHY THESE Why STRINGS DENY THE BOOT CAMP
            // INSTALLER" and "SAME BINARY, TWO ROUTES" notes above. The
            // registry mechanics stay in the comments - a user reading a tray
            // menu gets "the same Apple driver file doing the same job", not a
            // filter-stack lecture.
            Why: "Apple's own mouse driver - the one Apple ships for Windows - and the only "
               + "choice here that makes the wheel scroll. Apple signed it and Microsoft "
               + "countersigned it, so Test Mode is not required and no security setting has "
               + "to change. You do not run Apple's Boot Camp installer to get it: that "
               + "installer refuses to run on a PC that is not a Mac. You install just the "
               + "driver, either from the INF package or with a script that registers the "
               + "driver directly - both end with the same Apple driver file doing the same "
               + "job, so the result is the same either way. This app only opens the download "
               + "page for the INF package; it installs nothing itself. The battery percent "
               + "reads too, measured on a paired Magic Mouse v1 (030D) through its unified "
               + "Feature 0x47 report."),
        new(IdStockWindows,
            TrayMenu.V1V2RadioStockWindows,
            // README.md:201 "Scroll needs an Apple mouse filter driver
            // installed and bound on Windows 10 or Windows 11." Stock Windows
            // is exactly the state where that filter is absent, so the wheel is
            // dead by documentation, not by guess.
            Pointer: CapabilityExpectation.Works,
            Scroll: CapabilityExpectation.Dead,
            // Works: same as Boot Camp above. This app reads the percent off
            // HID itself (Feature 0x47 on the one unified collection), so the
            // read does not depend on the Apple scroll filter being bound.
            Battery: CapabilityExpectation.Works,
            Recommended: false,
            Why: "Windows' own driver. The pointer works and the battery percent still reads "
               + "- this app reads it off the mouse itself, through the same unified Feature "
               + "0x47 report - but the wheel does nothing."),
    ];

    // Same two choices as the measured 030D table, same scroll story, same Test
    // Mode answer. The battery cells differ for one reason only: nobody has run
    // this app against the other models in this family (0269 and 0310), so
    // driver repo issue #22 stays open for them. That is a gap in TESTING, not
    // a known failure - they may well read exactly like the 030D. The 030D's
    // Feature 0x47 result is still not copied here: it is a different report
    // descriptor on a different mouse, and this app has never read one. The
    // sentences below name no model, because this table serves both the v2
    // (0269) and the Apple Wireless Mouse (0310) and the honest claim is the
    // same for each: this model has not been measured.
    static readonly DriverOption[] UnconfirmedBatteryOptions =
    [
        new(IdBootCamp,
            TrayMenu.V1V2RadioBootCamp,
            Pointer: CapabilityExpectation.Works,
            Scroll: CapabilityExpectation.Works,
            // Unknown: driver repo issue #22, no v2 hardware measured.
            Battery: CapabilityExpectation.Unknown,
            Recommended: true,
            // Same wording as the v1 row and for the same reasons, down to the
            // two named routes; only the battery sentence differs, because only
            // the battery has not been measured here.
            Why: "Apple's own mouse driver - the one Apple ships for Windows - and the only "
               + "choice here that makes the wheel scroll. Apple signed it and Microsoft "
               + "countersigned it, so Test Mode is not required and no security setting has "
               + "to change. You do not run Apple's Boot Camp installer to get it: that "
               + "installer refuses to run on a PC that is not a Mac. You install just the "
               + "driver, either from the INF package or with a script that registers the "
               + "driver directly - both end with the same Apple driver file doing the same "
               + "job, so the result is the same either way. This app only opens the download "
               + "page for the INF package; it installs nothing itself. Whether the battery "
               + "percent reads on this model is not confirmed: this app has never been run "
               + "against one."),
        new(IdStockWindows,
            TrayMenu.V1V2RadioStockWindows,
            // README.md:201, as for the v1: no Apple filter, no wheel.
            Pointer: CapabilityExpectation.Works,
            Scroll: CapabilityExpectation.Dead,
            // Unknown: driver repo issue #22, no v2 hardware measured.
            Battery: CapabilityExpectation.Unknown,
            Recommended: false,
            Why: "Windows' own driver. The pointer works and the wheel does not. Whether the "
               + "battery percent reads is not confirmed on this model: this app has never "
               + "been run against one."),
    ];

    // --- Magic Keyboard: one choice ----------------------------------------
    //
    // README.md:177-181 keyboard table: the Driver column is the "SDP patch
    // (not a kernel driver)", the Scroll column is "n/a" and Battery is "Yes
    // after patch". README.md:269 "A one-time registry patch of the Bluetooth
    // SDP cache ... No kernel driver."
    // So there is exactly one driver story (Windows' own), scroll is
    // NotApplicable - a keyboard has no wheel to be dead - and the pointer is
    // NotApplicable too, matching DeviceCapability.PointerApplies, which
    // excludes MagicKeyboard.
    static readonly DriverOption[] KeyboardOptions =
    [
        new(IdStockWindows,
            TrayMenu.V3RadioStockWindows,
            Pointer: CapabilityExpectation.NotApplicable,
            Scroll: CapabilityExpectation.NotApplicable,
            Battery: CapabilityExpectation.Works,
            Recommended: true,
            Why: "Windows' own keyboard driver is the only choice. There is no scroll "
               + "driver for a keyboard, and battery percent comes from the one-time "
               + "SDP cache patch rather than from any driver."),
    ];

    // --- Magic Trackpad: one choice ----------------------------------------
    //
    // README.md:187 "Battery only: percent, enable, threshold, time alerts.
    // No KMDF / Boot Camp radios." README.md:191-193 trackpad table: Driver
    // "None", Scroll "n/a", Battery "Yes".
    // A trackpad therefore gets one driver story and NotApplicable scroll. The
    // app can offer the Boot Camp trackpad INF for the v1 pad
    // (TrayMenu.ShowTrackpadV1BootCamp), but that is a separate device-row
    // offer and not a scroll choice this layer can predict scroll for, so it is
    // deliberately not a second option here.
    static readonly DriverOption[] TrackpadOptions =
    [
        new(IdStockWindows,
            TrayMenu.V3RadioStockWindows,
            Pointer: CapabilityExpectation.Works,
            Scroll: CapabilityExpectation.NotApplicable,
            Battery: CapabilityExpectation.Works,
            Recommended: true,
            Why: "Windows' own driver is the only choice. Pointer and battery work; "
               + "this app ships no trackpad scroll or gesture driver."),
    ];

    // --- What a keyboard or a trackpad is ACTUALLY running -----------------
    //
    // These two kinds were the one family this layer could only speak about in
    // the negative. DriverHealthChecker never looks at them - it is called
    // with skipNonScroll: true (DriverHealthChecker.cs:406) and its gate at
    // :494-498 drops every PID in NonScrollApplePids - so their DriverStatus
    // arrives null, CurrentOptionId cannot name an option, and the advice fell
    // through to UnidentifiedStateClause: "The driver bound to this device has
    // not been read yet, so what works cannot be confirmed." On a Magic
    // Keyboard that sentence is false. The driver is perfectly readable, and
    // it is Microsoft's own from end to end. Measured on the reference PC
    // (2026-09-16), keyboard PID 0239, every node Status OK:
    //   BTHENUM parent : hidbth.inf, Microsoft, 10.0.26100.8737, stack
    //                    \Driver\HidBth, \Driver\BthEnum; Lower/UpperFilters
    //                    EMPTY
    //   ...&COL01      : Service kbdhid, keyboard.inf, Microsoft,
    //                    10.0.26100.8972, stack \Driver\kbdclass,
    //                    \Driver\kbdhid, \Driver\HidBth; filters EMPTY
    //   ...&COL02/03   : hidserv.inf, Microsoft, stack \Driver\HidBth; the
    //                    battery Feature cap lives on COL02 (the col02 gate
    //                    in DeviceRegistry.TryClassify)
    // So the honest line for that row is affirmative and specific, and this is
    // where it is written.
    //
    // This stays a PURE function, like everything else in this file: the live
    // reads belong to StockDriverReader (the driver in use) and SdpPatchReader
    // (whether this repo's registry patch is applied), both non-elevated and
    // read-only. Their results arrive here as data.
    //
    // Why this is not a new DriverStatus member: that enum is mouse-shaped.
    // Classify (DriverHealthChecker.cs:279-280) would call a keyboard
    // UnknownAppleMouse, Aggregate (:355-376) would poison the global status,
    // AfterFilterServiceState (:311-321) would flip Ok to NotBound, and
    // BoundCandidates / PreferredBoundName (:75-142) are filtered to the
    // Apple/KMDF service-name families and can never return kbdhid or HidBth.
    // A keyboard therefore gets its own small notion - "here is the stock
    // driver that was read" - and nothing else changes.
    //
    // Battery is a SEPARATE story from the driver on both kinds, and the two
    // kinds do not share it:
    //   keyboard - the percent needs this repo's one-time patch of the
    //     Bluetooth SDP cache (scripts/kbd-patch-cachedservices.ps1:37-38,
    //     :129 insert 09 20 B1 02 where a stock record carries 81 02 C0 C0),
    //     NOT a driver. KeyboardBatteryDevice.GetBatteryPercent returns -2
    //     until it is applied. SdpPatchReader now reads back whether it
    //     landed, so applied / not applied / could not check are three
    //     different sentences below, and Unknown NEVER implies missing.
    //   trackpad - no patch at all; see TrackpadBatterySentence.
    internal static string? StockDriverLine(
        DeviceKind kind, string? pid, StockDriverInfo? info, SdpPatchState? sdp)
    {
        if (!HasStockDriverEvidence(kind, pid, info))
            return null;

        var battery = kind == DeviceKind.MagicKeyboard
            ? KeyboardBatterySentence(sdp)
            : TrackpadBatterySentence();
        return $"{DriverSentence(kind, info!)} {HealthSentence(info!)} {battery}";
    }

    // The four kinds this app binds no vendor driver for: Windows' own driver
    // is the only thing they can be on, so "which driver" is a reading rather
    // than a choice. Shared by CurrentOptionId and by the stock-driver line so
    // the set cannot drift between them.
    internal static bool IsStockOnlyKind(DeviceKind kind) =>
        kind is DeviceKind.MagicKeyboard or DeviceKind.MagicTrackpadV1
             or DeviceKind.MagicTrackpadV2 or DeviceKind.MagicTrackpadV3;

    // Whether there is anything to say. A null record means StockDriverReader
    // could not read the driver, and a record whose every field came back
    // empty says just as little: both leave the old "not read yet" clause in
    // place, because that clause is then TRUE. A 0323 that arrived with some
    // other kind keeps the v3 mouse story, exactly as OptionsFor does.
    internal static bool HasStockDriverEvidence(DeviceKind kind, string? pid, StockDriverInfo? info)
    {
        if (info is null || TrayMenu.IsV3(kind, pid) || !IsStockOnlyKind(kind))
            return false;

        return Clean(info.Service) is not null
            || Clean(info.InfPath) is not null
            || Clean(info.Provider) is not null
            || Clean(info.Version) is not null
            || StackNames(info.Stack).Count > 0;
    }

    // Plain words first, then the identifying facts, so a user who does not
    // know what an INF is still learns that Windows' own driver is in use.
    static string DriverSentence(DeviceKind kind, StockDriverInfo info)
    {
        var facts = new List<string>(4);
        var service = Clean(info.Service);
        var inf = Clean(info.InfPath);
        if (service is not null && inf is not null)
            facts.Add($"the {service} service from {inf}");
        else if (service is not null)
            facts.Add($"the {service} service");
        else if (inf is not null)
            facts.Add($"the driver package {inf}");
        if (Clean(info.Provider) is string provider)
            facts.Add($"provider {provider}");
        if (Clean(info.Version) is string version)
            facts.Add($"version {version}");
        var stack = StackNames(info.Stack);
        if (stack.Count > 0)
            facts.Add($"driver stack {string.Join(", ", stack)}");

        return $"{LongDriverName(kind)} is what this {KindNoun(kind)} is running on: "
             + $"{string.Join(", ", facts)}.";
    }

    // "Present and problem-free" is the whole claim because it is the whole of
    // what was measured. StockDriverReader.NodeOk is
    // problem == 0 && (status & DN_HAS_PROBLEM) == 0 and never reads DN_STARTED
    // (that omission is recorded as a measured decision in NodeOk's own
    // comment): the reference PC's BTHENUM {00001200} SDP parent
    // counts toward AllNodesOk=true on status word 0x01802000 with DN_STARTED
    // CLEAR, because that profile node carries no function driver to start
    // while the keyboard types perfectly. So neither branch may say "started" -
    // the affirmative one would assert it on a node where it is measured clear,
    // and the negative one would imply a started check was attempted.
    //
    // AllNodesOk = false means NOT CONFIRMED, never broken - it is also what an
    // unreadable status returns. Missing evidence is not a fault, so the false
    // branch still names the driver affirmatively and never asks the user to
    // repair anything.
    static string HealthSentence(StockDriverInfo info) =>
        info.AllNodesOk
            ? "Every device node it owns was read as present, and Windows reports no problem "
              + "with any of them, so it is working, and there is nothing for you to install: "
              + "Windows ships this driver itself."
            : "Not every device node it owns could be confirmed present and problem-free on "
              + "this read, which is missing evidence and not a fault. There is still nothing "
              + "for you to install: Windows ships this driver itself.";

    // Three states, three sentences, and the Unknown one is the reason this
    // function exists: it must not read as "the patch is missing". The offer
    // it points NotApplied at is the existing orange "Fix battery reads" item
    // TrayApp builds on the device row under TrayMenu.ShowFixKeyboard, which
    // is still gated on the -2 the keyboard reports until the patch lands.
    //
    // The Applied branch also has to cover one real, non-broken state that
    // looks like a failure: patch present in CachedServices but the HID read
    // still -2, because hidbth only re-reads the SDP cache when the radio
    // cycles - the patch script ends on exactly that instruction ("Toggle BT
    // off/on for hidbth to re-read CachedServices",
    // scripts/kbd-patch-cachedservices.ps1). So this branch names the cure and
    // never says "broken", and never tells the user to run the patch again.
    static string KeyboardBatterySentence(SdpPatchState? sdp) => (sdp ?? SdpPatchState.Unknown) switch
    {
        SdpPatchState.Applied =>
            "The battery percent does not come from a driver at all: it comes from this app's "
            + "one-time patch of the Bluetooth SDP cache in the registry, and that patch is "
            + "applied for this keyboard right now, which is what lets the percent read. If "
            + "the battery row is still blank, turn Bluetooth off and back on - Windows only "
            + "re-reads that cache when the radio cycles - and do not run the patch again.",
        SdpPatchState.NotApplied =>
            "The battery percent does not come from a driver at all: it comes from this app's "
            + "one-time patch of the Bluetooth SDP cache in the registry, and that patch is "
            + "absent for this keyboard right now, so the percent cannot read until it is "
            + "there. The \"Fix battery reads\" item on this device's row applies it, once, "
            + "with one administrator approval.",
        _ =>
            "The battery percent does not come from a driver at all: it comes from this app's "
            + "one-time patch of the Bluetooth SDP cache in the registry. Whether that patch "
            + "is in place could not be checked on this read, so this app is not claiming "
            + "either way - a percent on the battery row above means it is there.",
    };

    // INFERENCE, not measurement, and reconciled AGAINST THE CODE (#134). No
    // trackpad has ever been paired to the PC this app is developed against, so
    // this sentence is read off the code path rather than off hardware - and
    // the code, not the older prose that used to stand here, is the ground
    // truth. MouseBatteryDevice.KnownMice carries 030E, 0265 and 0324, and
    // MouseBatteryDevice.GetBatteryPercent routes every trackpad kind to
    // ReadV1V2Feature; only MagicMouseV3 / 0323 takes ReadV3Rid90, which is the
    // fixed "Input 0x90 on COL02" path. So a trackpad's channel is whatever its
    // own descriptor declares, resolved at read time: Input report 0x90 when
    // the node this app opened is itself the 0xFF00 / 0x0014 vendor collection
    // (the splitVendor branch), otherwise the Generic Device Controls
    // battery-strength FEATURE report HidP_GetValueCaps finds (report 0x47 on
    // the measured 030D - the unifiedApple branch).
    //
    // The claim this comment used to make - "Input report 0x90 on the COL02
    // collection" - was the v3's path copied onto devices that never take it.
    // It was the PROSE that was corrected, not the read: with no trackpad to
    // measure there is no evidence the descriptor-driven path is wrong, and
    // rewriting a read to match a sentence would be exactly the guess this file
    // exists to refuse. The SDP patch
    // (scripts/kbd-patch-cachedservices.ps1) is keyboard-only and never touches
    // a trackpad. The whole cell therefore stays explicitly UNVERIFIED in the
    // copy below: if a trackpad is ever measured, this is the line to correct.
    static string TrackpadBatterySentence() =>
        "The battery percent comes straight off the trackpad over HID, read by this app "
        + "itself: nothing is installed for it and no registry patch is involved, so there is "
        + "nothing to apply here. The read takes the same descriptor-driven path as the older "
        + "mice: whichever battery report the trackpad's own HID descriptor declares - a "
        + "vendor input report if the node this app opens carries that collection, otherwise "
        + "the feature report - rather than the fixed input report the 2024 mouse uses. That "
        + "is read off this app's own code and not off hardware: no trackpad has been paired "
        + "to the PC this app was built against, so which report a trackpad really answers on "
        + "has never been confirmed here.";

    static string LongDriverName(DeviceKind kind) =>
        kind == DeviceKind.MagicKeyboard ? "Windows' own keyboard driver" : "Windows' own driver";

    static string KindNoun(DeviceKind kind) =>
        kind == DeviceKind.MagicKeyboard ? "keyboard" : "trackpad";

    // Registry data, not authored copy: a provider, INF or version string can
    // hold anything, and every string this app shows is plain ASCII on purpose
    // (this repo has been bitten by mojibake in tray strings). So values are
    // filtered to printable ASCII, trimmed and clamped, and whatever is left
    // empty counts as not read rather than as a blank fact.
    const int MaxFactChars = 64;
    const int MaxStackNames = 8;

    static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;

        var kept = new System.Text.StringBuilder(s!.Length);
        foreach (var c in s)
        {
            if (c >= 0x20 && c < 0x7F)
                kept.Append(c);
        }

        var cleaned = kept.ToString().Trim();
        if (cleaned.Length == 0)
            return null;
        return cleaned.Length <= MaxFactChars
            ? cleaned
            : cleaned[..MaxFactChars].TrimEnd() + "...";
    }

    // StockDriverReader hands over LEAF driver names ("kbdclass", "kbdhid",
    // "HidBth"): it strips the "\Driver\" prefix once, in the reader, so this
    // layer can print them as they are.
    static List<string> StackNames(string[]? stack)
    {
        var names = new List<string>(stack is null ? 0 : stack.Length);
        if (stack is null)
            return names;

        foreach (var raw in stack)
        {
            if (names.Count >= MaxStackNames)
                break;
            if (Clean(raw) is string name)
                names.Add(name);
        }
        return names;
    }

    // The one PID whose battery read this app has actually observed: the
    // reference PC's 030D, 97% through its unified Feature 0x47 report. Keyed
    // on the PID and never on DeviceKind, because 0310 - the Apple Wireless
    // Mouse, a different device that has never been measured - is filed under
    // DeviceKind.MagicMouseV1 on purpose, so that its AA-cell battery alerts
    // stay right (see the "WHY THE SPLIT IS BY PID" note above, #137). A
    // measurement taken on one model is never evidence about another, so this
    // is the whole gate on the Works battery cell for this family.
    internal static bool HasMeasuredBattery(string? pid) =>
        pid is not null && pid.Equals("030d", StringComparison.OrdinalIgnoreCase);

    // IsV3 first so a 0323 that arrived with some other DeviceKind still gets
    // the v3 story, exactly as TrayMenu.IsV3 is used elsewhere.
    internal static IReadOnlyList<DriverOption> OptionsFor(DeviceKind kind, string? pid)
    {
        if (TrayMenu.IsV3(kind, pid))
            return V3Options;
        // Every PID in the v1/v2 family shares the same two choices and the
        // same scroll story; only the battery cell differs, and it differs by
        // MODEL, which is the PID - not by DeviceKind. 030D was measured; 0269
        // and 0310 were not, and an unrecognised or missing PID has not been
        // either, so it falls to the unconfirmed table rather than inheriting
        // another model's measurement.
        if (TrayMenu.IsV1V2Mouse(kind))
            return HasMeasuredBattery(pid) ? MeasuredV1Options : UnconfirmedBatteryOptions;
        if (kind == DeviceKind.MagicKeyboard)
            return KeyboardOptions;
        if (kind is DeviceKind.MagicTrackpadV1 or DeviceKind.MagicTrackpadV2
                 or DeviceKind.MagicTrackpadV3)
            return TrackpadOptions;
        // Logitech rows are battery-only and not an Apple driver story at all.
        // No options means no recommendation and no advice, rather than advice
        // invented for a device this app never binds a driver to.
        return [];
    }

    internal static DriverOption? RecommendedFor(DeviceKind kind, string? pid)
    {
        foreach (var o in OptionsFor(kind, pid))
        {
            if (o.Recommended)
                return o;
        }
        return null;
    }

    // Which option the device is on right now, or null when the status does not
    // identify one. Mirrors TrayMenu's selected-radio mapping so the advice line
    // and the checked radio always agree:
    //   v3:    PatchedKmdf -> KMDF, PathAPatched -> Patched Apple,
    //          StockKmdf -> Stock Windows (TrayMenu.V3CheckedDriverRadio)
    //   v1/v2: Ok -> Boot Camp, NotInstalled -> Stock Windows
    //          (TrayMenu.V1V2CheckedDriverRadio)
    // Every other DriverStatus member is deliberately null: NotBound (package
    // present, nothing bound), Error (transient registry failure),
    // UnknownAppleMouse (PID outside the catalog), plus any status belonging to
    // the other device family. Null propagates to "cannot be confirmed" in
    // AdviceLine and to false in IsOnRecommended.
    internal static string? CurrentOptionId(DeviceKind kind, string? pid, DriverStatus? status)
    {
        if (status is null)
            return null;

        if (TrayMenu.IsV3(kind, pid))
        {
            return status switch
            {
                DriverStatus.PatchedKmdf => IdKmdf,
                DriverStatus.PathAPatched => IdPatchedApple,
                DriverStatus.StockKmdf => IdStockWindows,
                _ => null,
            };
        }

        if (TrayMenu.IsV1V2Mouse(kind))
        {
            return status switch
            {
                DriverStatus.Ok => IdBootCamp,
                DriverStatus.NotInstalled => IdStockWindows,
                _ => null,
            };
        }

        if (IsStockOnlyKind(kind))
        {
            // These devices bind no vendor filter, so the only driver they can
            // be on is Windows' own. DriverHealthChecker reports Ok both for
            // "healthy" and for "no Apple mouse paired", and NotInstalled when
            // no filter package exists - both are the stock state here.
            return status switch
            {
                DriverStatus.Ok => IdStockWindows,
                DriverStatus.NotInstalled => IdStockWindows,
                _ => null,
            };
        }

        return null;
    }

    internal static bool IsOnRecommended(DeviceKind kind, string? pid, DriverStatus? status)
    {
        var rec = RecommendedFor(kind, pid);
        if (rec is null)
            return false;
        return CurrentOptionId(kind, pid, status) == rec.Id;
    }

    // The line the menu shows in EVERY state, not only NotBound. Null only when
    // this app has no driver story for the device at all.
    //
    // stock / sdp are the readings for the kinds DriverHealthChecker skips: a
    // keyboard or trackpad whose driver has actually been read is described
    // from that reading instead of from a DriverStatus that was never taken.
    // Both default to null, which is exactly the old behaviour.
    internal static string? AdviceLine(
        DeviceKind kind, string? pid, DriverStatus? status,
        StockDriverInfo? stock = null, SdpPatchState? sdp = null)
    {
        var rec = RecommendedFor(kind, pid);
        if (rec is null)
            return null;

        // Read evidence outranks the mouse-shaped status on these kinds. Their
        // DriverStatus is null or a cross-family leftover no matter how healthy
        // they are, while the driver itself is sitting there readable - so once
        // it has been read, that reading is the line, and the "not read yet"
        // clause below is reserved for the case where it genuinely was not.
        if (StockDriverLine(kind, pid, stock, sdp) is string stockLine)
            return $"You are on the recommended driver for this device: {rec.Label}. {stockLine}";

        var options = OptionsFor(kind, pid);
        var currentId = CurrentOptionId(kind, pid, status);

        if (currentId is null)
            return $"{UnidentifiedStateClause(kind, pid, status)} {RecommendClause(rec)}";

        if (currentId == rec.Id)
            return $"You are on the recommended driver for this device: {rec.Label}. {rec.Why}";

        DriverOption? current = null;
        foreach (var o in options)
        {
            if (o.Id == currentId)
            {
                current = o;
                break;
            }
        }
        // CurrentOptionId only ever names an id from this device's own option
        // list, so this cannot be hit; keep it honest rather than throwing.
        if (current is null)
            return $"{UnidentifiedStateClause(kind, pid, status)} {RecommendClause(rec)}";

        return $"This device is on {current.Label}: {StayingCost(current)} {RecommendClause(rec)}";
    }

    static string RecommendClause(DriverOption rec) =>
        $"Recommended: {rec.Label} - {rec.Why}";

    // What it costs to stay on a non-recommended choice, in the user's terms,
    // read straight off that choice's own expectations so the sentence cannot
    // contradict the capability cells above it.
    static string StayingCost(DriverOption current) => (current.Scroll, current.Battery) switch
    {
        (CapabilityExpectation.EitherOrOnly, CapabilityExpectation.EitherOrOnly) =>
            "scroll and battery are mutually exclusive on it, so you can have one or "
            + "the other, never both.",
        (CapabilityExpectation.Dead, CapabilityExpectation.Works) =>
            "the pointer and the battery percent work, but the wheel stays dead.",
        (CapabilityExpectation.Dead, _) =>
            "the wheel stays dead on it.",
        // Boot Camp and KMDF are the recommended choice everywhere they are
        // offered, so no all-Works option reaches this switch today.
        _ => "it is not the driver this app recommends for this device.",
    };

    // Says what is unknown. Never predicts capabilities from a status it could
    // not match, and never reports the device as configured correctly.
    static string UnidentifiedStateClause(DeviceKind kind, string? pid, DriverStatus? status) =>
        status switch
        {
            null =>
                "The driver bound to this device has not been read yet, so what works "
                + "cannot be confirmed.",
            DriverStatus.NotBound =>
                TrayMenu.IsV3(kind, pid) || TrayMenu.IsV1V2Mouse(kind)
                    ? "A scroll driver is installed on this PC but nothing is bound to this "
                      + "mouse, so the wheel is dead."
                    : "No driver is bound to this device, so what works cannot be confirmed.",
            DriverStatus.NotInstalled =>
                "No scroll driver package for this device is installed on this PC, so the "
                + "wheel is dead.",
            DriverStatus.Error =>
                "The driver state could not be read on this PC, so what works cannot be "
                + "confirmed.",
            DriverStatus.UnknownAppleMouse =>
                "Windows reports an Apple mouse that is not in this app's catalog, so its "
                + "driver cannot be matched and what works cannot be confirmed.",
            // Cross-family leftovers: a v3 status seen on a v1/v2 row, or Ok on
            // a v3 row. Classify does not produce these pairings, but the menu
            // must still say something true if one ever arrives.
            _ =>
                "The driver bound to this device does not match any of the choices for it, "
                + "so what works cannot be confirmed.",
        };
}
