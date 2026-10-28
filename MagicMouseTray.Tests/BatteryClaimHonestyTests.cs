// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// The one claim in the Driver submenu that is read as a promise: "battery yes".
//
// DriverAdvisor already refuses to copy one model's measurement onto another
// for the v1/v2 mice - HasMeasuredBattery(pid) exists for exactly that, and
// 0269 and 0310 carry Unknown because no hardware was ever read. The same rule
// was never applied to the other two kinds:
//
//   KeyboardOptions  (DriverAdvisor.cs:388) - Battery: Works, unconditionally,
//     while the percent on a keyboard does not come from a driver at all. It
//     comes from this repo's one-time SDP cache patch
//     (scripts/kbd-patch-cachedservices.ps1), and KeyboardBatteryDevice returns
//     -2 until that patch lands. docs/DRIVER-MATRIX.md:54 states the measured
//     result as the patch's result ("the one-time SDP cache patch, not a
//     driver ... MEASURED", live read [47 0E] at :158). An UNPATCHED keyboard
//     is therefore a device this app has measured NOT reading a percent, and
//     the menu told it "battery yes" anyway.
//
//   TrackpadOptions  (DriverAdvisor.cs:411) - Battery: Works, on a kind
//     docs/DRIVER-MATRIX.md:55 calls "UNMEASURED here - no trackpad has been
//     hardware-tested in this project" and :166-168 repeats, and which
//     DriverAdvisor's own prose (TrackpadBatterySentence) admits has never been
//     paired to this project's PC. The advisor's paragraph says "has never been
//     confirmed here" in the same breath as the capability cell says yes.
//
// So this file pins the rule as ONE invariant over every kind rather than as
// per-cell copy: an affirmative battery prediction requires a measurement of
// THIS device in THIS state, and nothing else may render as a yes. The
// measured rows stay affirmative - a fix that turns every cell Unknown is a
// different lie, and is failed here too.
//
// The evidence seam is DriverAdvisor.HasMeasuredBattery, widened to the two
// facts a PID alone cannot carry: the kind (a trackpad has no measurement at
// any PID) and the SDP patch state (a keyboard's measurement is the patched
// keyboard's). The existing single-argument HasMeasuredBattery(pid) keeps its
// own meaning and its own tests - it is the v1/v2 model gate - and is not
// touched here.
public class BatteryClaimHonestyTests
{
    const string V3Pid = "0323";
    const string KeyboardPid = "0239";

    // The truth table, stated once. Every row is a documented measurement or a
    // documented absence of one, and the cells below are driven off it. A table
    // in the body rather than [InlineData], because SdpPatchState is internal
    // and a public theory method cannot take one.
    [Fact]
    public void A_battery_measurement_belongs_to_one_device_in_one_state()
    {
        (DeviceKind Kind, string? Pid, SdpPatchState? Sdp, bool Measured)[] table =
        [
            // Magic Mouse 2024: the battery is read by this app on the
            // reference PC, Input 0x90 on COL02 (docs/DRIVER-MATRIX.md:51
            // "MEASURED"). No patch is involved, so no patch state moves it.
            (DeviceKind.MagicMouseV3, V3Pid, null, true),
            (DeviceKind.MagicMouseV3, V3Pid, SdpPatchState.NotApplied, true),
            // Magic Mouse v1 030D: 97% through the unified Feature 0x47
            // (:52 "MEASURED: 97% on live hardware").
            (DeviceKind.MagicMouseV1, "030d", null, true),
            (DeviceKind.MagicMouseV1, "030D", SdpPatchState.Unknown, true),
            // v2 0269 and the Apple Wireless Mouse 0310: :53 "UNMEASURED".
            // 0310 arrives as DeviceKind.MagicMouseV1 on purpose (battery
            // chemistry), so the kind may not promote it.
            (DeviceKind.MagicMouseV2, "0269", null, false),
            (DeviceKind.MagicMouseV1, "0310", null, false),
            (DeviceKind.MagicMouseV2, "0310", SdpPatchState.Applied, false),
            (DeviceKind.MagicMouseV1, null, null, false),
            // Magic Keyboard: the measurement is the PATCHED keyboard's
            // (:54, :158). Unpatched, unreadable and never-checked are three
            // different states and none of them is that measurement.
            (DeviceKind.MagicKeyboard, KeyboardPid, SdpPatchState.Applied, true),
            (DeviceKind.MagicKeyboard, KeyboardPid, SdpPatchState.NotApplied, false),
            (DeviceKind.MagicKeyboard, KeyboardPid, SdpPatchState.Unknown, false),
            (DeviceKind.MagicKeyboard, KeyboardPid, null, false),
            (DeviceKind.MagicKeyboard, "0320", SdpPatchState.Applied, true),
            (DeviceKind.MagicKeyboard, "0320", null, false),
            // Magic Trackpad: :55 "UNMEASURED here - no trackpad has been
            // hardware-tested in this project". No patch state can change that:
            // the SDP patch is keyboard-only and never touches a trackpad.
            (DeviceKind.MagicTrackpadV1, "030e", SdpPatchState.Applied, false),
            (DeviceKind.MagicTrackpadV2, "0265", SdpPatchState.Applied, false),
            (DeviceKind.MagicTrackpadV3, "0324", SdpPatchState.Applied, false),
            (DeviceKind.MagicTrackpadV2, "0265", null, false),
            // Not this app's device at all.
            (DeviceKind.LogitechMouse, "c52b", SdpPatchState.Applied, false),
        ];

        foreach (var (kind, pid, sdp, measured) in table)
        {
            Assert.True(
                measured == DriverAdvisor.HasMeasuredBattery(kind, pid, sdp),
                $"{kind}/{pid}/{Name(sdp)} should be measured={measured}");
        }
    }

    // The invariant the per-cell edits kept breaking: no option, on any device,
    // in any patch state, may predict a working battery without the
    // measurement behind it - and no option may call a battery dead either,
    // because Unknown is untested hardware and never a fault.
    [Fact]
    public void No_option_predicts_a_battery_the_project_has_never_measured()
    {
        foreach (var (kind, pid) in EveryDevice())
        {
            foreach (var sdp in AllPatchStates())
            {
                var measured = DriverAdvisor.HasMeasuredBattery(kind, pid, sdp);
                foreach (var option in DriverAdvisor.OptionsFor(kind, pid, sdp))
                {
                    if (!measured)
                    {
                        Assert.False(
                            option.Battery == CapabilityExpectation.Works,
                            $"{kind}/{pid}/{Name(sdp)} option {option.Id} claims a battery "
                            + "this project has never measured");
                    }

                    Assert.NotEqual(CapabilityExpectation.Dead, option.Battery);
                }
            }
        }
    }

    // A keyboard's percent exists only because of the SDP cache patch, so the
    // prediction is the patch's state, not the driver's: Windows' own keyboard
    // driver is identical either way. Unpatched is written Unknown rather than
    // Dead on purpose - the keyboard is not broken, the patch is simply not
    // there yet, and the row that offers to apply it ("Fix battery reads") is
    // the place that says so.
    [Fact]
    public void A_keyboard_without_the_sdp_patch_does_not_predict_a_battery()
    {
        foreach (var sdp in new SdpPatchState?[]
                 { null, SdpPatchState.NotApplied, SdpPatchState.Unknown })
        {
            var only = Assert.Single(
                DriverAdvisor.OptionsFor(DeviceKind.MagicKeyboard, KeyboardPid, sdp));

            Assert.True(
                only.Battery == CapabilityExpectation.Unknown,
                $"keyboard battery is {only.Battery} with patch state {Name(sdp)}");

            // Everything else about the keyboard row is unchanged: one choice,
            // recommended, and nothing rendered as a defect.
            Assert.Equal(DriverAdvisor.IdStockWindows, only.Id);
            Assert.True(only.Recommended);
            Assert.Equal(CapabilityExpectation.NotApplicable, only.Scroll);
            Assert.Equal(CapabilityExpectation.NotApplicable, only.Pointer);
        }
    }

    // No trackpad has ever been paired to this project's PC, and no patch state
    // is evidence about one, so there is no state in which a trackpad row may
    // claim its battery.
    [Theory]
    [InlineData(DeviceKind.MagicTrackpadV1, "030e")]
    [InlineData(DeviceKind.MagicTrackpadV2, "0265")]
    [InlineData(DeviceKind.MagicTrackpadV3, "0324")]
    public void A_trackpad_never_predicts_a_battery_in_any_patch_state(DeviceKind kind, string pid)
    {
        foreach (var sdp in AllPatchStates())
        {
            var only = Assert.Single(DriverAdvisor.OptionsFor(kind, pid, sdp));

            Assert.Equal(CapabilityExpectation.Unknown, only.Battery);
            // The pointer and the absent scroll driver are untouched by this:
            // only the battery cell was unevidenced.
            Assert.Equal(CapabilityExpectation.Works, only.Pointer);
            Assert.Equal(CapabilityExpectation.NotApplicable, only.Scroll);
            Assert.Equal(DriverAdvisor.IdStockWindows, only.Id);
        }
    }

    // The other half of honesty: what WAS measured still reads as a yes. A fix
    // that turns every battery cell Unknown would pass the tests above and
    // would be just as false - the 030D's 97%, the 2024 mouse on KMDF and the
    // patched keyboard are all on record.
    [Fact]
    public void The_measured_devices_keep_their_affirmative_battery_claim()
    {
        foreach (var sdp in AllPatchStates())
        {
            foreach (var option in DriverAdvisor.OptionsFor(DeviceKind.MagicMouseV1, "030d", sdp))
                Assert.Equal(CapabilityExpectation.Works, option.Battery);

            var kmdf = DriverAdvisor.OptionsFor(DeviceKind.MagicMouseV3, V3Pid, sdp)
                .Single(o => o.Id == DriverAdvisor.IdKmdf);
            Assert.Equal(CapabilityExpectation.Works, kmdf.Battery);
            Assert.Equal(CapabilityExpectation.Works, kmdf.Scroll);
        }

        var patchedKeyboard = Assert.Single(
            DriverAdvisor.OptionsFor(DeviceKind.MagicKeyboard, KeyboardPid, SdpPatchState.Applied));
        Assert.Equal(CapabilityExpectation.Works, patchedKeyboard.Battery);
    }

    // The patch state is a keyboard fact. It may not reach any other kind's
    // options, in either direction: a mouse row must be byte-identical with and
    // without one, exactly as it is today.
    [Fact]
    public void A_patch_state_changes_nothing_on_a_device_that_has_no_patch()
    {
        foreach (var (kind, pid) in EveryDevice())
        {
            if (kind == DeviceKind.MagicKeyboard)
                continue;

            var baseline = DriverAdvisor.OptionsFor(kind, pid);
            foreach (var sdp in AllPatchStates())
            {
                Assert.True(
                    baseline.SequenceEqual(DriverAdvisor.OptionsFor(kind, pid, sdp)),
                    $"{kind}/{pid} changed with patch state {Name(sdp)}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // What the user actually reads
    // -----------------------------------------------------------------------
    // The cells above only matter as rendered rows, and the renderer has two
    // vocabularies: "battery yes" / "battery unknown" in the dialog, "battery"
    // / "battery?" in the menu. An unmeasured battery must land on the second
    // of each pair, and the menu row must still fit its cap once it does.

    [Theory]
    [InlineData(DeviceKind.MagicTrackpadV1, "030e")]
    [InlineData(DeviceKind.MagicTrackpadV2, "0265")]
    [InlineData(DeviceKind.MagicTrackpadV3, "0324")]
    public void A_trackpad_row_reads_as_an_unknown_battery_and_never_as_a_yes(
        DeviceKind kind, string pid)
    {
        foreach (var sdp in AllPatchStates())
        {
            var full = Assert.Single(DriverAdviceView.OptionRows(kind, pid, sdp));
            Assert.Contains("battery unknown", full);
            Assert.DoesNotContain("battery yes", full);
            Assert.DoesNotContain("battery no", full);

            var menu = Assert.Single(DriverAdviceView.OptionRowsShort(kind, pid, sdp));
            Assert.Contains("battery?", menu);
            Assert.DoesNotContain("no battery", menu);
            // The short vocabulary has no overlap with the observed rows above
            // it in the menu, so "unknown" is spelled "?" here and nowhere else.
            Assert.DoesNotContain("unknown", menu, StringComparison.OrdinalIgnoreCase);
            Assert.True(menu.Length <= DriverAdviceView.OptionRowShortMax,
                $"{menu.Length} chars, cap {DriverAdviceView.OptionRowShortMax}: {menu}");
        }
    }

    [Fact]
    public void A_keyboard_row_reads_as_a_battery_yes_only_once_the_patch_is_known_to_be_there()
    {
        foreach (var sdp in new SdpPatchState?[]
                 { null, SdpPatchState.NotApplied, SdpPatchState.Unknown })
        {
            var full = Assert.Single(
                DriverAdviceView.OptionRows(DeviceKind.MagicKeyboard, KeyboardPid, sdp));
            Assert.Contains("battery unknown", full);
            Assert.DoesNotContain("battery yes", full);
            Assert.DoesNotContain("battery no", full);

            var menu = Assert.Single(
                DriverAdviceView.OptionRowsShort(DeviceKind.MagicKeyboard, KeyboardPid, sdp));
            Assert.Equal("Stock Windows: battery? (recommended)", menu);
            Assert.True(menu.Length <= DriverAdviceView.OptionRowShortMax,
                $"{menu.Length} chars, cap {DriverAdviceView.OptionRowShortMax}: {menu}");
        }

        var applied = Assert.Single(DriverAdviceView.OptionRows(
            DeviceKind.MagicKeyboard, KeyboardPid, SdpPatchState.Applied));
        Assert.Contains("battery yes", applied);
        Assert.Equal(
            "Stock Windows: battery (recommended)",
            Assert.Single(DriverAdviceView.OptionRowsShort(
                DeviceKind.MagicKeyboard, KeyboardPid, SdpPatchState.Applied)));
    }

    // The default overloads are what the shipped menu and dialog call today
    // (TrayApp.cs:177 and :2120 pass no patch state at all), so the default has
    // to be the honest one: no patch state read means no affirmative claim.
    [Theory]
    [InlineData(DeviceKind.MagicKeyboard, "0320")]
    [InlineData(DeviceKind.MagicKeyboard, KeyboardPid)]
    [InlineData(DeviceKind.MagicTrackpadV1, "030e")]
    [InlineData(DeviceKind.MagicTrackpadV2, "0265")]
    [InlineData(DeviceKind.MagicTrackpadV3, "0324")]
    public void With_no_patch_state_read_the_rendered_row_makes_no_battery_claim(
        DeviceKind kind, string pid)
    {
        Assert.DoesNotContain(
            "battery yes", Assert.Single(DriverAdviceView.OptionRows(kind, pid)));
        Assert.Contains(
            "battery?", Assert.Single(DriverAdviceView.OptionRowsShort(kind, pid)));
    }

    static string Name(SdpPatchState? sdp) => sdp?.ToString() ?? "(no patch state read)";

    static IEnumerable<SdpPatchState?> AllPatchStates()
    {
        yield return null;
        foreach (var s in Enum.GetValues<SdpPatchState>())
            yield return s;
    }

    static IEnumerable<(DeviceKind Kind, string? Pid)> EveryDevice()
    {
        yield return (DeviceKind.MagicMouseV3, V3Pid);
        yield return (DeviceKind.MagicMouseV1, "030d");
        yield return (DeviceKind.MagicMouseV2, "0269");
        yield return (DeviceKind.MagicMouseV1, "0310");
        yield return (DeviceKind.MagicMouseV1, null);
        yield return (DeviceKind.MagicKeyboard, KeyboardPid);
        yield return (DeviceKind.MagicKeyboard, "0320");
        yield return (DeviceKind.MagicTrackpadV1, "030e");
        yield return (DeviceKind.MagicTrackpadV2, "0265");
        yield return (DeviceKind.MagicTrackpadV3, "0324");
        yield return (DeviceKind.LogitechMouse, "c52b");
    }
}
