// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// StockDriverReader.Select is the whole "which driver is ACTUALLY in use"
// decision with the registry and cfgmgr32 lifted out, so it is driven here from
// injected StockNode fixtures. Those fixtures are the VERBATIM five-node
// reading taken off the reference PC's Magic Keyboard (PID 0239) on 2026-09-16,
// read-only and non-elevated - the shape recorded in StockDriverReader's header.
//
// The registry half (ForPid) stays untested on purpose, the same call
// DriverClaimReaderTests.cs:18-21 makes: it needs a live HKLM\...\Enum\BTHENUM
// with a paired Apple device plus a working cfgmgr32, neither of which exists on
// a clean CI box, and faking the hive would only test the fake.
public class StockDriverReaderTests
{
    const string KbdBase = @"{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&0239";
    const string KbdInstance = @"9&73b8b28&0&E806884B0741_C00000000";
    const string HidInstance0 = @"a&eaf9d13&9&0000";
    const string HidInstance1 = @"a&eaf9d13&9&0001";
    const string HidInstance2 = @"a&eaf9d13&9&0002";

    static StockDriverReader.StockNode Node(
        string instanceId,
        string? service,
        string? infPath,
        string? provider,
        string? version,
        string[] stack,
        bool? present = true,
        bool? ok = true) =>
        new(instanceId, service, infPath, provider, version, stack, present, ok);

    // BTHENUM\{00001124-...}_VID&000205ac_PID&0239\9&73b8b28&0&E806884B0741_C00000000
    // Service=HidBth, hidbth.inf, Microsoft, 10.0.26100.8737, Status OK.
    static StockDriverReader.StockNode BthHidParent(bool? ok = true) => Node(
        @"BTHENUM\" + KbdBase + "\\" + KbdInstance,
        "HidBth", "hidbth.inf", "Microsoft", "10.0.26100.8737",
        ["HidBth", "BthEnum"], ok: ok);

    // The second BTHENUM parent of the same keyboard: the {00001200} SDP/PnP
    // information profile node. bth.inf, Microsoft, NO function service, and no
    // started driver either - measured status word 0x01802000, problem 0, which
    // Windows itself calls OK. It must not cost the keyboard its clean bill.
    static StockDriverReader.StockNode BthSdpParent() => Node(
        @"BTHENUM\{00001200-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&0239\" + KbdInstance,
        null, "bth.inf", "Microsoft", "10.0.26100.9444",
        ["BthEnum"]);

    // HID\...&Col01 - the functional child: Service=kbdhid, keyboard.inf.
    static StockDriverReader.StockNode KeyboardCol01(bool? ok = true) => Node(
        @"HID\" + KbdBase + @"&Col01\" + HidInstance0,
        "kbdhid", "keyboard.inf", "Microsoft", "10.0.26100.8972",
        ["kbdclass", "kbdhid", "HidBth"], ok: ok);

    // HID\...&Col02 / &Col03 - raw HID collections, hidserv.inf, no service.
    // Col02 is where the battery Feature cap lives once the SDP patch is in.
    static StockDriverReader.StockNode HidservCol02(bool? ok = true) => Node(
        @"HID\" + KbdBase + @"&Col02\" + HidInstance1,
        null, "hidserv.inf", "Microsoft", "10.0.26100.1",
        ["HidBth"], ok: ok);

    static StockDriverReader.StockNode HidservCol03() => Node(
        @"HID\" + KbdBase + @"&Col03\" + HidInstance2,
        null, "hidserv.inf", "Microsoft", "10.0.26100.1",
        ["HidBth"]);

    // Enumeration order is registry order: both BTHENUM parents, then the three
    // HID children.
    static StockDriverReader.StockNode[] ReferenceKeyboard() =>
        [BthHidParent(), BthSdpParent(), KeyboardCol01(), HidservCol02(), HidservCol03()];

    [Fact]
    public void Select_ReferenceKeyboard_ReportsKbdhidNotTheBluetoothParent()
    {
        var info = StockDriverReader.Select(ReferenceKeyboard());

        Assert.NotNull(info);
        // The functional child wins over BOTH BTHENUM parents. HidBth is the
        // Bluetooth HID transport, not the driver that makes a keyboard type,
        // and bth.inf carries no function service at all.
        Assert.Equal("kbdhid", info!.Service);
        Assert.Equal("keyboard.inf", info.InfPath);
        Assert.Equal("Microsoft", info.Provider);
        Assert.Equal("10.0.26100.8972", info.Version);
        Assert.Equal(["kbdclass", "kbdhid", "HidBth"], info.Stack);
        Assert.True(info.AllNodesOk);
    }

    [Fact]
    public void Select_ParentOnly_FallsBackToTheTransportParent()
    {
        // No HID children resolved at all - the honest answer is still the
        // driver bound to the parent, named for what it is.
        var info = StockDriverReader.Select([BthHidParent(), BthSdpParent()]);

        Assert.NotNull(info);
        Assert.Equal("HidBth", info!.Service);
        Assert.Equal("hidbth.inf", info.InfPath);
        Assert.Equal(["HidBth", "BthEnum"], info.Stack);
    }

    [Fact]
    public void Select_ServicelessNodesOnly_ReportsTheLowestCollection()
    {
        // Nothing with a service resolved. hidserv.inf is weak evidence but it
        // is real evidence, so it beats claiming to know nothing - and Col02
        // beats Col03 on collection order rather than on enumeration order.
        var info = StockDriverReader.Select([HidservCol03(), HidservCol02()]);

        Assert.NotNull(info);
        Assert.Null(info!.Service);
        Assert.Equal("hidserv.inf", info.InfPath);
    }

    [Fact]
    public void Select_CollectionOrderIsNumericNotLexical()
    {
        var col02 = HidservCol02();
        var col10 = Node(
            @"HID\" + KbdBase + @"&Col10\" + HidInstance2,
            null, "wrong.inf", "Microsoft", "10.0.26100.1", ["HidBth"]);

        // "10" sorts before "02" as text; 2 < 10 as a number.
        var info = StockDriverReader.Select([col10, col02]);

        Assert.Equal("hidserv.inf", info!.InfPath);
    }

    [Fact]
    public void Select_NothingResolves_IsNull()
    {
        // No nodes at all: the device was never paired with this PC.
        Assert.Null(StockDriverReader.Select([]));
        Assert.Null(StockDriverReader.Select(null));

        // Registry keys exist but PnP has no live devnode for any of them - a
        // stale pairing record. Absence of a devnode is not a driver reading.
        Assert.Null(StockDriverReader.Select(
        [
            BthHidParent() with { Present = false },
            KeyboardCol01() with { Present = false },
        ]));

        // Nodes resolved and carry no driver evidence whatsoever. A name is
        // never invented to fill this in.
        Assert.Null(StockDriverReader.Select(
        [
            Node(@"BTHENUM\" + KbdBase + "\\" + KbdInstance, null, null, null, null, []),
            Node(@"HID\" + KbdBase + @"&Col01\" + HidInstance0, "", "  ", null, null, []),
        ]));
    }

    [Fact]
    public void Select_AnyResolvedNodeReportsAProblem_ClearsAllNodesOk()
    {
        // A sibling collection Windows has a problem code for does not change
        // WHICH driver is in use - it only stops the reader claiming every node
        // was confirmed working.
        var info = StockDriverReader.Select(
            [BthHidParent(), BthSdpParent(), KeyboardCol01(), HidservCol02(ok: false), HidservCol03()]);

        Assert.Equal("kbdhid", info!.Service);
        Assert.False(info.AllNodesOk);

        // The chosen node itself having a problem is the same answer: still
        // kbdhid, still not confirmed.
        var faultedPick = StockDriverReader.Select([BthHidParent(), KeyboardCol01(ok: false)]);
        Assert.Equal("kbdhid", faultedPick!.Service);
        Assert.False(faultedPick.AllNodesOk);
    }

    [Fact]
    public void Select_UnknownNodeState_IsNotConfirmed()
    {
        // cfgmgr32 could not be asked. Present=null still counts as a candidate
        // on its registry evidence, but an unread status is never reported as
        // confirmed health.
        var info = StockDriverReader.Select(
            [KeyboardCol01() with { Present = null, Ok = null }]);

        Assert.Equal("kbdhid", info!.Service);
        Assert.False(info.AllNodesOk);
    }

    [Fact]
    public void Select_AbsentNodesDoNotDragHealthDown()
    {
        // The v3 mouse shape, measured on the reference PC 2026-09-16: five
        // nodes of which only FOUR resolve - the collection-less HID aggregate
        // reads Status Unknown and does NOT resolve as present (the v3 layout
        // documented at DeviceDiagReader.PointerKeyKind; SelectPointerKeys drops
        // that aggregate whenever a Col01 key exists), beside a live Col01
        // pointer child and a live Col02 vendor collection. That aggregate is not the mouse's
        // driver and must not make a healthy mouse read as unconfirmed.
        var v3 = @"{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0323";
        var info = StockDriverReader.Select(
        [
            Node(@"BTHENUM\" + v3 + @"\9&abc&0&001", "HidBth", "hidbth.inf", "Microsoft",
                "10.0.26100.8737", ["HidBth", "BthEnum"]),
            Node(@"HID\" + v3 + @"\a&31e5d054&2a&0000", null, null, null, null, [],
                present: false, ok: null),
            Node(@"HID\" + v3 + @"&Col01\a&31e5d054&2a&0000", "mouhid", "msmouse.inf",
                "Microsoft", "10.0.26100.1150", ["mouclass", "mouhid", "HidBth"]),
            Node(@"HID\" + v3 + @"&Col02\a&31e5d054&2a&0001", null, "hidserv.inf", "Microsoft",
                "10.0.26100.1", ["HidBth"]),
        ]);

        // Works for a mouse too, with no reference to any driver catalog: the
        // pointer collection's own function driver is the answer. Live reading
        // for this PID: mouhid / msmouse.inf / Microsoft / 10.0.26100.1150 /
        // mouclass|mouhid|HidBth, nodes=5 resolved=4 nodes_ok=true.
        Assert.Equal("mouhid", info!.Service);
        Assert.Equal("msmouse.inf", info.InfPath);
        Assert.Equal("10.0.26100.1150", info.Version);
        Assert.Equal(["mouclass", "mouhid", "HidBth"], info.Stack);
        Assert.True(info.AllNodesOk);
    }

    [Fact]
    public void Leaf_StripsTheDriverObjectPrefix()
    {
        // DEVPKEY_Device_Stack entries arrive as driver object paths; callers
        // render the leaf.
        Assert.Equal("kbdclass", StockDriverReader.Leaf(@"\Driver\kbdclass"));
        Assert.Equal("MagicMouseDriver204Scroll",
            StockDriverReader.Leaf(@"\Driver\MagicMouseDriver204Scroll"));
        // Nothing to strip, and a trailing separator must not yield "".
        Assert.Equal("HidBth", StockDriverReader.Leaf("HidBth"));
        Assert.Equal(@"\Driver\", StockDriverReader.Leaf(@"\Driver\"));
    }
}
