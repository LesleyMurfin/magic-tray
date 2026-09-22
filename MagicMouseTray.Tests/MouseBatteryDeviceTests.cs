// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class MouseBatteryDeviceTests
{
    [Fact]
    public void ParseRid90Percent_UsesBuf2_MatchesLiveHidProbe()
    {
        Assert.Equal(47, MouseBatteryDevice.ParseRid90Percent([0x90, 0x00, 47]));
        Assert.Equal(47, MouseBatteryDevice.ParseRid90Percent([0x90, 0x00, 47, 0x00, 0x00]));
        Assert.Equal(46, MouseBatteryDevice.ParseRid90Percent([0x90, 0x04, 0x2E])); // live mm-hid-probe
        Assert.Equal(43, MouseBatteryDevice.ParseRid90Percent([0x90, 0x04, 43]));   // live debug.log
        Assert.Null(MouseBatteryDevice.ParseRid90Percent([0x47, 60]));             // Feature 0x47 is not the mouse path
        Assert.Null(MouseBatteryDevice.ParseRid90Percent([0x90, 0x00]));
        Assert.Null(MouseBatteryDevice.ParseRid90Percent([0x90, 0x00, 101]));
        Assert.Null(MouseBatteryDevice.ParseRid90Percent([0x12, 0x00, 47]));
    }

    [Theory]
    [InlineData(@"\\?\bthhfenum#{0000111e-...}#7&iphone&hands-free")]
    [InlineData(@"\\?\BTHHFENUM\Dev_AABBCCDDEEFF")]
    [InlineData(@"\\?\hid#handsfree#1")]
    [InlineData(@"\\?\hid#vid_05ac&pid_12a8#iphone")]
    public void HandsFreeOrIphone_IsRejected(string path)
    {
        Assert.True(DeviceRegistry.IsHandsFreeOrIphonePath(path));
        Assert.Empty(DeviceRegistry.DiscoverFromPaths([path]));
    }

    [Fact]
    public void V3_OnlyCol02_IsBatteryCollection()
    {
        const string col02 = @"\\?\hid#vid_004c&pid_0323&col02#7&abc&0000#{4d1e55b2}";
        const string col01 = @"\\?\hid#vid_004c&pid_0323&col01#7&abc&0000#{4d1e55b2}";
        const string btCol02 = @"\\?\hid#{00001124-...}_vid&0001004c_pid&0323&col02#9&x";

        Assert.True(DeviceRegistry.Is0323BatteryCollectionPath(col02));
        Assert.True(DeviceRegistry.Is0323BatteryCollectionPath(btCol02));
        Assert.False(DeviceRegistry.Is0323BatteryCollectionPath(col01));
        Assert.False(DeviceRegistry.IsHandsFreeOrIphonePath(col02));
    }

    [Fact]
    public void Discover_0323_SkipsCol01AndHandsFree()
    {
        var paths = new[]
        {
            @"\\?\hid#vid_004c&pid_0323&col01#7&ptr#{4d1e55b2}",
            @"\\?\bthhfenum#iphone-hands-free",
            @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323&col02#9&batt",
        };
        var found = DeviceRegistry.DiscoverFromPaths(paths);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicMouseV3, found[0].Kind);
        Assert.Equal("0323", found[0].Pid);
        Assert.Equal("Magic Mouse 2024", found[0].DeviceName);
    }

    [Theory]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&0269#9&classic")]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0269#9&ble")]
    [InlineData(@"\\?\hid#vid_05ac&pid_0269#7&usb")]
    public void Discover_0269_IsMagicMouseV2_NotHandsFree(string path)
    {
        Assert.False(DeviceRegistry.IsHandsFreeOrIphonePath(path));
        var found = DeviceRegistry.DiscoverFromPaths(
            [path, @"\\?\bthhfenum#iphone-hands-free"]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicMouseV2, found[0].Kind);
        Assert.Equal("0269", found[0].Pid);
        Assert.Equal("Magic Mouse v2", found[0].DeviceName);
    }

    // 0310 is the Apple Wireless Mouse, NOT a Magic Mouse v1, and it has never
    // been paired to this project. Two things must therefore hold at once
    // (#137): the name the user reads is the real device, and the DeviceKind
    // stays MagicMouseV1 - not because the app thinks it is a v1, but because
    // Kind carries battery CHEMISTRY here (BatteryAlertPolicy treats
    // MagicMouseV1 as AA cells, which an Apple Wireless Mouse has, while
    // MagicMouseV2 is told to plug in a Lightning cable it does not have).
    [Theory]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&0310#9&bt")]
    [InlineData(@"\\?\hid#vid_05ac&pid_0310#7&usb")]
    public void Discover_0310_IsAppleWirelessMouse(string path)
    {
        var found = DeviceRegistry.DiscoverFromPaths([path]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicMouseV1, found[0].Kind);
        Assert.Equal("0310", found[0].Pid);
        Assert.Equal("Apple Wireless Mouse", found[0].DeviceName);

        // And the 030D measurement may not ride along on that shared kind: the
        // battery claim is keyed on the PID, so a 0310 discovered exactly as
        // the app discovers it predicts NOTHING about its battery.
        Assert.False(DriverAdvisor.HasMeasuredBattery(found[0].Pid));
        foreach (var option in DriverAdvisor.OptionsFor(found[0].Kind, found[0].Pid))
        {
            Assert.Equal(CapabilityExpectation.Unknown, option.Battery);
            Assert.DoesNotContain("measured on a paired", option.Why);
            Assert.DoesNotContain("0x47", option.Why);
        }
    }

    [Theory]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&030d#9&bt")]
    [InlineData(@"\\?\hid#vid_05ac&pid_030d#7&usb")]
    public void Discover_030D_IsMagicMouseV1(string path)
    {
        var found = DeviceRegistry.DiscoverFromPaths([path]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicMouseV1, found[0].Kind);
        Assert.Equal("030d", found[0].Pid);
        Assert.Equal("Magic Mouse v1", found[0].DeviceName);
    }

    [Theory]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&030e#9&bt")]
    [InlineData(@"\\?\hid#vid_05ac&pid_030e#7&usb")]
    public void Discover_030E_IsMagicTrackpadV1(string path)
    {
        var found = DeviceRegistry.DiscoverFromPaths([path]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicTrackpadV1, found[0].Kind);
        Assert.Equal("030e", found[0].Pid);
        Assert.Equal("Magic Trackpad", found[0].DeviceName);
    }

    [Fact]
    public void EveryKnownMousePid_HasUsbVid05acRow()
    {
        var pids = MouseBatteryDevice.KnownMice
            .Select(m => m.PidPattern[^4..].ToUpperInvariant())
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        var usb = MouseBatteryDevice.KnownMice
            .Where(m => m.VidPattern.Equals("VID_05AC", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.PidPattern[^4..].ToUpperInvariant())
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        Assert.Equal(pids, usb);
    }

    [Fact]
    public void Logitech_StaysGated_UnlessThirdPartyEnabled()
    {
        const string path = @"\\?\hid#vid_046d&pid_b023#7&direct";
        Assert.Empty(DeviceRegistry.DiscoverFromPaths([path]));
        var on = DeviceRegistry.DiscoverFromPaths([path], enableThirdParty: true);
        Assert.Single(on);
        Assert.Equal(DeviceKind.LogitechMouse, on[0].Kind);
    }

    [Theory]
    [InlineData("030e", "Magic Trackpad", DeviceKind.MagicTrackpadV1)]
    [InlineData("0265", "Magic Trackpad 2", DeviceKind.MagicTrackpadV2)]
    [InlineData("0324", "Magic Trackpad 2024", DeviceKind.MagicTrackpadV3)]
    public void TryKnownMouse_TrackpadPids(string pid, string expectedName, DeviceKind expectedKind)
    {
        Assert.True(MouseBatteryDevice.TryKnownMouse(pid, out var name, out var kind));
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedKind, kind);
    }

    // Live 2026-09-07: 30 of 63 reads on a just-charged Magic Mouse 2024 came back pct=0
    // from the lingering charge-cable interface. 0 is not a level Apple firmware reports.
    [Fact]
    public void ParseRid90Percent_RejectsZero_ButKeepsLiveValues()
    {
        Assert.Null(MouseBatteryDevice.ParseRid90Percent([0x90, 0x04, 0x00]));
        Assert.Null(MouseBatteryDevice.ParseRid90Percent([0x90, 0x00, 0x00]));
        Assert.True(MouseBatteryDevice.IsBogusZeroReport([0x90, 0x04, 0x00]));
        Assert.False(MouseBatteryDevice.IsBogusZeroReport([0x90, 0x04, 0x2E]));
        Assert.False(MouseBatteryDevice.IsBogusZeroReport([0x47, 0x00, 0x00])); // wrong report id
        Assert.Equal(46, MouseBatteryDevice.ParseRid90Percent([0x90, 0x04, 0x2E])); // live mm-hid-probe
        Assert.Equal(1, MouseBatteryDevice.ParseRid90Percent([0x90, 0x04, 0x01]));
        Assert.Equal(100, MouseBatteryDevice.ParseRid90Percent([0x90, 0x04, 100]));
    }

    // Live BT stack for the 0323 (all CM_PROB_OK).
    const string Bt0323Col01 =
        @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323&col01#a&31e5d054&2a&0000";
    const string Bt0323Col02 =
        @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323&col02#a&31e5d054&2a&0001";
    const string BtEnum0323 =
        @"\\?\bthenum#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323#9&73b8b28&0&d0c050cc8c4d_c00000000";

    // Charge-cable leftovers after a USB-C charge (all CM_PROB_PHANTOM, all still readable).
    const string Phantom0323Col01 = @"\\?\hid#vid_05ac&pid_0323&mi_01&col01#a&16288706&0&0000";
    const string Phantom0323Col02 = @"\\?\hid#vid_05ac&pid_0323&mi_01&col02#a&16288706&0&0001";
    const string Phantom0323Col03 = @"\\?\hid#vid_05ac&pid_0323&mi_01&col03#a&16288706&0&0002";
    const string Phantom0323Mi00  = @"\\?\usb#vid_05ac&pid_0323&mi_00#9&80f490&1&0000";
    const string Phantom0323Root  = @"\\?\usb#vid_05ac&pid_0323#j84hj804ysb000053a";

    // Regression: the phantom USB col02 also passes the col02 battery gate, so the tray
    // listed Magic Mouse 2024 twice and read a false 0% off the dead cable interface.
    [Fact]
    public void Discover_0323_PhantomUsbCol02_IsSuppressed_BluetoothWins()
    {
        var paths = new[]
        {
            Phantom0323Col01, Phantom0323Col02, Phantom0323Col03, Phantom0323Mi00, Phantom0323Root,
            BtEnum0323, Bt0323Col01, Bt0323Col02,
        };
        var found = DeviceRegistry.DiscoverFromPaths(paths);
        Assert.Single(found);
        Assert.Equal("Magic Mouse 2024", found[0].DeviceName);
        Assert.Equal(DeviceKind.MagicMouseV3, found[0].Kind);
        Assert.Equal("0323", found[0].Pid);
        Assert.Equal(Bt0323Col02, Assert.IsType<MouseBatteryDevice>(found[0]).DevicePath);
    }

    // Enumeration order must not decide the winner.
    [Fact]
    public void Discover_0323_BluetoothWins_RegardlessOfPathOrder()
    {
        var found = DeviceRegistry.DiscoverFromPaths([Bt0323Col02, Phantom0323Col02]);
        Assert.Single(found);
        Assert.Equal(Bt0323Col02, Assert.IsType<MouseBatteryDevice>(found[0]).DevicePath);
    }

    // A genuinely cabled Magic Mouse has no BT interface and must still report battery.
    [Fact]
    public void Discover_0323_UsbOnly_IsStillDiscovered()
    {
        var found = DeviceRegistry.DiscoverFromPaths([Phantom0323Col02]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicMouseV3, found[0].Kind);
        Assert.Equal(Phantom0323Col02, Assert.IsType<MouseBatteryDevice>(found[0]).DevicePath);
    }

    // Two BT collections that both pass the battery gate still yield one physical device.
    [Fact]
    public void Discover_SamePid_NeverListedTwice()
    {
        var found = DeviceRegistry.DiscoverFromPaths(
        [
            Bt0323Col02,
            @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323&col02#a&31e5d054&2a&0009",
        ]);
        Assert.Single(found);
        Assert.Equal(Bt0323Col02, Assert.IsType<MouseBatteryDevice>(found[0]).DevicePath);
    }

    [Theory]
    [InlineData(Bt0323Col02, true)]
    [InlineData(BtEnum0323, true)]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&0269#9&classic", true)]
    [InlineData(Phantom0323Col02, false)]
    [InlineData(Phantom0323Root, false)]
    [InlineData(@"\\?\hid#vid_05ac&pid_0239&col02#7&usb", false)]
    [InlineData("", false)]
    public void TransportPredicates_SplitBtFromUsbForm(string path, bool bluetooth)
    {
        Assert.Equal(bluetooth, DeviceRegistry.IsBluetoothTransportPath(path));
        Assert.Equal(!bluetooth && path.Length > 0, DeviceRegistry.IsUsbTransportPath(path));
    }

    // Live BT stack shape for a Magic Mouse v2: a pointer collection and a vendor collection
    // on the same PID. Nothing in either PATH says which of them answers a battery read.
    const string Bt0269Col01 =
        @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0269&col01#a&1f2e3d4c&0&0000";
    const string Bt0269Col02 =
        @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0269&col02#a&1f2e3d4c&0&0001";
    const string Usb0269Col02 = @"\\?\hid#vid_05ac&pid_0269&mi_01&col02#a&16288706&0&0001";

    // Regression for issue #124: one tester with a Magic Mouse v2 reported that Magic Tray
    // finds no battery while Apple's driver is bound. Discovery used to keep the first
    // interface per PID and discard the rest, so if Windows enumerated the pointer
    // collection first the other collection was thrown away and no read could ever reach
    // it. The tray still lists ONE mouse, so one device leaves discovery - but it now
    // carries every collection of that mouse as an ordered read candidate.
    [Fact]
    public void Discover_V2_KeepsAllCollectionsOfOneMouseAsCandidates()
    {
        var found = DeviceRegistry.DiscoverFromPaths([Bt0269Col01, Bt0269Col02]);
        Assert.Single(found);
        var mouse = Assert.IsType<MouseBatteryDevice>(found[0]);
        Assert.Equal(DeviceKind.MagicMouseV2, mouse.Kind);
        Assert.Equal("0269", mouse.Pid);
        Assert.Equal(Bt0269Col01, mouse.DevicePath);
        Assert.Equal(new[] { Bt0269Col01, Bt0269Col02 }, mouse.CandidatePaths);
    }

    // Candidates are the live device's own collections, never resurrected charge-cable
    // phantoms: USB still loses to Bluetooth for the same PID, and the dropped USB path
    // does not come back as a candidate.
    [Fact]
    public void Discover_V2_UsbPhantom_IsNotKeptAsCandidate()
    {
        var found = DeviceRegistry.DiscoverFromPaths([Usb0269Col02, Bt0269Col01, Bt0269Col02]);
        Assert.Single(found);
        var mouse = Assert.IsType<MouseBatteryDevice>(found[0]);
        Assert.Equal(new[] { Bt0269Col01, Bt0269Col02 }, mouse.CandidatePaths);
    }

    // A genuinely cabled mouse has no Bluetooth interface and must still be discovered.
    [Fact]
    public void Discover_V2_UsbOnly_IsStillOneDevice()
    {
        var found = DeviceRegistry.DiscoverFromPaths([Usb0269Col02]);
        Assert.Single(found);
        var mouse = Assert.IsType<MouseBatteryDevice>(found[0]);
        Assert.Equal("0269", mouse.Pid);
        Assert.Equal(Usb0269Col02, mouse.DevicePath);
    }

    // The v3 gate is untouched by the candidate rule: COL01 is the pointer collection and is
    // rejected at classify time, so it can never become a read candidate.
    [Fact]
    public void Discover_0323_Col01_IsNeverACandidate()
    {
        var found = DeviceRegistry.DiscoverFromPaths([Bt0323Col01, Bt0323Col02]);
        Assert.Single(found);
        var mouse = Assert.IsType<MouseBatteryDevice>(found[0]);
        Assert.Equal(DeviceKind.MagicMouseV3, mouse.Kind);
        Assert.Equal(new[] { Bt0323Col02 }, mouse.CandidatePaths);
    }

    // Keyboards keep the old rule - col02 only, one device per PID. The candidate list is a
    // mouse-only answer to #124; a keyboard's battery channel is already known to be col02.
    [Fact]
    public void Discover_Keyboard_IsUnchanged_OneDevicePerPid()
    {
        const string kbdCol01 =
            @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&0239&col01#a&2b3c4d5e&0&0000";
        const string kbdCol02 =
            @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&0239&col02#a&2b3c4d5e&0&0001";
        const string kbdCol03 =
            @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&0239&col03#a&2b3c4d5e&0&0002";

        var found = DeviceRegistry.DiscoverFromPaths([kbdCol01, kbdCol02, kbdCol03]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicKeyboard, found[0].Kind);
        Assert.Equal("0239", found[0].Pid);
    }
}
