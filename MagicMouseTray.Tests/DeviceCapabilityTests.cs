// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DeviceCapabilityTests
{
    static readonly int[] AllSentinels = { 75, 0, -1, -2, -3 };

    static DeviceCapability.CapabilityFacts Facts(
        DeviceKind kind = DeviceKind.MagicMouseV3,
        string? pid = "0323",
        int pct = 80,
        string? bound = null,
        bool? pkg = null,
        bool? running = null,
        bool? inStack = null,
        bool? pointer = null,
        bool? advancing = null,
        RepairProblem? problem = null,
        bool? enabled = null,
        SdpPatchState? sdp = null)
        => new(kind, pid, pct, bound, pkg, running, inStack, pointer, advancing, problem,
               enabled, sdp);

    // A healthy 0323: package present, filter bound, service running, attached.
    static DeviceCapability.CapabilityFacts HealthyStack(bool? advancing = null, int pct = 80)
        => Facts(pct: pct, bound: "MagicMouseDriver204Scroll", pkg: true, running: true,
                 inStack: true, pointer: true, advancing: advancing);

    [Fact]
    public void Rows_OnNoEvidence_SayUnknown_AndNeverAccuse()
    {
        var rows = DeviceCapability.Rows(Facts(pct: -1));
        Assert.All(rows, r => Assert.DoesNotContain("not working", r, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("unknown", DeviceCapability.PointerRow(Facts(pct: -1)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", DeviceCapability.ScrollRow(Facts(pct: -1)), StringComparison.OrdinalIgnoreCase);
        // Nothing has arrived yet is the battery's form of the same absence:
        // no claim about the device, and no fault laid at its door.
        Assert.Equal("Battery: no reading yet", DeviceCapability.BatteryRow(Facts(pct: -1)));
    }

    [Fact]
    public void Rows_NeverBlank_ForAnyKindSentinelOrProblem()
    {
        foreach (DeviceKind kind in Enum.GetValues<DeviceKind>())
            foreach (var pct in AllSentinels)
                foreach (RepairProblem problem in Enum.GetValues<RepairProblem>())
                {
                    var rows = DeviceCapability.Rows(Facts(kind: kind, pid: null, pct: pct, problem: problem));
                    Assert.NotEmpty(rows);
                    Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r)));
                }
    }

    [Fact]
    public void Keyboard_GetsBatteryOnly_NoPointerOrScrollLine()
    {
        var rows = DeviceCapability.Rows(Facts(kind: DeviceKind.MagicKeyboard, pid: "029c", pct: -2));
        var single = Assert.Single(rows);
        Assert.Contains(DeviceCapability.BatteryLabel(-2), single);
        Assert.All(rows, r => Assert.DoesNotContain("Pointer", r, StringComparison.OrdinalIgnoreCase));
        Assert.All(rows, r => Assert.DoesNotContain("Scroll", r, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Trackpad_GetsNoScrollDriverLine()
    {
        var rows = DeviceCapability.Rows(
            Facts(kind: DeviceKind.MagicTrackpadV1, pid: "030e", pointer: true));
        Assert.All(rows, r => Assert.DoesNotContain("Scroll", r, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, r => r.StartsWith("Pointer:", StringComparison.Ordinal));
    }

    [Fact]
    public void Scroll_CounterMovement_ReadsAsWorking()
    {
        var row = DeviceCapability.ScrollRow(HealthyStack(advancing: true));
        Assert.Equal("Scroll: working", row);
    }

    // The signal is one-directional: counters that are not advancing may simply mean nobody
    // touched the mouse, so a healthy-looking stack must never be called broken.
    [Fact]
    public void Scroll_HealthyStackWithoutCounterMovement_IsNotAnAccusation()
    {
        var row = DeviceCapability.ScrollRow(HealthyStack(advancing: null));
        Assert.DoesNotContain("not working", row, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Scroll: driver attached (verified when in use)", row);
    }

    [Fact]
    public void Scroll_NoPackage_SaysNotInstalled_NotBroken()
    {
        var row = DeviceCapability.ScrollRow(Facts(pkg: false));
        Assert.Contains("not installed", row, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scroll_FindingWins_OverStackFacts()
    {
        var row = DeviceCapability.ScrollRow(
            HealthyStack(advancing: true) with { Problem = RepairProblem.FilterNotInStack });
        Assert.Contains("not attached", row);
    }

    [Fact]
    public void Pointer_LiveChild_Works_MissingChildFinding_DoesNot()
    {
        Assert.Equal("Pointer: working", DeviceCapability.PointerRow(Facts(pointer: true)));

        var missing = DeviceCapability.PointerRow(
            Facts(pointer: null, problem: RepairProblem.PointerChildMissing));
        Assert.Contains("not working", missing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Battery_UsesTheSentinelWordingTheRowLabelUses()
    {
        foreach (var pct in AllSentinels)
            Assert.Contains(DeviceCapability.BatteryLabel(pct), DeviceCapability.BatteryRow(Facts(pct: pct)),
                StringComparison.OrdinalIgnoreCase);

        Assert.Equal("Battery: no reading yet", DeviceCapability.BatteryRow(Facts(pct: -1)));
        Assert.Equal("Battery: 75%", DeviceCapability.BatteryRow(Facts(pct: 75)));
    }

    [Fact]
    public void BatteryLabel_SentinelWording()
    {
        Assert.Equal("75%", DeviceCapability.BatteryLabel(75));
        Assert.Equal("Battery unavailable", DeviceCapability.BatteryLabel(-2));
        Assert.Equal("Battery unavailable", DeviceCapability.BatteryLabel(-3));
        Assert.Equal("No reading", DeviceCapability.BatteryLabel(-1));
    }

    // The help item is the user asserting a symptom no check can prove, so it is offered
    // only where every readable fact already says the driver side is fine.
    [Fact]
    public void ScrollHelp_OfferedOnlyWhenTheDriverSideLooksFine()
    {
        Assert.True(DeviceCapability.OfferScrollHelp(HealthyStack(advancing: true)));
        Assert.True(DeviceCapability.OfferScrollHelp(HealthyStack(advancing: null)));

        Assert.False(DeviceCapability.OfferScrollHelp(HealthyStack() with { FilterInStack = null }));
        Assert.False(DeviceCapability.OfferScrollHelp(HealthyStack() with { FilterInStack = false }));
        Assert.False(DeviceCapability.OfferScrollHelp(HealthyStack() with { FilterServiceRunning = false }));
        Assert.False(DeviceCapability.OfferScrollHelp(HealthyStack() with { BoundFilter = null }));
        Assert.False(DeviceCapability.OfferScrollHelp(
            HealthyStack() with { Kind = DeviceKind.MagicMouseV2, Pid = "030d" }));
    }

    [Fact]
    public void DriverLabel_UsesBoundFilterWhenPresent()
    {
        Assert.Equal("MagicMouseDriver", DeviceCapability.DriverLabel(DriverStatus.Ok, "MagicMouseDriver"));
        Assert.Equal("applewirelessmouse", DeviceCapability.DriverLabel(DriverStatus.Ok, "applewirelessmouse"));
        Assert.Equal("Scroll driver not bound", DeviceCapability.DriverLabel(DriverStatus.NotBound, null));
        Assert.Equal(DriverPackageCatalog.StockHidServiceName,
            DeviceCapability.DriverLabel(DriverStatus.StockKmdf, null));
        Assert.Equal(DriverPackageCatalog.PatchedKmdfServiceName,
            DeviceCapability.DriverLabel(DriverStatus.PatchedKmdf, null));
        Assert.Equal("MagicMouseDriver",
            DeviceCapability.DriverLabel(DriverStatus.PatchedKmdf, "MagicMouseDriver"));
    }

    // The mouse is not broken and no report went missing: the user switched
    // the device off in this app, so the tray never polled it. "No reading"
    // would blame the device for a read that was never attempted - the
    // reference PC's v1 answers 97% through Feature 0x47 the moment it is
    // switched back on.
    [Fact]
    public void SwitchedOffDevice_SaysSo_AndNeverClaimsNoReading()
    {
        var off = Facts(kind: DeviceKind.MagicMouseV1, pid: "030d", pct: -1, enabled: false);

        var row = DeviceCapability.BatteryRow(off);
        Assert.Contains("switched off in this app", row);
        Assert.DoesNotContain("no reading", row, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DeviceCapability.Rows(off), r => r.Contains("switched off in this app"));
        Assert.Equal(DeviceCapability.BatteryOffLabel, DeviceCapability.BatteryLabel(-1, false));

        // A percent read before the switch is not replayed as if it were current.
        Assert.DoesNotContain("97", DeviceCapability.BatteryRow(off with { LastPct = 97 }));

        // Tri-state: no recorded preference is not "off", and a device that is
        // on reads exactly as it always did.
        Assert.Equal("Battery: no reading yet", DeviceCapability.BatteryRow(off with { EnabledInApp = null }));
        Assert.Equal("Battery: 97%", DeviceCapability.BatteryRow(off with { LastPct = 97, EnabledInApp = true }));
        Assert.Equal("No reading", DeviceCapability.BatteryLabel(-1, null));
    }

    // A Magic Keyboard percent is not the keyboard volunteering it: Windows
    // exposes no battery Feature cap until this repo's SDP patch inserts
    // 09 20 B1 02 into the device's CachedServices record. Measured on the
    // reference PC, 2026-09-16: keyboard 0239 (MAC e806884b0741) is PATCHED -
    // marker present once, no stock 81 02 C0 C0 - and reads a percent. So
    // where the patch has been read as applied, the row is allowed to name it
    // as the source; that is the difference between a number and a number the
    // user can trust.
    [Fact]
    public void KeyboardBattery_NamesTheSdpPatch_WhenItIsReadAsApplied()
    {
        var kb = Facts(kind: DeviceKind.MagicKeyboard, pid: "0239", pct: 100,
                       sdp: SdpPatchState.Applied);

        Assert.Equal("Battery: 100% (SDP patch)", DeviceCapability.BatteryRow(kb));
        Assert.Equal("Battery: 55% (SDP patch)",
            DeviceCapability.BatteryRow(kb with { LastPct = 55 }));
        Assert.Contains(DeviceCapability.Rows(kb), r => r.Contains("(SDP patch)"));

        // Same reading on a mouse or a trackpad is meaningless: they read a HID
        // report directly and this patch has nothing to do with them. It must
        // never appear on their rows even if a caller passes it.
        Assert.Equal("Battery: 100%", DeviceCapability.BatteryRow(
            kb with { Kind = DeviceKind.MagicTrackpadV2, Pid = "0265" }));
        Assert.Equal("Battery: 100%", DeviceCapability.BatteryRow(
            kb with { Kind = DeviceKind.MagicMouseV3, Pid = "0323" }));
    }

    // -2 is "present but blocked": the keyboard is there, Windows just exposes
    // no battery cap. Where the patch has been READ as missing, that is the
    // actual reason and the row says so instead of the generic wording - and
    // it is the one place the row may point at a fix, because the fix exists.
    [Fact]
    public void KeyboardBattery_SaysThePatchIsNeeded_OnlyWhenItIsReadAsMissing()
    {
        var blocked = Facts(kind: DeviceKind.MagicKeyboard, pid: "0239", pct: -2);

        Assert.Equal("Battery unavailable (needs the SDP patch)",
            DeviceCapability.BatteryRow(blocked with { Sdp = SdpPatchState.NotApplied }));

        // Unknown and "never read" are the same statement - no evidence - and
        // neither licenses the claim that the patch is missing. A patch that
        // reads as applied beside a blocked percent is not evidence against
        // itself either: something else is in the way, and the row must not
        // invent which.
        const string neutral = "Battery unavailable (Windows sends no report)";
        Assert.Equal(neutral, DeviceCapability.BatteryRow(blocked));
        Assert.Equal(neutral, DeviceCapability.BatteryRow(blocked with { Sdp = SdpPatchState.Unknown }));
        Assert.Equal(neutral, DeviceCapability.BatteryRow(blocked with { Sdp = SdpPatchState.Applied }));

        // A percent that reads while the patch reads as missing is a
        // contradiction between two readings, so the row claims neither side.
        Assert.Equal("Battery: 100%", DeviceCapability.BatteryRow(
            blocked with { LastPct = 100, Sdp = SdpPatchState.NotApplied }));

        // The user's own switch still wins over every one of these: nothing
        // was polled, so there is no source to name.
        Assert.Equal("Battery: not polled (switched off in this app)",
            DeviceCapability.BatteryRow(blocked with { Sdp = SdpPatchState.NotApplied, EnabledInApp = false }));
        Assert.Equal("Battery: not polled (switched off in this app)",
            DeviceCapability.BatteryRow(
                blocked with { LastPct = 100, Sdp = SdpPatchState.Applied, EnabledInApp = false }));
    }

    // Whatever the patch state, a keyboard's missing scroll driver is never a
    // row: it has no wheel, and drawing one would invent a fault.
    [Fact]
    public void KeyboardRows_StayBatteryOnly_WhateverThePatchSays()
    {
        foreach (var sdp in AllSdpStates)
        {
            var rows = DeviceCapability.Rows(
                Facts(kind: DeviceKind.MagicKeyboard, pid: "0239", pct: 100, sdp: sdp));
            var single = Assert.Single(rows);
            Assert.StartsWith("Battery:", single, StringComparison.Ordinal);
        }
    }

    // Menu width is a contract, not a preference: a ToolStripItem is as wide as
    // its text, and the sentence-length rows this file used to produce ran the
    // full width of the screen over the rest of the menu. Driven over every
    // fact combination each row switches on, so a wording that grows past the
    // cap fails here rather than in a screenshot.
    [Fact]
    public void EveryRow_FitsRowMax_OnOneLine()
    {
        foreach (var problem in AllProblems())
            foreach (var pointer in TriState)
                AssertMenuSafe(DeviceCapability.PointerRow(Facts(problem: problem, pointer: pointer)));

        foreach (var problem in AllProblems())
            foreach (var pkg in TriState)
                foreach (var bound in new string?[] { null, "", "applewirelessmouse" })
                    foreach (var running in TriState)
                        foreach (var inStack in TriState)
                            foreach (var advancing in TriState)
                                AssertMenuSafe(DeviceCapability.ScrollRow(Facts(
                                    problem: problem, pkg: pkg, bound: bound,
                                    running: running, inStack: inStack, advancing: advancing)));

        foreach (var pct in AllSentinels)
            foreach (var enabled in TriState)
                foreach (var sdp in AllSdpStates)
                    foreach (DeviceKind kind in Enum.GetValues<DeviceKind>())
                    {
                        AssertMenuSafe(DeviceCapability.BatteryRow(
                            Facts(kind: kind, pct: pct, enabled: enabled, sdp: sdp)));
                        AssertMenuSafe(DeviceCapability.BatteryLabel(pct, enabled));
                    }

        foreach (DeviceKind kind in Enum.GetValues<DeviceKind>())
            foreach (var pid in new string?[] { null, "0323", "030d", "0320" })
                foreach (var row in DeviceCapability.Rows(Facts(kind: kind, pid: pid)))
                    AssertMenuSafe(row);

        AssertMenuSafe(DeviceCapability.ScrollHelpItemLabel);
    }

    static void AssertMenuSafe(string row)
    {
        Assert.False(string.IsNullOrWhiteSpace(row), "empty menu row");
        Assert.DoesNotContain('\n', row);
        Assert.DoesNotContain('\r', row);
        Assert.True(row.Length <= DeviceCapability.RowMax,
            $"{row.Length} chars, cap {DeviceCapability.RowMax}: {row}");
    }

    static readonly bool?[] TriState = { null, true, false };

    // Null is deliberately in this set: "never read" and SdpPatchState.Unknown
    // are the same statement, and both have to render as no evidence.
    static readonly SdpPatchState?[] AllSdpStates =
    {
        null, SdpPatchState.Applied, SdpPatchState.NotApplied, SdpPatchState.Unknown,
    };

    static IEnumerable<RepairProblem?> AllProblems()
    {
        yield return null;
        foreach (var p in Enum.GetValues<RepairProblem>())
            yield return p;
    }
}
