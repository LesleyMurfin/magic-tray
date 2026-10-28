// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// Discovery must hand AdaptivePoller every distinct interface that could answer a battery
// read. Live 2026-09-16: one Magic Mouse 2024 had two BT col02 interfaces and a leftover USB
// col02 interface; the one discovery kept answered [90 00 00] forever, so the tray showed
// "Battery unavailable" while the mouse itself was fine. AdaptivePoller groups by DeviceName
// and returns the first interface in the group that answers with a real percentage, ranking
// only the failure sentinels - -2 over -1 - when none does (AdaptivePoller.BestReading), so it
// only needs the candidates to reach it.
public class DeviceRegistryTests
{
    // The device identifiers in the paths below - Bluetooth address, USB serial - are
    // synthetic. They keep the shape of the captured ones (12 hex digits, same-length serial)
    // so every path still parses identically, but a real hardware identifier does not belong
    // in a repo file. Only the Bluetooth half has a scrubber behind it: Logger.RedactMacs
    // strips the 12-hex instance tail between '&' and '_' (its MacTail rule) out of debug.log.
    // Nothing matches the synthetic USB serial, which is exactly why a captured one must not
    // be committed here.

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

    // The path set captured in the live log of 2026-09-16, right after a USB-C charge left the
    // phantom USB nodes behind. It is that capture's interface set, not a claim about what the
    // machine enumerates now: every distinct col02 interface in the capture must survive, and
    // nothing else may. Keeping only one of them is what hid the working interface.
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
    // returns as soon as one interface answers a real percentage OR one read times out, so a
    // group costs at most one read budget per tick however many interfaces it holds. Without
    // the timeout exit the bound would be a fiction in exactly the case that matters: an
    // interface answering [90 00 00] never returns a real percentage.
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

    // --- Logitech (flag-gated third-party path) identity ---------------------------------
    //
    // Synthetic product ids, all outside DeviceRegistry's receiver list so they reach the
    // LogitechBatteryDevice branch: a BT mouse, a BT keyboard and a USB headset dongle.
    const string LogiMouseBt = @"\\?\hid#vid_046d&pid_b034#8&2b5e3d41&0&0000";
    const string LogiMouseBtCol02 = @"\\?\hid#vid_046d&pid_b034&col02#8&2b5e3d41&0&0001";
    const string LogiKeyboardBt = @"\\?\hid#vid_046d&pid_b35b#8&1f0ac937&0&0000";
    const string LogiHeadsetUsb = @"\\?\hid#vid_046d&pid_0aba&mi_03#7&3c1d0b82&0&0000";

    // Three different Logitech products enumerated in one cycle must not collapse into one
    // identity. Pid and DeviceName are the only keys downstream: AdaptivePoller groups
    // interfaces by DeviceName (one tray row, one winning reading per group) and both
    // AdaptivePoller.ShouldSkipPid and the config enable/disable map are keyed by Pid. With a
    // hardcoded identity a keyboard's reading lands on the mouse's tray row and disabling one
    // disables all three. The Pid must come from the path (DeviceRegistry.ExtractPid is the
    // repo's parser) and the name must carry it, so an unknown product is still tellable apart.
    [Fact]
    public void Discover_DistinctLogitechProductIds_GetDistinctIdentities()
    {
        var found = DeviceRegistry.DiscoverFromPaths(
            [LogiMouseBt, LogiKeyboardBt, LogiHeadsetUsb], enableThirdParty: true);

        Assert.Equal(3, found.Count);
        Assert.Equal(new[] { "b034", "b35b", "0aba" }, found.Select(d => d.Pid).ToArray());
        Assert.Equal(3, found.Select(d => d.Pid).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, found.Select(d => d.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(found, d =>
        {
            Assert.Equal(DeviceKind.LogitechMouse, d.Kind);
            Assert.Equal(DeviceRegistry.ExtractPid(d.DevicePath), d.Pid);
            // Human-readable and self-identifying: whatever the wording, the name has to
            // name the vendor and carry the product id that makes it unique.
            Assert.StartsWith("Logitech", d.DeviceName, StringComparison.Ordinal);
            Assert.Contains(d.Pid, d.DeviceName, StringComparison.OrdinalIgnoreCase);
        });
    }

    // The other half of the rule: discovery still keeps every interface (it cannot tell which
    // one answers HID++ without reading it), and two interfaces of the SAME product must share
    // one identity so AdaptivePoller treats them as one group with one tray row - the same
    // multiplicity contract the Apple paths have.
    [Fact]
    public void Discover_SameLogitechProductIdOnTwoInterfaces_SharesOneIdentity()
    {
        var found = DeviceRegistry.DiscoverFromPaths(
            [LogiMouseBt, LogiMouseBtCol02], enableThirdParty: true);

        Assert.Equal(new[] { LogiMouseBt, LogiMouseBtCol02 }, found.Select(d => d.DevicePath).ToArray());
        Assert.Equal("b034", found[0].Pid);
        Assert.Equal(found[0].Pid, found[1].Pid);
        Assert.Equal(found[0].DeviceName, found[1].DeviceName);
    }

    // Degenerate path: VID present, no pid_/pid& token at all. ExtractPid's documented fallback
    // is "0000", and the identity must follow it instead of throwing or going blank - a blank
    // DeviceName would be an unnamed tray row and an empty group key. It must still differ from
    // a real product's identity, so an unparseable interface cannot be grouped with a readable one.
    [Fact]
    public void Discover_LogitechPathWithoutProductId_FallsBackToStableIdentity()
    {
        const string noPid = @"\\?\hid#vid_046d#8&7c9b1a20&0&0000";
        var found = DeviceRegistry.DiscoverFromPaths([noPid, LogiMouseBt], enableThirdParty: true);

        Assert.Equal(2, found.Count);
        Assert.Equal("0000", found[0].Pid);
        Assert.StartsWith("Logitech", found[0].DeviceName, StringComparison.Ordinal);
        Assert.NotEqual(found[1].Pid, found[0].Pid);
        Assert.NotEqual(found[1].DeviceName, found[0].DeviceName);
    }
}
