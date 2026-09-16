// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DriverAdvisorTests
{
    const string V3Pid = "0323";

    [Fact]
    public void Magic_mouse_2024_offers_three_choices_with_kmdf_recommended()
    {
        var options = DriverAdvisor.OptionsFor(DeviceKind.MagicMouseV3, V3Pid);

        Assert.Equal(
            new[] { DriverAdvisor.IdKmdf, DriverAdvisor.IdPatchedApple, DriverAdvisor.IdStockWindows },
            options.Select(o => o.Id).ToArray());

        var rec = DriverAdvisor.RecommendedFor(DeviceKind.MagicMouseV3, V3Pid);
        Assert.NotNull(rec);
        Assert.Equal(DriverAdvisor.IdKmdf, rec!.Id);
        Assert.Equal(TrayMenu.V3RadioKmdf, rec.Label);
        // README.md:263 "scroll and battery (Input 0x90 on COL02)".
        Assert.Equal(CapabilityExpectation.Works, rec.Pointer);
        Assert.Equal(CapabilityExpectation.Works, rec.Scroll);
        Assert.Equal(CapabilityExpectation.Works, rec.Battery);
        Assert.Single(options, o => o.Recommended);
    }

    [Fact]
    public void Patched_apple_predicts_scroll_or_battery_never_both()
    {
        var patched = DriverAdvisor.OptionsFor(DeviceKind.MagicMouseV3, V3Pid)
            .Single(o => o.Id == DriverAdvisor.IdPatchedApple);

        Assert.Equal(CapabilityExpectation.EitherOrOnly, patched.Scroll);
        Assert.Equal(CapabilityExpectation.EitherOrOnly, patched.Battery);
        Assert.Equal(CapabilityExpectation.Works, patched.Pointer);
        Assert.False(patched.Recommended);
    }

    [Fact]
    public void Stock_windows_on_2024_mouse_predicts_a_dead_wheel_and_a_live_battery()
    {
        var stock = DriverAdvisor.OptionsFor(DeviceKind.MagicMouseV3, V3Pid)
            .Single(o => o.Id == DriverAdvisor.IdStockWindows);

        Assert.Equal(CapabilityExpectation.Dead, stock.Scroll);
        Assert.Equal(CapabilityExpectation.Works, stock.Battery);
        Assert.Equal(CapabilityExpectation.Works, stock.Pointer);
    }

    // A 0323 that arrives with some other DeviceKind must still get the v3
    // story, the way TrayMenu.IsV3 is used everywhere else in the app.
    [Fact]
    public void A_0323_arriving_as_another_kind_still_gets_the_v3_choices()
    {
        var options = DriverAdvisor.OptionsFor(DeviceKind.MagicMouseV1, V3Pid);

        Assert.Equal(3, options.Count);
        Assert.Equal(DriverAdvisor.IdKmdf,
            DriverAdvisor.RecommendedFor(DeviceKind.MagicMouseV1, V3Pid)!.Id);
    }

    [Theory]
    [InlineData(DeviceKind.MagicMouseV1, "030d")]
    [InlineData(DeviceKind.MagicMouseV2, "0269")]
    public void Older_mice_recommend_boot_camp_and_are_never_offered_kmdf(DeviceKind kind, string pid)
    {
        var options = DriverAdvisor.OptionsFor(kind, pid);

        Assert.Equal(
            new[] { DriverAdvisor.IdBootCamp, DriverAdvisor.IdStockWindows },
            options.Select(o => o.Id).ToArray());
        Assert.DoesNotContain(DriverAdvisor.IdKmdf, options.Select(o => o.Id));

        var rec = DriverAdvisor.RecommendedFor(kind, pid);
        Assert.Equal(DriverAdvisor.IdBootCamp, rec!.Id);
        Assert.Equal(TrayMenu.V1V2RadioBootCamp, rec.Label);
        Assert.Equal(CapabilityExpectation.Works, rec.Scroll);
        // Battery differs per model now - see the two tests below.
    }

    // Driver repo issue #22 asked whether the battery percent can be read on
    // the pre-0323 mice at all, since the COL02 vendor collection this app
    // reads input report 0x90 on does not exist there. It is now MEASURED for
    // the v1, and the answer arrived through a different channel than the
    // question assumed: the v1's single collection-less HID node declares the
    // battery as feature report 0x47 (usage page 0x0006 usage 0x0020), and a
    // paired v1 returned 97 percent through it -
    // "MOUSE_BATTERY_OK device=Magic Mouse v1 pct=97% (unified Feature 0x47)"
    // with "REPAIR_SNAPSHOT pid=030d ... bound=applewirelessmouse svc=running
    // ... batt=97" on the same tick. So Boot Camp - the bound state that was
    // measured - may claim Works.
    [Fact]
    public void A_v1_on_boot_camp_claims_the_battery_it_was_measured_reading()
    {
        var bootCamp = DriverAdvisor.OptionsFor(DeviceKind.MagicMouseV1, "030d")
            .Single(o => o.Id == DriverAdvisor.IdBootCamp);

        Assert.Equal(CapabilityExpectation.Works, bootCamp.Battery);
        Assert.Equal(CapabilityExpectation.Works, bootCamp.Scroll);
        Assert.True(bootCamp.Recommended);
        // The claim is sourced in the copy, not asserted bare.
        Assert.Contains("Feature 0x47", bootCamp.Why);
    }

    // The battery read is this app's own HID read (Feature 0x47 on the v1's one
    // unified collection), not something the scroll filter provides, so losing
    // the filter loses the WHEEL and nothing else. The stock row on a v1 must
    // therefore keep Battery = Works while Scroll goes Dead: a driver choice
    // that does not touch a capability must not be shown as costing it.
    [Fact]
    public void A_v1_on_stock_windows_keeps_its_battery_and_loses_only_the_wheel()
    {
        var stock = DriverAdvisor.OptionsFor(DeviceKind.MagicMouseV1, "030d")
            .Single(o => o.Id == DriverAdvisor.IdStockWindows);

        Assert.Equal(CapabilityExpectation.Works, stock.Battery);
        Assert.Equal(CapabilityExpectation.Dead, stock.Scroll);
        Assert.Equal(CapabilityExpectation.Works, stock.Pointer);

        // And the advice line for that state says exactly that, rather than
        // hedging a capability the app reads itself.
        var line = DriverAdvisor.AdviceLine(
            DeviceKind.MagicMouseV1, "030d", DriverStatus.NotInstalled)!;
        Assert.Contains("the pointer and the battery percent work", line);
        Assert.Contains("the wheel stays dead", line);
    }

    // Nothing but the 030D has been measured, so nothing but the 030D may claim
    // a battery - and the claim is keyed on the PID, not on the DeviceKind
    // (#137). 0310 matters here twice over: it is the Apple Wireless Mouse, a
    // device this project has never seen, and discovery hands it over as
    // DeviceKind.MagicMouseV1 on purpose (its battery chemistry is the v1's AA
    // cells). Before this fix that shared kind pulled the 030D's measured
    // Works cell and its "measured on a paired v1" sentence onto a mouse nobody
    // has ever read a percent from. The 030D's Feature 0x47 result is a
    // different report descriptor on a different mouse and may not be copied
    // across - that copy is exactly the guess these rows exist to block.
    [Theory]
    [InlineData(DeviceKind.MagicMouseV2, "0269")]
    [InlineData(DeviceKind.MagicMouseV1, "0310")]
    [InlineData(DeviceKind.MagicMouseV2, "0310")]
    // No PID at all is not the measured model either: it is no evidence.
    [InlineData(DeviceKind.MagicMouseV1, null)]
    public void Only_the_measured_030d_claims_a_battery(DeviceKind kind, string? pid)
    {
        Assert.False(DriverAdvisor.HasMeasuredBattery(pid));
        var options = DriverAdvisor.OptionsFor(kind, pid);
        Assert.Equal(2, options.Count);

        foreach (var option in options)
        {
            Assert.Equal(CapabilityExpectation.Unknown, option.Battery);
            Assert.NotEqual(CapabilityExpectation.Works, option.Battery);
            // Unknown means "cannot be predicted", never "broken".
            Assert.NotEqual(CapabilityExpectation.Dead, option.Battery);
            Assert.Contains("not confirmed", option.Why);
            Assert.DoesNotContain("0x47", option.Why);
            // The 030D's measurement wording may not appear on an unmeasured
            // model, in any of its forms.
            Assert.DoesNotContain("measured on a paired", option.Why);
            Assert.DoesNotContain("reads too", option.Why);
        }

        // The unknown is about the battery only: Boot Camp is still recommended
        // for SCROLL, and stock Windows still predicts a dead wheel.
        var rec = DriverAdvisor.RecommendedFor(kind, pid)!;
        Assert.Equal(DriverAdvisor.IdBootCamp, rec.Id);
        Assert.Equal(CapabilityExpectation.Works, rec.Scroll);
        Assert.Equal(
            CapabilityExpectation.Dead,
            options.Single(o => o.Id == DriverAdvisor.IdStockWindows).Scroll);
    }

    // The measured cell is the 030D's and travels with the PID, so a 030D keeps
    // it whichever kind it arrives under, and it is the ONLY PID that has it.
    [Fact]
    public void The_measured_battery_belongs_to_the_030d_pid_and_to_no_other()
    {
        Assert.True(DriverAdvisor.HasMeasuredBattery("030d"));
        Assert.True(DriverAdvisor.HasMeasuredBattery("030D"));
        foreach (var other in new string?[] { null, "", "0310", "0269", "0323", "030e", "ZZZZ" })
            Assert.False(DriverAdvisor.HasMeasuredBattery(other));

        foreach (var kind in new[] { DeviceKind.MagicMouseV1, DeviceKind.MagicMouseV2 })
        {
            foreach (var option in DriverAdvisor.OptionsFor(kind, "030d"))
                Assert.Equal(CapabilityExpectation.Works, option.Battery);
        }

        // And the citation names the model that was actually on the desk.
        var why = DriverAdvisor.RecommendedFor(DeviceKind.MagicMouseV1, "030d")!.Why;
        Assert.Contains("measured on a paired Magic Mouse v1 (030D)", why);
    }

    // "Boot Camp" covers two different Apple things - the DRIVER and the
    // INSTALLER - and only the driver works on a PC that is not a Mac. The
    // driver repo (sbagirici/apple-magic-mouse-scroll-fix-windows) lists that
    // as the third of its three problems: "Boot Camp installer checks for Mac
    // hardware" -> "Skip the installer entirely - install only the driver". A
    // Why that read as "run Apple's Boot Camp installer" would send the user at
    // the one step that provably cannot work on their PC, so the copy has to
    // deny it outright rather than merely leave it out.
    [Theory]
    [InlineData(DeviceKind.MagicMouseV1, "030d")]
    [InlineData(DeviceKind.MagicMouseV2, "0269")]
    public void The_older_mouse_recommendation_never_sends_a_user_to_the_boot_camp_installer(
        DeviceKind kind, string pid)
    {
        var why = DriverAdvisor.RecommendedFor(kind, pid)!.Why;

        Assert.Contains("do not run Apple's Boot Camp installer", why);
        Assert.Contains("refuses to run on a PC that is not a Mac", why);

        foreach (var instruction in new[]
                 {
                     "run the Boot Camp installer", "Run the Boot Camp installer",
                     "install Boot Camp", "Install Boot Camp",
                     "using the Boot Camp installer", "with the Boot Camp installer",
                 })
        {
            Assert.DoesNotContain(instruction, why);
        }

        // The other half of the confusion: it is Apple's own binary, still
        // carrying Microsoft's countersignature, so nothing about this PC's
        // security posture has to be weakened to load it.
        Assert.Contains("countersigned", why);
        Assert.Contains("Test Mode is not required", why);
        Assert.Contains("no security setting has to change", why);
    }

    // Both ways of installing just the driver end with the same Apple binary
    // bound as a lower filter - the repo's bundled driver is byte-identical to
    // the reference PC's installed file (SHA256 08F33D7E3ECE2C73..., 78,424
    // bytes), and oem8.inf's "HKR,,LowerFilters" in a .NT.HW section writes the
    // same instance key install.ps1 sets by hand - so the copy must name both
    // and rank neither. It must also not overstate this app, which for v1/v2
    // only opens a download page.
    [Theory]
    [InlineData(DeviceKind.MagicMouseV1, "030d")]
    [InlineData(DeviceKind.MagicMouseV2, "0269")]
    public void Both_install_routes_are_named_as_equals_and_the_app_claims_only_the_download_page(
        DeviceKind kind, string pid)
    {
        var why = DriverAdvisor.RecommendedFor(kind, pid)!.Why;

        Assert.Contains("INF package", why);
        Assert.Contains("registers the driver directly", why);
        Assert.Contains("the result is the same either way", why);
        Assert.Contains("only opens the download page", why);
        Assert.Contains("installs nothing itself", why);

        // Registry and filter-stack mechanics stay in the source comments.
        Assert.DoesNotContain("LowerFilters", why);
        Assert.DoesNotContain("lower filter", why);
        Assert.DoesNotContain("applewirelessmouse", why);
    }

    // The 0323 Apple-driver route binds Apple's own filter under the
    // applewirelessmouse service name, and driver repo PR #19 (docs/two-drivers)
    // records that TWO different binaries can be bound under that one name: the
    // recommended unmodified, Microsoft-countersigned one, which needs no Test
    // Mode, and the legacy re-signed CN=MagicMouseFix patch, which does. So a
    // Test Mode requirement is a property of the FILE, and this option - which
    // cannot see the file - must not assert it either way. The tray answers it
    // from SystemConfigChecker's BoundDriverSelfSigned instead.
    [Fact]
    public void The_apple_driver_option_does_not_claim_a_test_mode_requirement()
    {
        var patched = DriverAdvisor.OptionsFor(DeviceKind.MagicMouseV3, V3Pid)
            .Single(o => o.Id == DriverAdvisor.IdPatchedApple);

        foreach (var claim in new[]
                 {
                     "needs Test Mode", "need Test Mode", "needs test mode",
                     "requires Test Mode", "Test Mode is required",
                     "Test Mode too", "No Test Mode", "Test Mode is not required",
                 })
        {
            Assert.DoesNotContain(claim, patched.Why);
        }

        // It says what IS true of the route, and defers the signing question to
        // the evidence rather than dropping it.
        Assert.Contains("Apple's own filter driver", patched.Why);
        Assert.Contains("LowerFilters", patched.Why);
        Assert.Contains("depends on which binary is bound", patched.Why);
        Assert.Contains("signatures", patched.Why);
    }

    // The clause that must stop being mouse-only: a keyboard or a trackpad has
    // no vendor scroll filter, so its scroll expectation is NotApplicable and
    // must never read as Dead.
    [Theory]
    [InlineData(DeviceKind.MagicKeyboard, "0320")]
    [InlineData(DeviceKind.MagicTrackpadV1, "030e")]
    [InlineData(DeviceKind.MagicTrackpadV2, "0265")]
    [InlineData(DeviceKind.MagicTrackpadV3, "0324")]
    public void Keyboards_and_trackpads_get_one_choice_with_scroll_not_applicable(
        DeviceKind kind, string pid)
    {
        var options = DriverAdvisor.OptionsFor(kind, pid);

        var only = Assert.Single(options);
        Assert.Equal(DriverAdvisor.IdStockWindows, only.Id);
        Assert.True(only.Recommended);
        Assert.Equal(CapabilityExpectation.NotApplicable, only.Scroll);
        Assert.NotEqual(CapabilityExpectation.Dead, only.Scroll);
        Assert.NotEqual(CapabilityExpectation.Dead, only.Battery);
        Assert.NotEqual(CapabilityExpectation.Dead, only.Pointer);

        Assert.Equal(only.Id, DriverAdvisor.RecommendedFor(kind, pid)!.Id);
        Assert.False(string.IsNullOrWhiteSpace(DriverAdvisor.AdviceLine(kind, pid, DriverStatus.Ok)));
    }

    [Fact]
    public void A_keyboard_has_no_pointer_of_its_own_but_a_trackpad_does()
    {
        Assert.Equal(
            CapabilityExpectation.NotApplicable,
            DriverAdvisor.OptionsFor(DeviceKind.MagicKeyboard, "0320").Single().Pointer);
        Assert.Equal(
            CapabilityExpectation.Works,
            DriverAdvisor.OptionsFor(DeviceKind.MagicTrackpadV2, "0265").Single().Pointer);
    }

    // The bug this exists to kill: silence once the device is bound. Every
    // DriverStatus member, plus no status at all, must produce a usable line
    // that names the recommended driver, so a future enum member cannot fall
    // through to null. Every family with a driver story is covered, not just
    // the v3 mouse: the v1/v2 rows changed when their battery cell became
    // Unknown, and StayingCost reads the capability cells, so a cell edit can
    // silence a whole family's advice.
    [Theory]
    [InlineData(DeviceKind.MagicMouseV3, "0323", TrayMenu.V3RadioKmdf)]
    [InlineData(DeviceKind.MagicMouseV1, "030d", TrayMenu.V1V2RadioBootCamp)]
    [InlineData(DeviceKind.MagicMouseV2, "0269", TrayMenu.V1V2RadioBootCamp)]
    [InlineData(DeviceKind.MagicKeyboard, "0320", TrayMenu.V3RadioStockWindows)]
    [InlineData(DeviceKind.MagicTrackpadV2, "0265", TrayMenu.V3RadioStockWindows)]
    public void Every_driver_status_yields_an_advice_line_naming_the_recommendation(
        DeviceKind kind, string pid, string recommendedLabel)
    {
        foreach (var status in AllStatuses())
        {
            var line = DriverAdvisor.AdviceLine(kind, pid, status);

            Assert.False(string.IsNullOrWhiteSpace(line), $"null advice for {Name(status)}");
            Assert.Contains(recommendedLabel, line!);
            Assert.EndsWith(".", line!);
        }
    }

    [Fact]
    public void Being_on_the_recommended_driver_is_stated_affirmatively()
    {
        var line = DriverAdvisor.AdviceLine(DeviceKind.MagicMouseV3, V3Pid, DriverStatus.PatchedKmdf)!;

        Assert.Contains("You are on the recommended driver", line);
        Assert.DoesNotContain("Recommended:", line);
        Assert.True(DriverAdvisor.IsOnRecommended(DeviceKind.MagicMouseV3, V3Pid, DriverStatus.PatchedKmdf));
    }

    [Fact]
    public void Patched_apple_advice_names_kmdf_and_the_cost_of_staying()
    {
        var line = DriverAdvisor.AdviceLine(DeviceKind.MagicMouseV3, V3Pid, DriverStatus.PathAPatched)!;

        Assert.Contains(TrayMenu.V3RadioPatchedApple, line);
        Assert.Contains("never both", line);
        Assert.Contains($"Recommended: {TrayMenu.V3RadioKmdf}", line);
    }

    [Fact]
    public void Stock_windows_advice_names_kmdf_and_the_dead_wheel()
    {
        var line = DriverAdvisor.AdviceLine(DeviceKind.MagicMouseV3, V3Pid, DriverStatus.StockKmdf)!;

        Assert.Contains(TrayMenu.V3RadioStockWindows, line);
        Assert.Contains("wheel", line);
        Assert.Contains($"Recommended: {TrayMenu.V3RadioKmdf}", line);
    }

    [Fact]
    public void An_older_mouse_on_stock_windows_is_pointed_at_boot_camp()
    {
        var line = DriverAdvisor.AdviceLine(DeviceKind.MagicMouseV2, "0269", DriverStatus.NotInstalled)!;

        Assert.Contains(TrayMenu.V1V2RadioStockWindows, line);
        Assert.Contains($"Recommended: {TrayMenu.V1V2RadioBootCamp}", line);
        Assert.True(DriverAdvisor.IsOnRecommended(DeviceKind.MagicMouseV2, "0269", DriverStatus.Ok));
    }

    // Unreadable or unmatched state must never be reported as "you are set up
    // correctly", and must say what is unknown instead of predicting.
    [Theory]
    [InlineData(null)]
    [InlineData(DriverStatus.Error)]
    [InlineData(DriverStatus.UnknownAppleMouse)]
    [InlineData(DriverStatus.Ok)] // v1/v2-family status on a v3 row: unmatched
    public void Unreadable_status_is_never_reported_as_on_the_recommended_driver(DriverStatus? status)
    {
        Assert.False(DriverAdvisor.IsOnRecommended(DeviceKind.MagicMouseV3, V3Pid, status));

        var line = DriverAdvisor.AdviceLine(DeviceKind.MagicMouseV3, V3Pid, status)!;
        Assert.Contains("cannot be confirmed", line);
        Assert.DoesNotContain("You are on the recommended driver", line);
        Assert.DoesNotContain("This device is on", line);
    }

    [Fact]
    public void Nothing_bound_says_the_wheel_is_dead_rather_than_claiming_a_driver()
    {
        var line = DriverAdvisor.AdviceLine(DeviceKind.MagicMouseV3, V3Pid, DriverStatus.NotBound)!;

        Assert.Contains("wheel is dead", line);
        Assert.False(DriverAdvisor.IsOnRecommended(DeviceKind.MagicMouseV3, V3Pid, DriverStatus.NotBound));
    }

    // A Logitech row is battery-only and this app binds no driver for it, so
    // inventing advice for it would be a guess.
    [Fact]
    public void A_device_this_app_has_no_driver_story_for_gets_no_advice()
    {
        Assert.Empty(DriverAdvisor.OptionsFor(DeviceKind.LogitechMouse, "c52b"));
        Assert.Null(DriverAdvisor.RecommendedFor(DeviceKind.LogitechMouse, "c52b"));
        Assert.Null(DriverAdvisor.AdviceLine(DeviceKind.LogitechMouse, "c52b", DriverStatus.Ok));
        Assert.False(DriverAdvisor.IsOnRecommended(DeviceKind.LogitechMouse, "c52b", DriverStatus.Ok));
    }

    // This repo has been bitten by mojibake in tray strings.
    [Fact]
    public void All_advice_text_is_plain_ascii()
    {
        foreach (var (kind, pid) in EveryDevice())
        {
            foreach (var o in DriverAdvisor.OptionsFor(kind, pid))
                AssertAscii(o.Label + " " + o.Why);

            foreach (var status in AllStatuses())
                AssertAscii(DriverAdvisor.AdviceLine(kind, pid, status) ?? "");
        }
    }

    // -----------------------------------------------------------------------
    // The read stock driver: keyboards and trackpads
    // -----------------------------------------------------------------------
    // DriverHealthChecker never looks at these kinds (skipNonScroll: true,
    // DriverHealthChecker.cs:406, gate at :494-498), so their DriverStatus is
    // null or a cross-family leftover forever - and the advice used to say
    // "The driver bound to this device has not been read yet". On a Magic
    // Keyboard that is false: the driver is readable, and it is Microsoft's
    // own end to end. These tests pin the affirmative line and the three
    // honest battery states.

    // The live keyboard on the reference PC, 2026-09-16, PID 0239, every node
    // Status OK: Service kbdhid, keyboard.inf, provider Microsoft, version
    // 10.0.26100.8972, stack \Driver\kbdclass, \Driver\kbdhid, \Driver\HidBth.
    // StockDriverReader strips the "\Driver\" prefix, so leaf names arrive.
    static StockDriverInfo LiveKeyboard(bool allNodesOk = true) => new(
        Service: "kbdhid",
        InfPath: "keyboard.inf",
        Provider: "Microsoft",
        Version: "10.0.26100.8972",
        Stack: ["kbdclass", "kbdhid", "HidBth"],
        AllNodesOk: allNodesOk);

    // INFERENCE, not a capture: no trackpad has ever been paired to this
    // project's PC. This is the SHAPE a stock Bluetooth pointer node reads as,
    // used only to drive the wording - no test below asserts these particular
    // values are what a real trackpad reports.
    static StockDriverInfo TrackpadShape(bool allNodesOk = true) => new(
        Service: "mouhid",
        InfPath: "msmouse.inf",
        Provider: "Microsoft",
        Version: "10.0.26100.8972",
        Stack: ["mouclass", "mouhid", "HidBth"],
        AllNodesOk: allNodesOk);

    [Fact]
    public void A_keyboard_with_a_read_driver_is_told_what_it_runs_and_never_not_read_yet()
    {
        foreach (var status in AllStatuses())
        {
            foreach (var sdp in AllPatchStates())
            {
                var line = DriverAdvisor.AdviceLine(
                    DeviceKind.MagicKeyboard, "0239", status, LiveKeyboard(), sdp)!;

                // Plain words first, then the facts that back them.
                Assert.Contains("Windows' own keyboard driver", line);
                Assert.Contains("the kbdhid service from keyboard.inf", line);
                Assert.Contains("provider Microsoft", line);
                Assert.Contains("version 10.0.26100.8972", line);
                Assert.Contains("driver stack kbdclass, kbdhid, HidBth", line);

                // Working, and nothing to install - the two things the row
                // never said.
                Assert.Contains("so it is working", line);
                Assert.Contains("nothing for you to install", line);

                // The complained-about sentence, and its siblings, are gone.
                Assert.DoesNotContain("has not been read yet", line);
                Assert.DoesNotContain("cannot be confirmed", line);
                Assert.DoesNotContain("could not be read", line);

                // A keyboard's absent scroll driver is correct by design, so
                // the line must not raise it at all.
                Assert.DoesNotContain("scroll", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("wheel", line, StringComparison.OrdinalIgnoreCase);

                Assert.Contains(TrayMenu.V3RadioStockWindows, line);
                Assert.EndsWith(".", line);
            }
        }
    }

    // Three patch states, three different sentences, and none of them may
    // overstate what was read.
    [Fact]
    public void Each_patch_state_gives_the_keyboard_battery_its_own_honest_sentence()
    {
        var applied = Battery(SdpPatchState.Applied);
        var notApplied = Battery(SdpPatchState.NotApplied);
        var unknown = Battery(SdpPatchState.Unknown);

        Assert.Equal(3, new[] { applied, notApplied, unknown }.Distinct().Count());

        // All three agree on the mechanism: a registry patch, not a driver.
        foreach (var line in new[] { applied, notApplied, unknown })
        {
            Assert.Contains("does not come from a driver", line);
            Assert.Contains("one-time patch of the Bluetooth SDP cache", line);
        }

        // Applied: said affirmatively, with the one real non-broken state
        // named (patch in CachedServices, HID still -2 until the radio cycles
        // - the patch script's own closing instruction) and no re-run advised.
        Assert.Contains("applied for this keyboard right now", applied);
        Assert.Contains("lets the percent read", applied);
        Assert.Contains("turn Bluetooth off and back on", applied);
        Assert.Contains("do not run the patch again", applied);
        Assert.DoesNotContain("Fix battery reads", applied);

        // NotApplied: says what is missing and points at the offer that
        // already exists on the row (TrayApp.cs:1493-1496).
        Assert.Contains("absent for this keyboard right now", notApplied);
        Assert.Contains("cannot read until it is there", notApplied);
        Assert.Contains("\"Fix battery reads\" item", notApplied);
        Assert.Contains("one administrator approval", notApplied);

        // A state that was never read is not a state: null reads as Unknown.
        Assert.Equal(unknown, Battery(null));
    }

    // The honesty rule this whole slice turns on: Unknown is no evidence, and
    // no evidence is never a fault. It may not read as "the patch is missing",
    // and it may not send the user at the elevated offer on a guess.
    [Fact]
    public void An_unchecked_patch_is_never_reported_as_a_missing_patch()
    {
        var unknown = Battery(SdpPatchState.Unknown);

        Assert.Contains("could not be checked", unknown);
        Assert.Contains("not claiming either way", unknown);

        foreach (var claim in new[]
                 {
                     "absent", "not applied", "missing", "needs", "cannot read",
                     "Fix battery reads", "run the patch",
                 })
        {
            Assert.DoesNotContain(claim, unknown, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Trackpads do not use the SDP patch at all: 030E, 0265 and 0324 are in
    // MouseBatteryDevice.KnownMice, their percent comes off HID with nothing
    // installed, the patch script is keyboard-only and TrayApp.cs:229-230 gates
    // the offer on kind == MagicKeyboard. So no trackpad line may mention the
    // patch, whatever SdpPatchState is handed in.
    //
    // The line must also match the report channel the CODE takes (#134).
    // MouseBatteryDevice.GetBatteryPercent sends MagicMouseV3 / 0323 to
    // ReadV3Rid90 - the fixed Input 0x90 on COL02 - and every trackpad kind to
    // ReadV1V2Feature, which resolves the channel from the descriptor at read
    // time. The advisor used to claim the v3's fixed 0x90/COL02 channel for
    // trackpads, which is a report they never take, so the prose - the claim -
    // was corrected to the code and the copy may not name that fixed report.
    [Theory]
    [InlineData(DeviceKind.MagicTrackpadV1, "030e")]
    [InlineData(DeviceKind.MagicTrackpadV2, "0265")]
    [InlineData(DeviceKind.MagicTrackpadV3, "0324")]
    public void A_trackpad_reads_its_battery_off_hid_and_never_mentions_the_patch(
        DeviceKind kind, string pid)
    {
        foreach (var sdp in AllPatchStates())
        {
            var line = DriverAdvisor.AdviceLine(kind, pid, null, TrackpadShape(), sdp)!;

            Assert.Contains("Windows' own driver", line);
            Assert.Contains("straight off the trackpad over HID", line);
            Assert.Contains("nothing is installed for it", line);
            Assert.Contains("no registry patch is involved", line);

            // The channel the code actually resolves: descriptor-driven, one of
            // two reports, decided on the device - not the v3's fixed report.
            Assert.Contains("descriptor-driven path as the older mice", line);
            Assert.Contains("vendor input report if the node this app opens", line);
            Assert.Contains("otherwise the feature report", line);
            foreach (var v3Channel in new[] { "COL02", "0x90", "Input report 0x90" })
                Assert.DoesNotContain(v3Channel, line);

            // The inference is labelled in the copy, not only in a comment:
            // this app has never had a trackpad paired to it, so the whole cell
            // stays unverified - including which report it would answer on.
            Assert.Contains("no trackpad has been paired", line);
            Assert.Contains("has never been confirmed here", line);

            foreach (var patchWord in new[] { "SDP", "Fix battery reads", "administrator" })
                Assert.DoesNotContain(patchWord, line);

            Assert.DoesNotContain("has not been read yet", line);
            Assert.EndsWith(".", line);
        }
    }

    // AllNodesOk = false means NOT CONFIRMED, never broken (StockDriverReader
    // sets it true only for nodes positively observed present and started), so
    // it must not turn a healthy PC's row into a repair prompt.
    [Fact]
    public void Unconfirmed_nodes_are_missing_evidence_and_not_a_fault()
    {
        var line = DriverAdvisor.AdviceLine(
            DeviceKind.MagicKeyboard, "0239", null, LiveKeyboard(allNodesOk: false),
            SdpPatchState.Applied)!;

        Assert.Contains("Windows' own keyboard driver", line);
        Assert.Contains("the kbdhid service from keyboard.inf", line);
        Assert.Contains("missing evidence and not a fault", line);
        Assert.Contains("nothing for you to install", line);

        foreach (var fault in new[] { "broken", "not working", "failed", "reinstall" })
            Assert.DoesNotContain(fault, line, StringComparison.OrdinalIgnoreCase);
    }

    // Where there is no evidence, the old clause is TRUE and must stay: the
    // line this slice replaces is only wrong when the driver WAS read.
    [Fact]
    public void With_no_readable_driver_the_unknown_clause_stands()
    {
        var empty = new StockDriverInfo(null, null, null, null, [], AllNodesOk: false);

        // Nothing read at all, and a record that says nothing, are the same.
        Assert.Null(DriverAdvisor.StockDriverLine(
            DeviceKind.MagicKeyboard, "0239", null, SdpPatchState.Applied));
        Assert.Null(DriverAdvisor.StockDriverLine(
            DeviceKind.MagicKeyboard, "0239", empty, SdpPatchState.Applied));
        Assert.False(DriverAdvisor.HasStockDriverEvidence(DeviceKind.MagicKeyboard, "0239", empty));

        var line = DriverAdvisor.AdviceLine(DeviceKind.MagicKeyboard, "0239", null)!;
        Assert.Contains("has not been read yet", line);
        Assert.Contains(TrayMenu.V3RadioStockWindows, line);
    }

    // The mouse rows are a different story with a different reader, and this
    // slice may not touch them: a 0323 keeps the v3 story even if a stock
    // record is handed in, and every mouse line is identical with and without
    // one.
    [Fact]
    public void Mouse_rows_ignore_the_stock_driver_reading_entirely()
    {
        Assert.Null(DriverAdvisor.StockDriverLine(
            DeviceKind.MagicMouseV3, V3Pid, LiveKeyboard(), SdpPatchState.Applied));
        Assert.Null(DriverAdvisor.StockDriverLine(
            DeviceKind.MagicMouseV1, "030d", LiveKeyboard(), SdpPatchState.Applied));
        // A 0323 that arrived as a keyboard is still the v3 mouse, exactly as
        // OptionsFor treats it.
        Assert.Null(DriverAdvisor.StockDriverLine(
            DeviceKind.MagicKeyboard, V3Pid, LiveKeyboard(), SdpPatchState.Applied));

        foreach (var (kind, pid) in EveryDevice())
        {
            if (kind is DeviceKind.MagicKeyboard or DeviceKind.MagicTrackpadV1
                     or DeviceKind.MagicTrackpadV2 or DeviceKind.MagicTrackpadV3)
                continue;
            foreach (var status in AllStatuses())
            {
                Assert.Equal(
                    DriverAdvisor.AdviceLine(kind, pid, status),
                    DriverAdvisor.AdviceLine(kind, pid, status, LiveKeyboard(), SdpPatchState.Applied));
            }
        }
    }

    // Totality, now over one more axis: no kind x PID x status x patch state
    // may fall silent or stop naming the recommendation, whether or not a
    // driver was read.
    [Fact]
    public void Every_kind_status_and_patch_state_still_yields_one_ascii_sentence()
    {
        foreach (var kind in Enum.GetValues<DeviceKind>())
        {
            foreach (var (pid, info) in EveryReading())
            {
                foreach (var status in AllStatuses())
                {
                    foreach (var sdp in AllPatchStates())
                    {
                        var line = DriverAdvisor.AdviceLine(kind, pid, status, info, sdp);
                        if (DriverAdvisor.RecommendedFor(kind, pid) is null)
                        {
                            Assert.Null(line);
                            continue;
                        }

                        Assert.False(string.IsNullOrWhiteSpace(line),
                            $"null advice for {kind}/{pid}/{Name(status)}/{sdp}");
                        Assert.Contains(DriverAdvisor.RecommendedFor(kind, pid)!.Label, line!);
                        Assert.EndsWith(".", line!);
                        AssertAscii(line!);
                    }
                }
            }
        }
    }

    // Registry data is not authored copy: a provider or version value can hold
    // anything at all, and every string this app shows is plain ASCII on
    // purpose. Values are filtered, trimmed and clamped, and what is left empty
    // counts as not read rather than as a blank fact.
    [Fact]
    public void Registry_values_are_filtered_to_ascii_and_clamped()
    {
        var polluted = new StockDriverInfo(
            Service: "kbd\u00e9hid",
            InfPath: "  keyboard.inf\u201d ",
            Provider: "\u00ae",
            Version: new string('9', 200),
            Stack: ["\u0007kbdclass", "   ", "HidBth"],
            AllNodesOk: true);

        var line = DriverAdvisor.StockDriverLine(
            DeviceKind.MagicKeyboard, "0239", polluted, SdpPatchState.Unknown)!;

        AssertAscii(line);
        Assert.Contains("the kbdhid service from keyboard.inf", line);
        // A value that was nothing but non-ASCII is not read at all, so it is
        // not printed as an empty fact.
        Assert.DoesNotContain("provider ,", line);
        Assert.DoesNotContain("provider .", line);
        // Clamped, and the blank stack entry dropped.
        Assert.Contains("...", line);
        Assert.Contains("driver stack kbdclass, HidBth", line);
        Assert.True(line.Length < 1200, $"{line.Length} chars: {line}");
    }

    static string Battery(SdpPatchState? sdp) => DriverAdvisor.AdviceLine(
        DeviceKind.MagicKeyboard, "0239", null, LiveKeyboard(), sdp)!;

    static IEnumerable<SdpPatchState?> AllPatchStates()
    {
        yield return null;
        foreach (var s in Enum.GetValues<SdpPatchState>())
            yield return s;
    }

    // PID plus the driver reading that goes with it, including "nothing was
    // read" and a record that resolved nothing.
    static IEnumerable<(string? Pid, StockDriverInfo? Info)> EveryReading()
    {
        yield return (null, null);
        yield return ("", LiveKeyboard());
        yield return (V3Pid, LiveKeyboard());
        yield return ("030d", LiveKeyboard());
        yield return ("0239", null);
        yield return ("0239", LiveKeyboard());
        yield return ("0239", LiveKeyboard(allNodesOk: false));
        yield return ("0239", new StockDriverInfo(null, null, null, null, [], false));
        yield return ("0320", LiveKeyboard());
        yield return ("030e", TrackpadShape());
        yield return ("0265", TrackpadShape());
        yield return ("0324", TrackpadShape(allNodesOk: false));
        yield return ("c52b", LiveKeyboard());
        yield return ("ZZZZ", TrackpadShape());
    }

    static void AssertAscii(string s)
    {
        foreach (var c in s)
            Assert.True(c >= 0x20 && c < 0x7F, $"non-ascii U+{(int)c:X4} in: {s}");
    }

    static IEnumerable<DriverStatus?> AllStatuses()
    {
        yield return null;
        foreach (var s in Enum.GetValues<DriverStatus>())
            yield return s;
    }

    static IEnumerable<(DeviceKind Kind, string Pid)> EveryDevice()
    {
        yield return (DeviceKind.MagicMouseV3, V3Pid);
        yield return (DeviceKind.MagicMouseV1, "030d");
        yield return (DeviceKind.MagicMouseV2, "0269");
        yield return (DeviceKind.MagicKeyboard, "0320");
        yield return (DeviceKind.MagicTrackpadV1, "030e");
        yield return (DeviceKind.MagicTrackpadV2, "0265");
        yield return (DeviceKind.MagicTrackpadV3, "0324");
        yield return (DeviceKind.LogitechMouse, "c52b");
    }

    static string Name(DriverStatus? s) => s?.ToString() ?? "(no status read)";
}
