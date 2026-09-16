// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MagicMouseTray;

// ONE HONEST ANSWER TO "WHICH DRIVER IS ACTUALLY IN USE", FOR ANY DEVICE KIND.
//
// DriverHealthChecker cannot answer it, and that is by construction rather than
// by omission:
//   - GetPerDeviceStatus (DriverHealthChecker.cs:406) walks BTHENUM with
//     skipNonScroll:true, and the gate at TryParseAppleHidKey:494-498 drops
//     every PID in NonScrollApplePids:44-51 - all keyboards and all three
//     trackpads - so a keyboard never even reaches it;
//   - un-skipping them would be WRONG, not merely noisy. Classify:279-280
//     returns UnknownAppleMouse for any PID outside KnownMousePids, Aggregate
//     :350-376 lets that poison the global status, AfterFilterServiceState
//     :309-318 would flip Ok to NotBound on a device with no vendor filter to
//     run, and BoundCandidates/PreferredBoundName:75-139 are FILTERED to the
//     Apple/KMDF service-name families, so they can never return kbdhid or
//     HidBth no matter which device is fed in.
// That family filter is exactly the thing this file must not have. So this is a
// separate reader with no notion of "expected" driver at all: it reports what is
// bound, whatever it is, and never ranks it against a catalog.
//
// READ-ONLY AND NON-ELEVATED BY CONSTRUCTION: registry opens with
// writable:false, two cfgmgr32 query calls per node, no process spawn, no
// write, no devnode restart.
//
// HONESTY RULES, same as DeviceStackReader / DriverClaimReader:
//   - null = no evidence. Nothing resolved, or every read failed. Never a fault.
//   - a field that did not read comes back null; a name is never invented.
//   - AN ABSENT VENDOR FILTER IS NOT A FAULT. For every device this file was
//     written for, empty LowerFilters/UpperFilters is the CORRECT and expected
//     state (see the capture below: all five nodes, both lists empty, all OK).
//     This file therefore does not read the filter lists at all.
//   - AllNodesOk=false means NOT CONFIRMED, never "broken": it is also what an
//     unreadable status returns. Only true is positive evidence.
//
// THE SHAPE THIS WAS BUILT AGAINST - MAGIC KEYBOARD PID 0239, MEASURED VERBATIM
// ON THE REFERENCE PC, READ-ONLY AND NON-ELEVATED, 2026-09-16. Five live nodes,
// every one of them present, Status OK, CM_PROB_NONE, and with EMPTY
// LowerFilters and EMPTY UpperFilters - 100% Microsoft's own stack, nothing of
// this project's installed in it:
//
//  BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&0239\
//          9&73b8b28&0&E806884B0741_C00000000                  class HIDClass
//    Service=HidBth   Driver={745a17a0-74d3-11d0-b6fe-00a0c90f57da}\0004
//    InfPath=hidbth.inf  Provider=Microsoft  Version=10.0.26100.8737
//    Stack=\Driver\HidBth,\Driver\BthEnum        LowerFilters=[] UpperFilters=[]
//    Status=OK  Present=True  CM_PROB_NONE  status word 0x0180000A
//
//  BTHENUM\{00001200-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&0239\
//          9&73b8b28&0&E806884B0741_C00000000                  class Bluetooth
//    Service=(empty)  Driver={e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\0007
//    InfPath=bth.inf     Provider=Microsoft  Version=10.0.26100.9444
//    Stack=\Driver\BthEnum                       LowerFilters=[] UpperFilters=[]
//    Status=OK  Present=True  CM_PROB_NONE  status word 0x01802000
//    (the SDP/PnP-information profile node, NOT the HID one - it carries no
//     function service, which is why the rule below ranks it last, and no
//     started driver either, which is why NodeOk tests the problem code and
//     NOT DN_STARTED)
//
//  HID\{00001124-...}_VID&000205ac_PID&0239&Col01\a&eaf9d13&9&0000  class Keyboard
//    Service=kbdhid   Driver={4d36e96b-e325-11ce-bfc1-08002be10318}\0001
//    InfPath=keyboard.inf  Provider=Microsoft  Version=10.0.26100.8972
//    Stack=\Driver\kbdclass,\Driver\kbdhid,\Driver\HidBth
//                                                LowerFilters=[] UpperFilters=[]
//    Status=OK  Present=True  CM_PROB_NONE  status word 0x0180000A
//    <- THE FUNCTIONAL CHILD. This is the answer for a keyboard.
//
//  HID\{00001124-...}_VID&000205ac_PID&0239&Col02\a&eaf9d13&9&0001  class HIDClass
//    Service=(empty)  Driver={745a17a0-74d3-11d0-b6fe-00a0c90f57da}\0005
//    InfPath=hidserv.inf   Provider=Microsoft  Version=10.0.26100.1
//    Stack=\Driver\HidBth                        LowerFilters=[] UpperFilters=[]
//    Status=OK  Present=True  CM_PROB_NONE  status word 0x0180200A
//    (the vendor collection the battery Feature cap lives on -
//     DeviceRegistry.cs:138-140)
//
//  HID\{00001124-...}_VID&000205ac_PID&0239&Col03\a&eaf9d13&9&0002  class HIDClass
//    Service=(empty)  Driver={745a17a0-74d3-11d0-b6fe-00a0c90f57da}\0006
//    InfPath=hidserv.inf   Provider=Microsoft  Version=10.0.26100.1
//    Stack=\Driver\HidBth                        LowerFilters=[] UpperFilters=[]
//    Status=OK  Present=True  CM_PROB_NONE  status word 0x0180200A
//
// So for 0239 this reader answers kbdhid / keyboard.inf / Microsoft /
// 10.0.26100.8972 / [kbdclass, kbdhid, HidBth] / AllNodesOk=true.
internal sealed record StockDriverInfo(
    string? Service,
    string? InfPath,
    string? Provider,
    string? Version,
    string[] Stack,     // LEAF driver names, "\Driver\" stripped: kbdclass, kbdhid, HidBth
    bool AllNodesOk);   // true = every resolved node observed present and problem-free

internal static class StockDriverReader
{
    const string BtEnumBase = @"SYSTEM\CurrentControlSet\Enum\BTHENUM";
    const string HidEnumBase = @"SYSTEM\CurrentControlSet\Enum\HID";
    const string ClassBase = @"SYSTEM\CurrentControlSet\Control\Class";

    // Bluetooth HID-profile transport GUID, same constant and same reason as
    // DeviceDiagReader.cs:30 (private there, so not shareable without editing
    // that file). A live BT HID child key carries it; the USB charge-cable
    // phantoms do not.
    const string BtTransportGuid = "{00001124-0000-1000-8000-00805f9b34fb}";

    // The Bluetooth transport and enumerator services. HidBth is the BT HID
    // FUNCTION driver for the profile connection, not the driver that makes the
    // device do its job - the same distinction DriverHealthChecker.cs:25-26
    // makes. A node whose only service is one of these is the transport parent,
    // so it is the fallback answer, never the preferred one.
    static readonly string[] TransportServices =
        ["HidBth", "BthEnum", "BthPort", "BTHUSB", "BthLEEnum", "BthMini"];

    // NORMAL = present devices only, same reason as DeviceStackReader.cs:37-40
    // and DeviceDiagReader.cs:32-35: a charge-cable phantom or a stale pairing
    // record must never be allowed to answer a question about the live stack.
    const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    const uint CR_SUCCESS = 0x00000000;
    const uint CR_NO_SUCH_DEVNODE = 0x0000000D;
    const uint CR_BUFFER_SMALL = 0x00000001;

    // cfgmgr32.h: the devnode is flagged as having a problem. See NodeOk for
    // why this, and not DN_STARTED (0x00000008), is the health test.
    const uint DN_HAS_PROBLEM = 0x00000400;

    const uint DEVPROP_TYPE_STRING_LIST = 0x00002012;

    // One live devnode as READ, before anything is chosen. This is the seam that
    // keeps the selection testable: the registry/CM walk fills these in, Select
    // is pure, and the tests drive Select from fixtures rather than from a hive
    // (the same split DriverClaimReader.DatabaseReading uses, for the same
    // reason - faking HKLM would only test the fake).
    //
    // Present / Ok are TRI-STATE:
    //   true  - positively observed;
    //   false - the registry key exists and CM has no live devnode for it
    //           (Present), or the live devnode reports a problem code (Ok);
    //   null  - could not ask. cfgmgr32 missing on a stripped Windows, a node
    //           deleted mid-walk. No evidence either way, so Present=null still
    //           counts as a candidate on its registry evidence alone.
    internal readonly record struct StockNode(
        string InstanceId,   // BTHENUM\<device key>\<instance> or HID\<device key>\<instance>
        string? Service,
        string? InfPath,
        string? Provider,
        string? Version,
        string[] Stack,
        bool? Present,
        bool? Ok);

    // null = no evidence: no node for this PID, or every read failed. Never throws.
    internal static StockDriverInfo? ForPid(string? pid)
    {
        if (string.IsNullOrEmpty(pid))
            return null;

        List<StockNode> nodes;
        try
        {
            nodes = ReadNodes(pid);
        }
        catch (Exception ex)
        {
            // Registry security, a key deleted mid-walk, a hive we are not
            // allowed into. All of them mean we learned nothing.
            Logger.Log($"STOCK_DRIVER_FAILED pid={pid} err={ex.Message}");
            return null;
        }

        var info = Select(nodes);
        // One line per query, in the existing style:
        // STOCK_DRIVER pid=0239 service=kbdhid inf=keyboard.inf provider=Microsoft
        //   stack=kbdclass|kbdhid|HidBth nodes_ok=true nodes=5 resolved=5
        Logger.Log(Describe(pid, nodes, info));
        return info;
    }

    // THE SELECTION RULE, in one place and pure.
    //
    // A resolved node is one whose devnode was not proven absent (Present is
    // true or unknown) and that carries at least one piece of driver evidence.
    // Nodes proven absent are dropped outright - a stale pairing record for the
    // same PID under the other Apple VID spelling, or a phantom, must not get to
    // answer, and must not drag AllNodesOk down either.
    //
    // Among the resolved nodes the winner is, in order:
    //   1. THE FUNCTIONAL CHILD - a node with a service that is not the
    //      Bluetooth transport. For 0239 that is Col01 Service=kbdhid: the node
    //      that actually makes the keyboard type. For a mouse it is the pointer
    //      collection's mouhid. This is the whole point of the file.
    //   2. THE TRANSPORT PARENT - a node whose only service is HidBth/BthEnum.
    //      Naming the transport is still an honest answer to "what is bound";
    //      it is simply less informative than the child.
    //   3. A NODE WITH EVIDENCE BUT NO SERVICE - the raw-HID collections
    //      (hidserv.inf) and the {00001200} SDP profile node (bth.inf). Last,
    //      because "no function service" is the least specific answer there is.
    // Ties inside a tier break on collection number, collection-less first, then
    // enumeration order: that is what makes the transport parent (no &COL
    // suffix) the tier-2 answer rather than an arbitrary sibling collection.
    internal static StockDriverInfo? Select(IReadOnlyList<StockNode>? nodes)
    {
        if (nodes is null || nodes.Count == 0)
            return null;

        StockNode pick = default;
        bool picked = false;
        int bestTier = int.MaxValue;
        int bestOrder = int.MaxValue;
        bool allOk = true;
        int resolved = 0;

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.Present == false || !HasEvidence(node))
                continue;

            resolved++;
            // Anything short of a positive observation is "not confirmed".
            if (node.Ok != true)
                allOk = false;

            int tier = Tier(node);
            int order = CollectionOrder(node.InstanceId);
            if (tier < bestTier || (tier == bestTier && order < bestOrder))
            {
                pick = node;
                picked = true;
                bestTier = tier;
                bestOrder = order;
            }
        }

        if (!picked || resolved == 0)
            return null;

        return new StockDriverInfo(
            Blank(pick.Service),
            Blank(pick.InfPath),
            Blank(pick.Provider),
            Blank(pick.Version),
            pick.Stack ?? [],
            allOk);
    }

    // A node with nothing in any field is not evidence of a driver, so it is
    // neither an answer nor something AllNodesOk should speak for.
    static bool HasEvidence(StockNode node) =>
        Blank(node.Service) is not null
        || Blank(node.InfPath) is not null
        || Blank(node.Provider) is not null
        || Blank(node.Version) is not null
        || node.Stack is { Length: > 0 };

    static int Tier(StockNode node)
    {
        var service = Blank(node.Service);
        if (service is null)
            return 2;
        return IsTransportService(service) ? 1 : 0;
    }

    internal static bool IsTransportService(string? service) =>
        service is not null
        && Array.Exists(TransportServices, s => string.Equals(s, service, StringComparison.OrdinalIgnoreCase));

    // "&COL02" -> 2. No suffix -> 0, so the collection-less transport parent and
    // the single-collection v1 HID node sort ahead of numbered siblings. Parsed
    // rather than string-compared, same discipline as
    // DeviceDiagReader.IsCollectionOne: Col1/Col001 must not read as something
    // else, and anything that is not plain digits is not a collection number.
    internal static int CollectionOrder(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
            return 0;

        int col = instanceId.LastIndexOf("&COL", StringComparison.OrdinalIgnoreCase);
        if (col < 0)
            return 0;

        var rest = instanceId.AsSpan(col + 4);
        int end = rest.IndexOf('\\');
        if (end >= 0)
            rest = rest[..end];

        return int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    // Every BTHENUM parent instance for this PID plus every Bluetooth-transport
    // HID child of it. The PID matcher is DeviceSnapshotReader.BthenumKeyMatchesPid,
    // the single BTHENUM convention this repo has (DeviceStackReader:149 and
    // DriverClaimReader:131 use the same one) - no second VID table lives here.
    static List<StockNode> ReadNodes(string pid)
    {
        var nodes = new List<StockNode>();

        using (var root = Registry.LocalMachine.OpenSubKey(BtEnumBase, writable: false))
        {
            if (root is not null)
            {
                foreach (var deviceKeyName in root.GetSubKeyNames())
                {
                    if (!DeviceSnapshotReader.BthenumKeyMatchesPid(deviceKeyName, pid))
                        continue;
                    AddInstances(root, deviceKeyName, "BTHENUM\\", nodes);
                }
            }
        }

        using (var root = Registry.LocalMachine.OpenSubKey(HidEnumBase, writable: false))
        {
            if (root is not null)
            {
                foreach (var deviceKeyName in root.GetSubKeyNames())
                {
                    if (!IsBluetoothHidChildKey(deviceKeyName, pid))
                        continue;
                    AddInstances(root, deviceKeyName, "HID\\", nodes);
                }
            }
        }

        return nodes;
    }

    static void AddInstances(
        RegistryKey root, string deviceKeyName, string enumeratorPrefix, List<StockNode> into)
    {
        using var deviceKey = root.OpenSubKey(deviceKeyName, writable: false);
        if (deviceKey is null)
            return;

        foreach (var instanceName in deviceKey.GetSubKeyNames())
        {
            using var instanceKey = deviceKey.OpenSubKey(instanceName, writable: false);
            if (instanceKey is null)
                continue;

            var instanceId = enumeratorPrefix + deviceKeyName + "\\" + instanceName;
            var (present, ok, stack) = Probe(instanceId);
            var software = ReadSoftwareKey(instanceKey);

            into.Add(new StockNode(
                InstanceId: instanceId,
                Service: Blank(instanceKey.GetValue("Service") as string),
                InfPath: software.InfPath,
                Provider: software.Provider,
                Version: software.Version,
                Stack: stack,
                Present: present,
                Ok: ok));
        }
    }

    // EVERY collection of the live Bluetooth HID stack, which is why this is not
    // DeviceDiagReader.ClassifyPointerKey: that one answers "is this the POINTER
    // child" and returns None for COL02 and above (DeviceDiagReader.cs:538-540).
    // Here COL02/COL03 are legitimate nodes to report on.
    //
    // The VID_ / &MI_ / USB\ forms are the USB charge-cable phantoms and are
    // rejected outright, same as DeviceDiagReader.cs:519-522: a phantom COL01
    // exists on the reference PC after any USB-C charge.
    internal static bool IsBluetoothHidChildKey(string? deviceKeyName, string pid)
    {
        if (string.IsNullOrEmpty(deviceKeyName) || string.IsNullOrEmpty(pid))
            return false;

        if (deviceKeyName.Contains("VID_", StringComparison.OrdinalIgnoreCase)
            || deviceKeyName.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
            || deviceKeyName.Contains(@"USB\", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!deviceKeyName.Contains(BtTransportGuid, StringComparison.OrdinalIgnoreCase))
            return false;

        return DeviceSnapshotReader.BthenumKeyMatchesPid(deviceKeyName, pid);
    }

    // InfPath / ProviderName / DriverVersion of the driver bound to this
    // instance, i.e. DEVPKEY_Device_DriverInfPath and its neighbours. Those
    // properties are backed by Control\Class\<class guid>\<NNNN>, and the
    // instance key names that software key in its Driver value - the same
    // resolution DriverClaimReader.BoundInfName:407-416 uses and measured the
    // same way. The instance key's OWN InfPath value is empty on these BTHENUM
    // parents, which is why the software key is the one that gets read. Read
    // through the registry rather than CM_Get_DevNode_PropertyW so three of the
    // four fields need no devnode handle at all.
    //
    // (private there, so this is a second copy rather than an edit to a file
    // this change does not own; the P/Invoke declarations below are duplicated
    // for the same reason DeviceDiagReader.cs:613-616 duplicates its own.)
    static (string? InfPath, string? Provider, string? Version) ReadSoftwareKey(
        RegistryKey instanceKey)
    {
        if (instanceKey.GetValue("Driver") is not string driverRef || !IsSafeDriverRef(driverRef))
            return (null, null, null);

        using var softwareKey = Registry.LocalMachine.OpenSubKey(
            ClassBase + "\\" + driverRef, writable: false);
        if (softwareKey is null)
            return (null, null, null);

        return (
            Blank(softwareKey.GetValue("InfPath") as string),
            Blank(softwareKey.GetValue("ProviderName") as string),
            Blank(softwareKey.GetValue("DriverVersion") as string));
    }

    // Present / Ok / Stack for one instance id. Never throws: a stripped or
    // future Windows without cfgmgr32, a PlatformNotSupportedException or a node
    // deleted mid-walk all come back as unknown, not as "absent".
    static (bool? Present, bool? Ok, string[] Stack) Probe(string instanceId)
    {
        try
        {
            var cr = CM_Locate_DevNodeW(out var devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL);
            if (cr == CR_NO_SUCH_DEVNODE)
                // The key is there and PnP has no live devnode for it. Proven
                // absent, so it is not a node this reader may speak for - and Ok
                // stays unknown rather than being reported as broken.
                return (false, null, []);
            if (cr != CR_SUCCESS)
                // We could not ask. No evidence either way.
                return (null, null, []);

            return (true, NodeOk(devInst), ReadDriverStack(devInst) ?? []);
        }
        catch (Exception ex)
        {
            Logger.Log($"STOCK_DRIVER_PROBE_FAILED id={instanceId} err={ex.Message}");
            return (null, null, []);
        }
    }

    // "Windows reports no problem with this devnode" - CM_PROB_NONE and no
    // DN_HAS_PROBLEM. That is exactly what Device Manager calls "This device is
    // working properly" and what Get-PnpDevice prints as Status OK, which is
    // what all five 0239 nodes report.
    //
    // DN_STARTED IS DELIBERATELY NOT REQUIRED, and that is a measured decision,
    // not a relaxation. Status words read off the reference PC's five 0239
    // nodes, 2026-09-16, non-elevated:
    //   BTHENUM {00001124} HID parent    0x0180000A  problem=0  DN_STARTED set
    //   BTHENUM {00001200} SDP parent    0x01802000  problem=0  DN_STARTED CLEAR
    //   HID &Col01 (kbdhid)              0x0180000A  problem=0  DN_STARTED set
    //   HID &Col02 (hidserv)             0x0180200A  problem=0  DN_STARTED set
    //   HID &Col03 (hidserv)             0x0180200A  problem=0  DN_STARTED set
    // The {00001200} profile node carries no function driver to start - it is
    // DN_NT_ENUMERATOR|DN_NT_DRIVER|DN_DISABLEABLE with neither DN_DRIVER_LOADED
    // nor DN_STARTED - yet Windows itself calls it healthy and the keyboard
    // types perfectly. Requiring DN_STARTED would therefore report a working
    // keyboard as unconfirmed on every single poll, which is the exact false
    // negative this whole change exists to remove.
    //
    // A genuine fault still fails this test: a problem code (28 no driver, 22
    // disabled, 43 stopped by the driver) is non-zero.
    //
    // null when the status could not be read: a failed query must never read as
    // a fault.
    static bool? NodeOk(uint devInst)
    {
        if (CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) != CR_SUCCESS)
            return null;
        return problem == 0 && (status & DN_HAS_PROBLEM) == 0;
    }

    // DEVPKEY_Device_Stack, the repo's existing ground truth for what is
    // ATTACHED (DeviceStackReader.cs:18-22), reduced to LEAF driver names:
    // "\Driver\kbdclass" -> "kbdclass". The leaf is what a caller can render and
    // what the family predicates elsewhere are written against; the "\Driver\"
    // prefix carries no information for either.
    //
    // null on every failure - an absent property, an unexpected type, an empty
    // list. A live devnode always reports at least its own function driver, so an
    // empty list is a read we do not trust, not proof of an empty stack.
    static string[]? ReadDriverStack(uint devInst)
    {
        var key = DevpkeyDeviceStack;

        // Two-call pattern: a null buffer asks for the size and reports
        // CR_BUFFER_SMALL, then the real read fills it.
        uint size = 0;
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out _, null, ref size, 0) != CR_BUFFER_SMALL
            || size == 0)
            return null;

        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out var type, buffer, ref size, 0) != CR_SUCCESS
            || type != DEVPROP_TYPE_STRING_LIST
            || size == 0)
            return null;

        int bytes = (int)Math.Min(size, (uint)buffer.Length) / 2 * 2;
        if (bytes == 0)
            return null;

        var entries = System.Text.Encoding.Unicode
            .GetString(buffer, 0, bytes)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
            return null;

        var leaves = new string[entries.Length];
        for (int i = 0; i < entries.Length; i++)
            leaves[i] = Leaf(entries[i]);
        return leaves;
    }

    internal static string Leaf(string entry)
    {
        if (string.IsNullOrEmpty(entry))
            return entry;
        int slash = entry.LastIndexOf('\\');
        return slash < 0 || slash == entry.Length - 1 ? entry : entry[(slash + 1)..];
    }

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    static string Describe(string pid, List<StockNode> nodes, StockDriverInfo? info)
    {
        int resolved = 0;
        foreach (var node in nodes)
            if (node.Present != false && HasEvidence(node))
                resolved++;

        if (info is null)
            return $"STOCK_DRIVER pid={pid} resolved=none nodes={nodes.Count}";

        return $"STOCK_DRIVER pid={pid} service={info.Service ?? "none"} "
            + $"inf={info.InfPath ?? "none"} provider={info.Provider ?? "none"} "
            + $"stack={(info.Stack.Length == 0 ? "none" : string.Join("|", info.Stack))} "
            + $"nodes_ok={(info.AllNodesOk ? "true" : "false")} "
            + $"version={info.Version ?? "none"} nodes={nodes.Count} resolved={resolved}";
    }

    // devpkey.h line 156 (Windows SDK), same key DeviceStackReader.cs:25-33
    // declares:
    //   DEFINE_DEVPROPKEY(DEVPKEY_Device_Stack,
    //     0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2,
    //     14);   // DEVPROP_TYPE_STRING_LIST
    static readonly DEVPROPKEY DevpkeyDeviceStack = new()
    {
        fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"),
        pid = 14,
    };

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    static extern uint CM_Get_DevNode_Status(
        out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    // PropertyBuffer is null on the sizing call, which marshals as a NULL pointer.
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY PropertyKey,
        out uint PropertyType, byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

    // {class guid}\NNNN - exactly one backslash, nothing else path-like. Same
    // gate as DriverClaimReader.IsSafeDriverRef:494-505: no registry-sourced
    // string is concatenated into a key path without it.
    static bool IsSafeDriverRef(string? driverRef)
    {
        if (string.IsNullOrEmpty(driverRef) || driverRef.Length > 128)
            return false;
        if (driverRef.Count(c => c == '\\') != 1)
            return false;
        if (driverRef[0] == '\\' || driverRef[^1] == '\\')
            return false;
        if (driverRef.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (var c in driverRef)
        {
            if (char.IsAsciiLetterOrDigit(c))
                continue;
            if (c is '\\' or '{' or '}' or '-' or '_' or '.')
                continue;
            return false;
        }
        return true;
    }
}
