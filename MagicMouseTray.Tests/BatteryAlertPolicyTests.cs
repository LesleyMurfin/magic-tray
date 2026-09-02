// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class BatteryAlertPolicyTests
{
    static readonly DateTime Morning = new(2026, 9, 1, 10, 0, 0);
    static readonly DateTime Evening = new(2026, 9, 1, 21, 0, 0);

    static BatteryAlertDecision Eval(
        DeviceKind kind,
        int pct,
        double hours,
        bool rateKnown,
        int threshold = 10,
        DateTime? now = null,
        int lastGood = int.MinValue,
        string name = "device",
        string[]? alreadyFired = null)
    {
        var fired = new HashSet<string>(alreadyFired ?? Array.Empty<string>());
        return BatteryAlertPolicy.Evaluate(
            kind, name, pct, threshold, hours, rateKnown,
            now ?? Morning, lastGood, fired);
    }

    [Theory]
    [InlineData(DeviceKind.MagicKeyboard, "Magic Keyboard")]
    [InlineData(DeviceKind.MagicMouseV1, "Magic Mouse")]
    public void Aa_ToastAt48h_EvenAt50Percent(DeviceKind kind, string name)
    {
        var d = Eval(kind, pct: 50, hours: 48, rateKnown: true, name: name);
        Assert.Equal(BatteryAlertAction.Toast, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventTwoDay, d.EventId);
        Assert.Contains("2 days", d.Body);
        Assert.Contains("Buy AA batteries", d.Body);
        Assert.DoesNotContain("USB-C", d.Body);
        Assert.DoesNotContain("charge", d.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TimeWins_At50Percent_WhenHoursInWindow()
    {
        var kb = Eval(DeviceKind.MagicKeyboard, pct: 50, hours: 24, rateKnown: true, name: "Magic Keyboard");
        var v3 = Eval(DeviceKind.MagicMouseV3, pct: 50, hours: 10, rateKnown: true, name: "Magic Mouse 2024");
        Assert.Equal(BatteryAlertAction.Toast, kb.Action);
        Assert.Equal(BatteryAlertPolicy.EventTwoDay, kb.EventId);
        Assert.Equal(BatteryAlertAction.Toast, v3.Action);
        Assert.Equal(BatteryAlertPolicy.EventNightBefore, v3.EventId);
    }

    [Fact]
    public void ThresholdIgnored_ToastAt50WithThreshold10()
    {
        var d = Eval(DeviceKind.MagicKeyboard, pct: 50, hours: 48, rateKnown: true,
            threshold: 10, name: "Magic Keyboard");
        Assert.Equal(BatteryAlertAction.Toast, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventTwoDay, d.EventId);
    }

    [Fact]
    public void NoTwoDayToast_WhenHours72()
    {
        var aboveFloor = Eval(DeviceKind.MagicKeyboard, pct: 15, hours: 72, rateKnown: true, name: "Magic Keyboard");
        var mid = Eval(DeviceKind.MagicKeyboard, pct: 50, hours: 72, rateKnown: true, name: "Magic Keyboard");
        Assert.Equal(BatteryAlertAction.None, aboveFloor.Action);
        Assert.Null(aboveFloor.EventId);
        Assert.Equal(BatteryAlertAction.None, mid.Action);
        Assert.Null(mid.EventId);
    }

    [Fact]
    public void DeathModal_At0Percent()
    {
        var d = Eval(DeviceKind.MagicKeyboard, pct: 0, hours: 0, rateKnown: true, name: "Magic Keyboard");
        Assert.Equal(BatteryAlertAction.Modal, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventDeath, d.EventId);
        Assert.Contains("Replace", d.Body);
        Assert.DoesNotContain("USB-C", d.Body);
        Assert.DoesNotContain("charge", d.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeathModal_At1Percent_Connected()
    {
        var d = Eval(DeviceKind.MagicKeyboard, pct: 1, hours: 10, rateKnown: true, name: "Magic Keyboard");
        Assert.Equal(BatteryAlertAction.Modal, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventDeath, d.EventId);
        Assert.Contains("Replace", d.Body);
    }

    [Fact]
    public void DeathModal_OnDisconnectAfterLastGood1()
    {
        var d = Eval(DeviceKind.MagicMouseV1, pct: -1, hours: -1, rateKnown: false,
            lastGood: 1, name: "Magic Mouse");
        Assert.Equal(BatteryAlertAction.Modal, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventDeath, d.EventId);
        Assert.False(d.CloseModal);
        Assert.Contains("Replace", d.Body);
    }

    [Fact]
    public void NoDeath_OnDisconnectAfterLastGood8()
    {
        var d = Eval(DeviceKind.MagicMouseV1, pct: -1, hours: -1, rateKnown: false,
            lastGood: 8, name: "Magic Mouse");
        Assert.Equal(BatteryAlertAction.None, d.Action);
        Assert.False(d.CloseModal);
        Assert.NotEqual(BatteryAlertPolicy.EventDeath, d.EventId);
    }

    [Fact]
    public void V3_ToastAt24h_EvenAt50Percent()
    {
        var d = Eval(DeviceKind.MagicMouseV3, pct: 50, hours: 24, rateKnown: true, name: "Magic Mouse 2024");
        Assert.Equal(BatteryAlertAction.Toast, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventNightBefore, d.EventId);
        Assert.Contains("USB-C", d.Body);
        Assert.Contains("24h", d.Body);
        Assert.DoesNotContain("Replace", d.Body);
        Assert.DoesNotContain("charge", d.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoV3Toast_WhenHoursOver24_AbovePercentThreshold()
    {
        var d = Eval(DeviceKind.MagicMouseV3, pct: 15, hours: 25, rateKnown: true, name: "Magic Mouse 2024");
        Assert.Equal(BatteryAlertAction.None, d.Action);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void V3_Connected0or1_PlugUsbC_Modal(int pct)
    {
        var d = Eval(DeviceKind.MagicMouseV3, pct: pct, hours: 1, rateKnown: true, name: "Magic Mouse 2024");
        Assert.Equal(BatteryAlertAction.Modal, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventDeath, d.EventId);
        Assert.Contains("USB-C", d.Body);
        Assert.Contains($"{pct}%", d.Body);
        Assert.DoesNotContain("Replace", d.Body);
    }

    [Fact]
    public void V3Disconnect_DoesNotDeathModal()
    {
        var d = Eval(DeviceKind.MagicMouseV3, pct: -1, hours: -1, rateKnown: false,
            lastGood: 8, name: "Magic Mouse 2024");
        Assert.Equal(BatteryAlertAction.None, d.Action);
        Assert.True(d.CloseModal);
        Assert.NotEqual(BatteryAlertPolicy.EventDeath, d.EventId);
    }

    [Fact]
    public void NoEveningToast_AfterNightBefore()
    {
        var name = "Magic Mouse 2024";
        var first = Eval(DeviceKind.MagicMouseV3, pct: 50, hours: 20, rateKnown: true,
            now: Evening, name: name, alreadyFired: new[] { BatteryAlertPolicy.EventNightBefore });
        Assert.Equal(BatteryAlertAction.None, first.Action);
        Assert.Null(first.EventId);
    }

    [Theory]
    [InlineData(DeviceKind.MagicKeyboard, "Magic Keyboard")]
    [InlineData(DeviceKind.MagicMouseV3, "Magic Mouse 2024")]
    public void RateUnknown_At10_ToastsPercent(DeviceKind kind, string name)
    {
        var d = Eval(kind, pct: 10, hours: -1, rateKnown: false, name: name);
        Assert.Equal(BatteryAlertAction.Toast, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventPercent, d.EventId);
        Assert.Contains("10%", d.Body);
        if (BatteryAlertPolicy.IsAaPowered(kind))
        {
            Assert.Contains("Replace batteries soon", d.Body);
            Assert.DoesNotContain("USB-C", d.Body);
        }
        else
        {
            Assert.Contains("Plug in USB-C soon", d.Body);
            Assert.DoesNotContain("Replace", d.Body);
        }
    }

    [Theory]
    [InlineData(DeviceKind.MagicKeyboard, "Magic Keyboard", 11)]
    [InlineData(DeviceKind.MagicMouseV3, "Magic Mouse 2024", 11)]
    [InlineData(DeviceKind.MagicKeyboard, "Magic Keyboard", 16)]
    [InlineData(DeviceKind.MagicKeyboard, "Magic Keyboard", 50)]
    [InlineData(DeviceKind.MagicMouseV3, "Magic Mouse 2024", 50)]
    public void RateUnknown_AboveThreshold_None(DeviceKind kind, string name, int pct)
    {
        var d = Eval(kind, pct: pct, hours: -1, rateKnown: false, name: name);
        Assert.Equal(BatteryAlertAction.None, d.Action);
        Assert.Null(d.EventId);
    }

    [Theory]
    [InlineData(DeviceKind.MagicKeyboard, "Magic Keyboard", true)]
    [InlineData(DeviceKind.MagicMouseV3, "Magic Mouse 2024", false)]
    public void RateUnknown_StillModal_AtConnected0or1(DeviceKind kind, string name, bool aa)
    {
        var d = Eval(kind, pct: 1, hours: -1, rateKnown: false, name: name);
        Assert.Equal(BatteryAlertAction.Modal, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventDeath, d.EventId);
        if (aa)
        {
            Assert.Contains("Replace", d.Body);
            Assert.DoesNotContain("USB-C", d.Body);
        }
        else
        {
            Assert.Contains("USB-C", d.Body);
            Assert.DoesNotContain("Replace", d.Body);
        }
    }

    [Fact]
    public void Copy_AaReplace_V3UsbC_NeverTheOther()
    {
        var aaToast = Eval(DeviceKind.MagicKeyboard, 50, 40, true, name: "Magic Keyboard");
        var aaDeath = Eval(DeviceKind.MagicKeyboard, 0, 0, true, name: "Magic Keyboard");
        var v3Toast = Eval(DeviceKind.MagicMouseV3, 50, 12, true, name: "Magic Mouse 2024");
        var v3Now = Eval(DeviceKind.MagicMouseV3, 1, 1, true, name: "Magic Mouse 2024");

        foreach (var d in new[] { aaToast, aaDeath })
        {
            Assert.DoesNotContain("USB-C", d.Body);
            Assert.DoesNotContain("charge", d.Body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Replace", aaDeath.Body);
        Assert.Contains("Buy AA batteries", aaToast.Body);

        foreach (var d in new[] { v3Toast, v3Now })
        {
            Assert.Contains("USB-C", d.Body);
            Assert.DoesNotContain("Replace", d.Body);
            Assert.DoesNotContain("charge", d.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MixedKeyboardAndV3_DiscoverOmitsOne_EvaluatesDisconnect()
    {
        var keyboard = "Magic Keyboard";
        var v3 = "Magic Mouse 2024";
        var both = new[] { keyboard, v3 };

        var v3Dropped = BatteryAlertPolicy.NamesOmittedFromDiscover(both, new[] { keyboard });
        Assert.Equal(new[] { v3 }, v3Dropped);
        var v3Eval = Eval(DeviceKind.MagicMouseV3, pct: -1, hours: -1, rateKnown: false,
            lastGood: 1, name: v3Dropped[0]);
        Assert.Equal(BatteryAlertAction.None, v3Eval.Action);
        Assert.True(v3Eval.CloseModal);
        Assert.NotEqual(BatteryAlertPolicy.EventDeath, v3Eval.EventId);
        Assert.True(BatteryAlertPolicy.ShouldCloseModal(v3Eval.CloseModal, v3, v3));
        Assert.False(BatteryAlertPolicy.ShouldCloseModal(v3Eval.CloseModal, keyboard, v3));

        var kbDropped = BatteryAlertPolicy.NamesOmittedFromDiscover(both, new[] { v3 });
        Assert.Equal(new[] { keyboard }, kbDropped);
        var kbEval = Eval(DeviceKind.MagicKeyboard, pct: -1, hours: -1, rateKnown: false,
            lastGood: 1, name: kbDropped[0]);
        Assert.Equal(BatteryAlertAction.Modal, kbEval.Action);
        Assert.Equal(BatteryAlertPolicy.EventDeath, kbEval.EventId);
        Assert.False(kbEval.CloseModal);
        Assert.Contains("Replace", kbEval.Body);
        Assert.False(BatteryAlertPolicy.ShouldCloseModal(kbEval.CloseModal, v3, keyboard));
        Assert.False(BatteryAlertPolicy.ShouldCloseModal(kbEval.CloseModal, keyboard, keyboard));
    }

    [Fact]
    public void ShouldCloseModal_OnlyWhenCriticalDeviceMatches()
    {
        Assert.False(BatteryAlertPolicy.ShouldCloseModal(true, null, "Magic Mouse 2024"));
        Assert.False(BatteryAlertPolicy.ShouldCloseModal(true, "Magic Mouse 2024", ""));
        Assert.False(BatteryAlertPolicy.ShouldCloseModal(false, "Magic Mouse 2024", "Magic Mouse 2024"));
        Assert.True(BatteryAlertPolicy.ShouldCloseModal(true, "Magic Mouse 2024", "magic mouse 2024"));
    }

    [Fact]
    public void TwoDay_DoesNotRepeat_WhenAlreadyFired()
    {
        var d = Eval(DeviceKind.MagicKeyboard, pct: 50, hours: 48, rateKnown: true,
            name: "Magic Keyboard", alreadyFired: new[] { BatteryAlertPolicy.EventTwoDay });
        Assert.Equal(BatteryAlertAction.None, d.Action);
    }

    [Fact]
    public void Aa_50Percent_40Hours_ToastsTwoDay()
    {
        var d = Eval(DeviceKind.MagicKeyboard, pct: 50, hours: 40, rateKnown: true,
            threshold: 10, name: "Magic Keyboard");
        Assert.Equal(BatteryAlertAction.Toast, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventTwoDay, d.EventId);
        Assert.Contains("2 days", d.Body);
        Assert.Contains("Buy AA batteries", d.Body);
    }

    [Fact]
    public void Aa_10Percent_40Hours_StillTwoDay()
    {
        var d = Eval(DeviceKind.MagicKeyboard, pct: 10, hours: 40, rateKnown: true,
            threshold: 10, name: "Magic Keyboard");
        Assert.Equal(BatteryAlertAction.Toast, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventTwoDay, d.EventId);
        Assert.Contains("2 days", d.Body);
        Assert.Contains("Buy AA batteries", d.Body);
    }

    [Fact]
    public void Aa_9Percent_8Days_ToastsPercent()
    {
        var d = Eval(DeviceKind.MagicKeyboard, pct: 9, hours: 192, rateKnown: true,
            threshold: 10, name: "Magic Keyboard");
        Assert.Equal(BatteryAlertAction.Toast, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventPercent, d.EventId);
        Assert.Contains("9%", d.Body);
        Assert.Contains("Replace batteries soon", d.Body);
        Assert.DoesNotContain("USB-C", d.Body);
    }

    [Fact]
    public void Rearm_TimeEvents_WhenHoursLeaveWindow()
    {
        var fired = new HashSet<string> { BatteryAlertPolicy.EventTwoDay };
        BatteryAlertPolicy.RearmFired(fired, DeviceKind.MagicKeyboard, pct: 50, hoursToEmpty: 72, rateKnown: true, threshold: 10);
        Assert.DoesNotContain(BatteryAlertPolicy.EventTwoDay, fired);
    }

    [Fact]
    public void Rearm_KeepsTwoDay_WhileHoursInWindow_EvenAbove10Percent()
    {
        var fired = new HashSet<string> { BatteryAlertPolicy.EventTwoDay };
        BatteryAlertPolicy.RearmFired(fired, DeviceKind.MagicKeyboard, pct: 50, hoursToEmpty: 40, rateKnown: true, threshold: 10);
        Assert.Contains(BatteryAlertPolicy.EventTwoDay, fired);
    }

    [Fact]
    public void Rearm_Death_WhenPctAbove1()
    {
        var fired = new HashSet<string> { BatteryAlertPolicy.EventDeath };
        BatteryAlertPolicy.RearmFired(fired, DeviceKind.MagicKeyboard, pct: 50, hoursToEmpty: 40, rateKnown: true, threshold: 10);
        Assert.DoesNotContain(BatteryAlertPolicy.EventDeath, fired);
    }

    [Fact]
    public void Rearm_DoesNotClearDeath_AtConnected1Percent()
    {
        var fired = new HashSet<string> { BatteryAlertPolicy.EventDeath };
        BatteryAlertPolicy.RearmFired(fired, DeviceKind.MagicKeyboard, pct: 1, hoursToEmpty: 10, rateKnown: true, threshold: 10);
        Assert.Contains(BatteryAlertPolicy.EventDeath, fired);
    }

    [Fact]
    public void Rearm_DoesNotClearDeath_OnDisconnect()
    {
        var fired = new HashSet<string> { BatteryAlertPolicy.EventDeath };
        BatteryAlertPolicy.RearmFired(fired, DeviceKind.MagicKeyboard, pct: -1, hoursToEmpty: -1, rateKnown: false, threshold: 10);
        Assert.Contains(BatteryAlertPolicy.EventDeath, fired);
        Assert.DoesNotContain(BatteryAlertPolicy.EventTwoDay, fired);
    }

    [Fact]
    public void Rearm_Percent_WhenPctAboveThreshold()
    {
        var fired = new HashSet<string> { BatteryAlertPolicy.EventPercent };
        BatteryAlertPolicy.RearmFired(fired, DeviceKind.MagicKeyboard, pct: 15, hoursToEmpty: -1, rateKnown: false, threshold: 10);
        Assert.DoesNotContain(BatteryAlertPolicy.EventPercent, fired);
    }

    [Fact]
    public void HoursInTimeWindow_Aa48_V3_24()
    {
        Assert.True(BatteryAlertPolicy.HoursInTimeWindow(DeviceKind.MagicKeyboard, 40, true));
        Assert.True(BatteryAlertPolicy.HoursInTimeWindow(DeviceKind.MagicKeyboard, 48, true));
        Assert.False(BatteryAlertPolicy.HoursInTimeWindow(DeviceKind.MagicKeyboard, 72, true));
        Assert.False(BatteryAlertPolicy.HoursInTimeWindow(DeviceKind.MagicKeyboard, 192, true));
        Assert.True(BatteryAlertPolicy.HoursInTimeWindow(DeviceKind.MagicMouseV3, 24, true));
        Assert.False(BatteryAlertPolicy.HoursInTimeWindow(DeviceKind.MagicMouseV3, 25, true));
        Assert.False(BatteryAlertPolicy.HoursInTimeWindow(DeviceKind.MagicKeyboard, 40, false));
    }

    [Fact]
    public void TrackpadV1_AaToastAt48h_EvenAt50Percent()
    {
        var d = Eval(DeviceKind.MagicTrackpadV1, pct: 50, hours: 48, rateKnown: true, name: "Magic Trackpad");
        Assert.Equal(BatteryAlertAction.Toast, d.Action);
        Assert.Equal(BatteryAlertPolicy.EventTwoDay, d.EventId);
        Assert.Equal("Trackpad battery low", d.Title);
        Assert.Contains("Buy AA batteries", d.Body);
        Assert.DoesNotContain("USB-C", d.Body);
        Assert.DoesNotContain("Lightning", d.Body);
        Assert.DoesNotContain("charge", d.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrackpadV2Lightning_AndV3UsbC_NightBeforeAt24h()
    {
        var v2 = Eval(DeviceKind.MagicTrackpadV2, pct: 50, hours: 24, rateKnown: true, name: "Magic Trackpad 2");
        Assert.Equal(BatteryAlertAction.Toast, v2.Action);
        Assert.Equal(BatteryAlertPolicy.EventNightBefore, v2.EventId);
        Assert.Contains("Lightning", v2.Body);
        Assert.DoesNotContain("USB-C", v2.Body);
        Assert.DoesNotContain("Replace", v2.Body);

        var v3 = Eval(DeviceKind.MagicTrackpadV3, pct: 50, hours: 24, rateKnown: true, name: "Magic Trackpad 2024");
        Assert.Equal(BatteryAlertAction.Toast, v3.Action);
        Assert.Equal(BatteryAlertPolicy.EventNightBefore, v3.EventId);
        Assert.Contains("USB-C", v3.Body);
        Assert.DoesNotContain("Lightning", v3.Body);
        Assert.DoesNotContain("Replace", v3.Body);
    }

}
