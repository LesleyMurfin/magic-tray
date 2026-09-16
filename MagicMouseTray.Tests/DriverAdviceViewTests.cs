// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// DriverAdviceView is pure text over DriverAdvisor: no WinForms, no registry,
// no clock. These tests cover the two ways this copy can lie - a missing or
// empty line, and a prediction a user could read as a reading of their own PC
// - plus the capability wordings the honesty rules pin down.
public class DriverAdviceViewTests
{
    const string V3Pid = "0323";

    // The advice line is the one line that is always in the Driver submenu, so
    // there is no state in which either form may be blank, and none in which
    // either may leave out what this app recommends.
    [Fact]
    public void Advice_speaks_in_every_driver_state()
    {
        foreach (var status in AllStatuses())
        {
            var full = DriverAdviceView.AdviceFull(DeviceKind.MagicMouseV3, V3Pid, status);
            var menu = DriverAdviceView.AdviceShort(DeviceKind.MagicMouseV3, V3Pid, status);

            Assert.False(string.IsNullOrWhiteSpace(full), $"empty advice paragraph for {status}");
            Assert.False(string.IsNullOrWhiteSpace(menu), $"empty advice line for {status}");
            Assert.Contains(TrayMenu.V3RadioKmdf, full);
            Assert.Contains(TrayMenu.V3RadioKmdf, menu);
        }
    }

    // A device this app binds no driver for gets an honest absence, not silence
    // and not an invented recommendation.
    [Fact]
    public void A_device_with_no_driver_story_is_told_what_is_unknown()
    {
        var row = DriverAdviceView.AdviceFull(DeviceKind.LogitechMouse, "c52b", DriverStatus.Ok);

        Assert.False(string.IsNullOrWhiteSpace(row));
        Assert.Contains("unknown", row);
        Assert.False(DriverAdviceView.ShowOptions(DeviceKind.LogitechMouse, "c52b"));
        Assert.Empty(DriverAdviceView.OptionRows(DeviceKind.LogitechMouse, "c52b"));
    }

    // One row per choice, in the advisor's own order so a caller can pair them
    // with the radios by index, and exactly one row marked recommended.
    [Fact]
    public void Option_rows_track_the_advisor_choices_one_for_one()
    {
        var options = DriverAdvisor.OptionsFor(DeviceKind.MagicMouseV3, V3Pid);
        var rows = DriverAdviceView.OptionRows(DeviceKind.MagicMouseV3, V3Pid);

        Assert.Equal(options.Count, rows.Count);
        for (var i = 0; i < rows.Count; i++)
            Assert.StartsWith(options[i].Label, rows[i]);

        Assert.Equal(1, rows.Count(r => r.Contains("(recommended)")));
        Assert.Contains("(recommended)", rows.Single(r => r.StartsWith(TrayMenu.V3RadioKmdf, StringComparison.Ordinal)));
    }

    // The hazard this view exists to avoid: the device row above already shows
    // DeviceCapability's readings, which lead with "Pointer:" / "Scroll:" /
    // "Battery:" and judge in "working" terms. A prediction that borrowed
    // either would read as a second opinion about the user's own PC.
    [Fact]
    public void Predicted_rows_cannot_be_read_as_readings_of_this_pc()
    {
        foreach (var (kind, pid) in EveryDevice())
        {
            foreach (var row in DriverAdviceView.OptionRows(kind, pid))
            {
                Assert.Contains("would give", row);
                Assert.DoesNotContain("working", row, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Pointer:", row);
                Assert.DoesNotContain("Scroll:", row);
                Assert.DoesNotContain("Battery:", row);
            }
        }

        Assert.Contains("prediction", DriverAdviceView.OptionsHeader());
    }

    // README.md:166 "Wheel and battery are mutually exclusive - one or the
    // other, never both." The row must say that as one exclusive choice, never
    // as two capabilities that both simply work.
    [Fact]
    public void Patched_apple_row_states_the_exclusivity()
    {
        var rows = DriverAdviceView.OptionRows(DeviceKind.MagicMouseV3, V3Pid);
        var patched = rows.Single(r => r.StartsWith(TrayMenu.V3RadioPatchedApple, StringComparison.Ordinal));

        Assert.Contains("scroll or battery", patched);
        Assert.Contains("never both", patched);
        Assert.DoesNotContain("scroll yes", patched);
        Assert.DoesNotContain("battery yes", patched);
        Assert.DoesNotContain("(recommended)", patched);
    }

    // A keyboard has no wheel and this app ships no trackpad scroll driver.
    // Neither may be rendered as a dead capability: nagging a healthy PC is the
    // failure this wording exists to avoid.
    [Theory]
    [InlineData(DeviceKind.MagicKeyboard, "0320")]
    [InlineData(DeviceKind.MagicTrackpadV1, "030e")]
    [InlineData(DeviceKind.MagicTrackpadV2, "0265")]
    [InlineData(DeviceKind.MagicTrackpadV3, "0324")]
    public void Keyboard_and_trackpad_get_one_stock_row_that_is_not_broken(DeviceKind kind, string pid)
    {
        var row = Assert.Single(DriverAdviceView.OptionRows(kind, pid));

        Assert.StartsWith(TrayMenu.V3RadioStockWindows, row);
        Assert.Contains("not apply", row);
        Assert.Contains("battery yes", row);
        Assert.DoesNotContain("scroll no", row);
        Assert.DoesNotContain("not working", row);
        Assert.DoesNotContain("dead", row);
    }

    // No shipped option carries Unknown today, but the renderer must stay total
    // and must not turn an absent expectation into a verdict either way.
    [Fact]
    public void Unknown_reads_as_unknown_and_never_as_a_failure()
    {
        var row = DriverAdviceView.OptionSummary(new DriverOption(
            "test-unknown", "Some driver",
            Pointer: CapabilityExpectation.Works,
            Scroll: CapabilityExpectation.Unknown,
            Battery: CapabilityExpectation.Unknown,
            Recommended: false,
            Why: "unused"));

        Assert.Contains("scroll unknown", row);
        Assert.Contains("battery unknown", row);
        Assert.DoesNotContain("scroll no", row);
        Assert.DoesNotContain("battery no", row);
    }

    // Totality at the edges: an option with nothing applicable still produces
    // one non-empty, non-accusing sentence, and a lone either-or capability
    // does not invent a rival to exclude it.
    [Fact]
    public void Degenerate_expectations_still_produce_one_honest_sentence()
    {
        var nothingApplies = DriverAdviceView.OptionSummary(new DriverOption(
            "test-na", "Nothing driver",
            CapabilityExpectation.NotApplicable,
            CapabilityExpectation.NotApplicable,
            CapabilityExpectation.NotApplicable,
            false, "unused"));

        Assert.False(string.IsNullOrWhiteSpace(nothingApplies));
        Assert.Contains("do not apply to this device", nothingApplies);
        Assert.DoesNotContain("pointer no", nothingApplies);
        Assert.DoesNotContain("scroll no", nothingApplies);
        Assert.DoesNotContain("battery no", nothingApplies);

        var loneEitherOr = DriverAdviceView.OptionSummary(new DriverOption(
            "test-lone", "Lone driver",
            CapabilityExpectation.Works,
            CapabilityExpectation.EitherOrOnly,
            CapabilityExpectation.Works,
            false, "unused"));

        Assert.Contains("scroll in one mode only", loneEitherOr);
        Assert.DoesNotContain("never both", loneEitherOr);
    }

    // The short forms are what the menu actually shows, so they must still
    // answer the two questions the paragraph answered: what this device is on,
    // and what to be on instead. A cap kept by dropping the recommendation
    // would be a cap bought with a lie.
    [Fact]
    public void Advice_short_names_the_state_and_the_recommendation()
    {
        Assert.Equal("On the recommended driver (KMDF)",
            DriverAdviceView.AdviceShort(DeviceKind.MagicMouseV3, V3Pid, DriverStatus.PatchedKmdf));
        Assert.Equal("On Patched Apple - KMDF is recommended",
            DriverAdviceView.AdviceShort(DeviceKind.MagicMouseV3, V3Pid, DriverStatus.PathAPatched));
        Assert.Equal("On Stock Windows - KMDF is recommended",
            DriverAdviceView.AdviceShort(DeviceKind.MagicMouseV3, V3Pid, DriverStatus.StockKmdf));
        Assert.Equal("Driver not read yet - KMDF is recommended",
            DriverAdviceView.AdviceShort(DeviceKind.MagicMouseV3, V3Pid, null));
        Assert.Equal("On the recommended driver (Boot Camp)",
            DriverAdviceView.AdviceShort(DeviceKind.MagicMouseV1, "030d", DriverStatus.Ok));
        Assert.Equal("On Stock Windows - Boot Camp is recommended",
            DriverAdviceView.AdviceShort(DeviceKind.MagicMouseV1, "030d", DriverStatus.NotInstalled));

        // No driver story at all: an absence of knowledge, never a fault and
        // never an invented recommendation.
        Assert.Equal("No driver advice for this device",
            DriverAdviceView.AdviceShort(DeviceKind.LogitechMouse, "c52b", DriverStatus.Ok));

        // Nothing is dropped to fit: wherever this app has a recommendation,
        // every state still carries it.
        foreach (var (kind, pid) in EveryDevice())
        {
            if (DriverAdvisor.RecommendedFor(kind, pid) is null)
                continue;
            foreach (var status in AllStatuses())
            {
                var line = DriverAdviceView.AdviceShort(kind, pid, status);
                Assert.Contains("recommended", line, StringComparison.Ordinal);
            }
        }
    }

    // -----------------------------------------------------------------------
    // The read stock driver at menu width
    // -----------------------------------------------------------------------
    // "Driver not read yet" was the keyboard row's permanent state, because
    // DriverHealthChecker never reads these kinds at all. Once the driver HAS
    // been read, the menu line names it - and the cap still holds.

    // Live keyboard, reference PC 2026-09-16, PID 0239 (leaf stack names:
    // StockDriverReader strips the "\Driver\" prefix).
    static StockDriverInfo LiveKeyboard(bool allNodesOk = true) => new(
        Service: "kbdhid",
        InfPath: "keyboard.inf",
        Provider: "Microsoft",
        Version: "10.0.26100.8972",
        Stack: ["kbdclass", "kbdhid", "HidBth"],
        AllNodesOk: allNodesOk);

    // INFERENCE: no trackpad has been paired to this project's PC. A shape to
    // drive the wording, not a capture.
    static StockDriverInfo TrackpadShape(bool allNodesOk = true) => new(
        Service: "mouhid",
        InfPath: "msmouse.inf",
        Provider: "Microsoft",
        Version: "10.0.26100.8972",
        Stack: ["mouclass", "mouhid", "HidBth"],
        AllNodesOk: allNodesOk);

    [Fact]
    public void Advice_short_names_the_read_driver_instead_of_saying_not_read_yet()
    {
        Assert.Equal("Windows' own keyboard driver - working; SDP patch applied",
            DriverAdviceView.AdviceShort(
                DeviceKind.MagicKeyboard, "0239", null, LiveKeyboard(), SdpPatchState.Applied));
        Assert.Equal("Windows' own keyboard driver - working; battery needs patch",
            DriverAdviceView.AdviceShort(
                DeviceKind.MagicKeyboard, "0239", null, LiveKeyboard(), SdpPatchState.NotApplied));
        // A check that did not happen is not a missing patch.
        Assert.Equal("Windows' own keyboard driver - working; patch not checked",
            DriverAdviceView.AdviceShort(
                DeviceKind.MagicKeyboard, "0239", null, LiveKeyboard(), SdpPatchState.Unknown));
        Assert.Equal("Windows' own keyboard driver - working; patch not checked",
            DriverAdviceView.AdviceShort(DeviceKind.MagicKeyboard, "0239", null, LiveKeyboard(), null));
        // Unconfirmed nodes: missing evidence, said as such, and the battery
        // tail gives way to it rather than the cap being blown.
        Assert.Equal("Windows' own keyboard driver - health not confirmed",
            DriverAdviceView.AdviceShort(
                DeviceKind.MagicKeyboard, "0239", null, LiveKeyboard(allNodesOk: false),
                SdpPatchState.Applied));
        // Trackpads: HID, no patch, whatever patch state is handed in.
        Assert.Equal("Windows' own driver - working; battery over HID",
            DriverAdviceView.AdviceShort(
                DeviceKind.MagicTrackpadV2, "0265", null, TrackpadShape(), SdpPatchState.NotApplied));

        // Every status, with the driver read, stops saying it was not read.
        foreach (var status in AllStatuses())
        {
            var menu = DriverAdviceView.AdviceShort(
                DeviceKind.MagicKeyboard, "0239", status, LiveKeyboard(), SdpPatchState.Applied);
            var full = DriverAdviceView.AdviceFull(
                DeviceKind.MagicKeyboard, "0239", status, LiveKeyboard(), SdpPatchState.Applied);

            Assert.DoesNotContain("not read yet", menu);
            Assert.DoesNotContain("has not been read yet", full);
            Assert.Contains("kbdhid", full);
        }
    }

    // The pair NonMouseWire wires the row with: both null wherever there is no
    // read driver to describe, which is its fallback switch to the
    // status-based line.
    [Fact]
    public void The_stock_advice_pair_is_null_for_mice_and_for_unread_drivers()
    {
        Assert.Null(DriverAdviceView.StockAdviceShort(
            DeviceKind.MagicMouseV3, V3Pid, LiveKeyboard(), SdpPatchState.Applied));
        Assert.Null(DriverAdviceView.StockAdviceFull(
            DeviceKind.MagicMouseV3, V3Pid, LiveKeyboard(), SdpPatchState.Applied));
        Assert.Null(DriverAdviceView.StockAdviceShort(
            DeviceKind.MagicKeyboard, "0239", null, SdpPatchState.Applied));
        Assert.Null(DriverAdviceView.StockAdviceFull(
            DeviceKind.MagicKeyboard, "0239", null, SdpPatchState.Applied));

        // And the full twin is the advisor's prose verbatim, so the dialog and
        // this file cannot drift.
        Assert.Equal(
            DriverAdvisor.StockDriverLine(
                DeviceKind.MagicKeyboard, "0239", LiveKeyboard(), SdpPatchState.Applied),
            DriverAdviceView.StockAdviceFull(
                DeviceKind.MagicKeyboard, "0239", LiveKeyboard(), SdpPatchState.Applied));
    }

    // Mice keep the old behaviour exactly: this slice only speaks for the kinds
    // the health checker skips.
    [Fact]
    public void Mouse_menu_lines_are_unchanged_when_a_stock_reading_is_passed()
    {
        foreach (var (kind, pid) in EveryDevice())
        {
            if (kind is DeviceKind.MagicKeyboard or DeviceKind.MagicTrackpadV1
                     or DeviceKind.MagicTrackpadV2 or DeviceKind.MagicTrackpadV3)
                continue;
            foreach (var status in AllStatuses())
            {
                Assert.Equal(
                    DriverAdviceView.AdviceShort(kind, pid, status),
                    DriverAdviceView.AdviceShort(
                        kind, pid, status, LiveKeyboard(), SdpPatchState.Applied));
                Assert.Equal(
                    DriverAdviceView.AdviceFull(kind, pid, status),
                    DriverAdviceView.AdviceFull(
                        kind, pid, status, LiveKeyboard(), SdpPatchState.Applied));
            }
        }
    }

    // The cap is the contract on the new axis too: every kind x PID x status x
    // patch state x reading, with the widest wordings this file can produce.
    [Fact]
    public void Every_stock_driver_menu_string_fits_its_cap_on_one_line()
    {
        foreach (var kind in Enum.GetValues<DeviceKind>())
        {
            foreach (var pid in EveryPid())
            {
                foreach (var info in EveryReading())
                {
                    foreach (var sdp in AllPatchStates())
                    {
                        var menu = DriverAdviceView.AdviceShort(kind, pid, null, info, sdp);
                        AssertFits(menu, DriverAdviceView.AdviceShortMax);
                        AssertSpeaksInAscii(menu);
                        AssertSpeaksInAscii(DriverAdviceView.AdviceFull(kind, pid, null, info, sdp));

                        if (DriverAdviceView.StockAdviceShort(kind, pid, info, sdp) is string read)
                        {
                            AssertFits(read, DriverAdviceView.AdviceShortMax);
                            Assert.Equal(read, menu);
                            Assert.DoesNotContain("not read yet", read);
                        }
                    }
                }
            }
        }
    }

    static IEnumerable<SdpPatchState?> AllPatchStates()
    {
        yield return null;
        foreach (var s in Enum.GetValues<SdpPatchState>())
            yield return s;
    }

    static IEnumerable<StockDriverInfo?> EveryReading()
    {
        yield return null;
        yield return new StockDriverInfo(null, null, null, null, [], false);
        yield return LiveKeyboard();
        yield return LiveKeyboard(allNodesOk: false);
        yield return TrackpadShape();
        yield return TrackpadShape(allNodesOk: false);
    }

    // Same hazard as the full rows, one line narrower: the short rows sit under
    // DeviceCapability's readings, so nothing in them may be readable as a
    // verdict on this PC. They are too narrow for "would give", so the
    // separation rests on word order and on a vocabulary with no overlap at all
    // - including "unknown", which belongs to the observed rows and is written
    // as a trailing "?" here.
    [Fact]
    public void Option_rows_short_cannot_be_read_as_readings_of_this_pc()
    {
        foreach (var (kind, pid) in EveryDevice())
        {
            var full = DriverAdviceView.OptionRows(kind, pid);
            var menu = DriverAdviceView.OptionRowsShort(kind, pid);

            Assert.Equal(full.Count, menu.Count);
            foreach (var row in menu)
            {
                Assert.DoesNotContain("working", row, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unknown", row, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Pointer:", row);
                Assert.DoesNotContain("Scroll:", row);
                Assert.DoesNotContain("Battery:", row);
            }
        }

        var v3 = DriverAdviceView.OptionRowsShort(DeviceKind.MagicMouseV3, V3Pid);
        Assert.Equal("KMDF: pointer, scroll, battery (recommended)", v3[0]);
        Assert.Equal("Patched Apple: pointer, one of scroll or battery", v3[1]);
        Assert.Equal("Stock Windows: pointer, battery, no scroll", v3[2]);

        // v1's battery is measured (Feature 0x47, 97% on the reference PC);
        // v2's has never been read, and says so with a question mark rather
        // than by vanishing from the row or by reading as "no battery".
        Assert.Equal("Boot Camp: pointer, scroll, battery (recommended)",
            DriverAdviceView.OptionRowsShort(DeviceKind.MagicMouseV1, "030d")[0]);
        var v2 = DriverAdviceView.OptionRowsShort(DeviceKind.MagicMouseV2, "0269");
        Assert.Equal("Boot Camp: pointer, scroll, battery? (recommended)", v2[0]);
        Assert.DoesNotContain(v2, r => r.Contains("no battery", StringComparison.Ordinal));

        // A keyboard has no wheel and no pointer of its own: absent from the
        // short row, never rendered as a dead capability.
        Assert.Equal("Stock Windows: battery (recommended)",
            Assert.Single(DriverAdviceView.OptionRowsShort(DeviceKind.MagicKeyboard, "0320")));
    }

    // Menu width is a contract, not a preference: a ToolStripItem is as wide as
    // its text, and the paragraphs this view used to hand the menu rendered as
    // one line the full width of the screen, over the top of everything else.
    // Driven over every kind x PID x status, so a wording that grows past its
    // cap fails here rather than in a screenshot.
    [Fact]
    public void Every_menu_string_fits_its_cap_on_one_line()
    {
        AssertFits(DriverAdviceView.OptionsHeader(), DriverAdviceView.OptionsHeaderMax);

        foreach (var kind in Enum.GetValues<DeviceKind>())
        {
            foreach (var pid in EveryPid())
            {
                foreach (var status in AllStatuses())
                    AssertFits(DriverAdviceView.AdviceShort(kind, pid, status),
                        DriverAdviceView.AdviceShortMax);
                foreach (var row in DriverAdviceView.OptionRowsShort(kind, pid))
                    AssertFits(row, DriverAdviceView.OptionRowShortMax);
                foreach (var option in DriverAdvisor.OptionsFor(kind, pid))
                    AssertFits(DriverAdviceView.OptionSummaryShort(option),
                        DriverAdviceView.OptionRowShortMax);
            }
        }
    }

    static void AssertFits(string s, int cap)
    {
        Assert.False(string.IsNullOrWhiteSpace(s), "empty menu string");
        Assert.DoesNotContain('\n', s);
        Assert.DoesNotContain('\r', s);
        Assert.True(s.Length <= cap, $"{s.Length} chars, cap {cap}: {s}");
    }

    // This repo has been bitten by mojibake in tray strings.
    [Fact]
    public void All_view_text_is_plain_ascii_and_never_empty()
    {
        AssertSpeaksInAscii(DriverAdviceView.OptionsHeader());

        foreach (var kind in Enum.GetValues<DeviceKind>())
        {
            foreach (var pid in EveryPid())
            {
                foreach (var row in DriverAdviceView.OptionRows(kind, pid))
                    AssertSpeaksInAscii(row);
                foreach (var row in DriverAdviceView.OptionRowsShort(kind, pid))
                    AssertSpeaksInAscii(row);
                foreach (var option in DriverAdvisor.OptionsFor(kind, pid))
                {
                    AssertSpeaksInAscii(DriverAdviceView.OptionSummary(option));
                    AssertSpeaksInAscii(DriverAdviceView.OptionSummaryShort(option));
                }
                foreach (var status in AllStatuses())
                {
                    AssertSpeaksInAscii(DriverAdviceView.AdviceFull(kind, pid, status));
                    AssertSpeaksInAscii(DriverAdviceView.AdviceShort(kind, pid, status));
                }
            }
        }
    }

    static void AssertSpeaksInAscii(string s)
    {
        Assert.False(string.IsNullOrWhiteSpace(s), "empty menu string");
        foreach (var c in s)
            Assert.True(c >= 0x20 && c < 0x7F, $"non-ascii U+{(int)c:X4} in: {s}");
    }

    static IEnumerable<DriverStatus?> AllStatuses()
    {
        yield return null;
        foreach (var s in Enum.GetValues<DriverStatus>())
            yield return s;
    }

    static IEnumerable<string?> EveryPid()
    {
        yield return null;
        yield return "";
        yield return V3Pid;
        yield return "030d";
        yield return "0269";
        yield return "0310";
        yield return "0320";
        yield return "030e";
        yield return "0265";
        yield return "0324";
        yield return "c52b";
        yield return "ZZZZ";
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
}
