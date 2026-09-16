// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// Discovery must hand AdaptivePoller every distinct interface that could answer a battery
// read. Live 2026-09-16: one Magic Mouse 2024 had two BT col02 interfaces and a leftover USB
// col02 interface; the one discovery kept answered [90 00 00] forever, so the tray showed
// "Battery unavailable" while the mouse itself was fine. AdaptivePoller groups by DeviceName
// and ranks a real percentage above -2 above -1 (AdaptivePoller.BestReading, scored by
// AdaptivePoller.ReadingRank), so it only needs the candidates to reach it.
public class DeviceRegistryTests
{
    // The device identifiers in the paths below - Bluetooth address, USB serial - are
    // synthetic. They keep the shape of the captured ones (12 hex digits, same-length serial)
    // so every path still parses identically, but a real hardware identifier does not belong
    // in a repo file: Logger.RedactMacs keeps exactly this shape out of debug.log.

    // Live BT stack for the 0323 (both CM_PROB_OK, both pass the col02 battery gate).
    const string BtCol02A =
        @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323&col02#a&31e5d054&2a&0009";
    const string BtCol02B =
        @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323&col02#a&31e5d054&2a&0001";
    const string BtCol01 =
        @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323&col01#a&31e5d054&2a&0000";
    const string BtEnum =
        @"\\?\bthenum#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323#9&73b8b28&0&aabbccddeeff_c00000000";

    // Charge-cable leftovers after a USB-C charge (CM_PROB_PHANTOM, all still readable).
    const string UsbCol01 = @"\\?\hid#vid_05ac&pid_0323&mi_01&col01#a&16288706&0&0000";
    const string UsbCol02 = @"\\?\hid#vid_05ac&pid_0323&mi_01&col02#a&16288706&0&0001";
    const string UsbCol03 = @"\\?\hid#vid_05ac&pid_0323&mi_01&col03#a&16288706&0&0002";
    const string UsbMi00 = @"\\?\usb#vid_05ac&pid_0323&mi_00#9&80f490&1&0000";
    const string UsbRoot = @"\\?\usb#vid_05ac&pid_0323#f0f0synthetic00001";

    static string[] PathsOf(IReadOnlyList<IBatteryDevice> found)
    {
        var paths = new string[found.Count];
        for (int i = 0; i < found.Count; i++)
            paths[i] = Assert.IsType<MouseBatteryDevice>(found[i]).DevicePath;
        return paths;
    }

    // The path set captured in the live log of 2026-09-16, in the bt=2 usb=6 state right after
    // a USB-C charge. It is that capture's interface set, not a claim about what the machine
    // enumerates now: every distinct col02 interface in the capture must survive, and nothing
    // else may. Keeping only one of them is what hid the working interface.
    [Fact]
    public void Discover_0323_LiveLogPathSet_KeepsEveryDistinctCol02Interface()
    {
        var found = DeviceRegistry.DiscoverFromPaths(
        [
            UsbCol01, UsbCol02, UsbCol03, UsbMi00, UsbRoot,
            BtEnum, BtCol01, BtCol02A, BtCol02B,
        ]);

        Assert.Equal(new[] { UsbCol02, BtCol02A, BtCol02B }, PathsOf(found));
        Assert.All(found, d =>
        {
            Assert.Equal(DeviceKind.MagicMouseV3, d.Kind);
            Assert.Equal("0323", d.Pid);
            Assert.Equal("Magic Mouse 2024", d.DeviceName);
        });
    }

    // Both transports for the same PID are candidates; discovery cannot tell which one
    // answers without reading it.
    [Fact]
    public void Discover_0323_UsbAndBluetoothCol02_BothSurvive()
    {
        var found = DeviceRegistry.DiscoverFromPaths([UsbCol02, BtCol02A]);
        Assert.Equal(new[] { UsbCol02, BtCol02A }, PathsOf(found));
    }

    // A collection gate exists only for the 0323 and for keyboards (DeviceRegistry.TryClassify).
    // Every other MouseBatteryDevice.KnownMice row matches on vendor plus product substrings
    // alone, so with the per-PID collapse gone each HID collection of a v1/v2 mouse becomes its
    // own device. That amplification is deliberate, not an oversight: the read cost is bounded
    // by AdaptivePoller.BestReading, which walks a DeviceName group in discovery order and
    // returns as soon as one interface answers a real percentage, so the rest go unread.
    [Fact]
    public void Discover_030D_EveryCollection_BecomesItsOwnDevice()
    {
        // Built from the VID_05AC / PID_030D row of MouseBatteryDevice.KnownMice (USB v1).
        const string v1Col01 = @"\\?\hid#vid_05ac&pid_030d&mi_01&col01#a&b1b1b1b1&0&0000";
        const string v1Col02 = @"\\?\hid#vid_05ac&pid_030d&mi_01&col02#a&b1b1b1b1&0&0001";
        const string v1Col03 = @"\\?\hid#vid_05ac&pid_030d&mi_01&col03#a&b1b1b1b1&0&0002";

        var found = DeviceRegistry.DiscoverFromPaths([v1Col01, v1Col02, v1Col03]);

        Assert.Equal(new[] { v1Col01, v1Col02, v1Col03 }, PathsOf(found));
        Assert.All(found, d =>
        {
            Assert.Equal(DeviceKind.MagicMouseV1, d.Kind);
            Assert.Equal("030d", d.Pid);
            Assert.Equal("Magic Mouse v1", d.DeviceName);
        });
    }

    // Live log: Windows enumerated the same USB col02 path twice in one poll cycle. The same
    // interface must not become two devices.
    [Fact]
    public void Discover_IdenticalPathListedTwice_YieldsOneDevice()
    {
        var found = DeviceRegistry.DiscoverFromPaths([UsbCol02, UsbCol02, UsbCol02.ToUpperInvariant()]);
        Assert.Equal(new[] { UsbCol02 }, PathsOf(found));
    }

    // The col02 gates are now the only thing collapsing a device's collections, so they have
    // to hold on their own: a 0323 with no col02 interface yields nothing...
    [Fact]
    public void Discover_0323_WithoutCol02_YieldsNothing()
    {
        Assert.Empty(DeviceRegistry.DiscoverFromPaths([BtCol01, UsbCol01, UsbCol03, UsbMi00, UsbRoot, BtEnum]));
    }

    // ...and a Magic Keyboard's three collections still yield one device, not three.
    [Fact]
    public void Discover_Keyboard_OnlyCol02Survives()
    {
        const string kbCol02 = @"\\?\hid#vid_05ac&pid_0239&col02#7&usb&0001";
        var found = DeviceRegistry.DiscoverFromPaths(
        [
            @"\\?\hid#vid_05ac&pid_0239&col01#7&usb&0000",
            kbCol02,
            @"\\?\hid#vid_05ac&pid_0239&col03#7&usb&0002",
        ]);
        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicKeyboard, found[0].Kind);
        Assert.Equal("0239", found[0].Pid);
        Assert.Equal("Apple Wireless Keyboard (2011)", found[0].DeviceName);
    }
}
