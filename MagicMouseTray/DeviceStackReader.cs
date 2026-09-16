// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MagicMouseTray;

// Answers one question the rest of the reader cannot: is the bound lower filter
// actually ATTACHED to this mouse's live device stack right now?
//
// Every other signal we collect is registration, not attachment:
//   - LowerFilters under Enum\BTHENUM says PnP was TOLD to load the filter.
//   - sc query <service> reporting RUNNING (DriverHealthChecker.cs:324-345) says
//     the driver IMAGE is loaded somewhere in the kernel.
// After a reboot PnP can rebuild the BTHENUM stack without the filter while both
// of those still read healthy, which is exactly the dead-wheel case the repair
// menu used to call "No problems found" (the A5/A6 pass rule in
// docs/TEST-PLAN.md).
//
// DEVPKEY_Device_Stack is the repo's existing ground truth for attachment:
// scripts/capture-state.ps1:212-221 reads that property on the live BTHENUM
// instance and says there in its own words that LowerFilters and sc query only
// prove registration, Test-StackHasFilter (scripts/capture-state.ps1:146-155)
// is the name match against the returned string list, and
// scripts/diagnose-and-recover.ps1:266-268 is what calls it the discriminator
// for this exact post-reboot case. This file is the C# port of that measurement
// - read-only, no process spawn, one CM query per live instance.
internal static class DeviceStackReader
{
    // devpkey.h line 156 (Windows SDK):
    //   DEFINE_DEVPROPKEY(DEVPKEY_Device_Stack,
    //     0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2,
    //     14);   // DEVPROP_TYPE_STRING_LIST
    static readonly DEVPROPKEY DevpkeyDeviceStack = new()
    {
        fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"),
        pid = 14,
    };

    const uint DEVPROP_TYPE_STRING_LIST = 0x00002012;

    // NORMAL means "present devices only". That is the point: a charge-cable
    // phantom must not be allowed to answer a question about the live stack, so
    // an unresolvable instance is treated as no evidence, never as "not attached".
    const uint CM_LOCATE_DEVNODE_NORMAL = 0;

    const uint CR_SUCCESS = 0x00000000;
    const uint CR_BUFFER_SMALL = 0x0000001A;

    // true  = a live BTHENUM instance for this PID names boundFilterName in its
    //         DEVPKEY_Device_Stack.
    // false = at least one live instance answered and none of the answers names it
    //         (the proven contradiction: registered + running + not attached).
    // null  = no evidence. No instance resolved, or every property read failed.
    //         Callers must not treat null as either state.
    internal static bool? ContainsFilter(string pid, string boundFilterName)
    {
        // Nothing bound means there is no name to look for, so there is nothing
        // this query could prove either way.
        if (string.IsNullOrEmpty(pid) || string.IsNullOrEmpty(boundFilterName))
            return null;

        int instances = 0;
        int readable = 0;
        bool? inStack = null;

        try
        {
            foreach (var instanceId in LiveInstanceIds(pid))
            {
                instances++;

                var stack = ReadDeviceStack(instanceId);
                if (stack is null)
                    continue;
                readable++;

                if (NamesFilter(stack, boundFilterName))
                {
                    inStack = true;
                    break;
                }
            }

            // Only a read that succeeded can prove absence.
            if (inStack is null && readable > 0)
                inStack = false;
        }
        catch (Exception ex)
        {
            // DllNotFoundException / EntryPointNotFoundException on a stripped or
            // future Windows, PlatformNotSupportedException, registry security -
            // all of them mean we learned nothing, not that the filter is gone.
            Logger.Log($"REPAIR_STACK_FAILED pid={pid} bound={boundFilterName} err={ex.Message}");
            return null;
        }

        // One line per call: this runs on every poller tick.
        Logger.Log($"REPAIR_STACK pid={pid} bound={boundFilterName} "
            + $"in_stack={Describe(inStack)} instances={instances} readable={readable}");
        return inStack;
    }

    static string Describe(bool? inStack) =>
        inStack is null ? "unknown" : inStack.Value ? "true" : "false";

    // Stack entries are driver object paths - "\Driver\HidBth", "\Driver\mouhid",
    // "\Driver\MagicMouseDriver204Scroll" - while boundFilterName is the verbatim
    // registry service name ("MagicMouseDriver204Scroll"). So the name can only be
    // a SUBSTRING of an entry, which is exactly how the PowerShell measurement
    // matches it (capture-state.ps1:156, `$stackStr -imatch 'applewirelessmouse'`).
    //
    // A family filter under ANY name counts as attached, not just the bound one.
    // The planner treats "not attached" as a dead wheel and offers an elevated
    // restart, so the false positive to avoid is a stack that carries a working
    // vendor filter whose driver object is not named exactly like its service
    // (or a second build of the same package - rule 2's territory, and it
    // restarts the stack anyway). The dead-wheel stack this rule is for carries
    // no family filter at all, only \Driver\HidBth and \Driver\mouhid, so
    // widening the match costs no detection.
    static bool NamesFilter(string[] stack, string boundFilterName)
    {
        foreach (var entry in stack)
        {
            if (entry.Contains(boundFilterName, StringComparison.OrdinalIgnoreCase))
                return true;

            // Leaf of "\Driver\<service>" is the service name the family
            // predicates in RepairPlanner are written against.
            var leaf = entry[(entry.LastIndexOf('\\') + 1)..];
            if (RepairPlanner.IsKmdfFamily(leaf) || RepairPlanner.IsAppleFamily(leaf))
                return true;
        }
        return false;
    }

    // Devnode instance ids for this PID, built from the same registry walk and the
    // same PID matcher DeviceSnapshotReader.ReadBthenumLayer uses - there is one
    // PID-matching convention, not two. An instance id is
    // BTHENUM\<device key>\<instance key>; the usb\ / hid\vid_ phantom prefixes
    // DeviceRepair skips (DeviceRepair.cs:169-174) cannot occur here because only
    // Enum\BTHENUM is enumerated.
    static List<string> LiveInstanceIds(string pid)
    {
        var ids = new List<string>();

        using var root = Registry.LocalMachine.OpenSubKey(
            DeviceSnapshotReader.BtEnumBase, writable: false);
        if (root is null)
            return ids;

        foreach (var subkeyName in root.GetSubKeyNames())
        {
            if (!DeviceSnapshotReader.BthenumKeyMatchesPid(subkeyName, pid))
                continue;

            using var deviceKey = root.OpenSubKey(subkeyName, writable: false);
            if (deviceKey is null) continue;

            foreach (var instanceName in deviceKey.GetSubKeyNames())
                ids.Add(@"BTHENUM\" + subkeyName + "\\" + instanceName);
        }

        return ids;
    }

    // null on every failure: an instance that is not present, a property that is
    // absent (CR_NO_SUCH_VALUE), a type we did not ask for, or an empty list. A
    // live devnode always reports at least its own function driver, so an empty
    // list is a read we do not trust, not proof of an empty stack.
    static string[]? ReadDeviceStack(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
            return null;

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

        return ParseStringList(buffer, size);
    }

    // DEVPROP_TYPE_STRING_LIST is a REG_MULTI_SZ body: UTF-16 strings, each
    // NUL-terminated, the list closed by a second NUL. Odd trailing byte is
    // dropped rather than trusted. null for an empty list, per ReadDeviceStack.
    static string[]? ParseStringList(byte[] buffer, uint size)
    {
        int bytes = (int)Math.Min(size, (uint)buffer.Length) / 2 * 2;
        if (bytes == 0)
            return null;

        var entries = System.Text.Encoding.Unicode
            .GetString(buffer, 0, bytes)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return entries.Length > 0 ? entries : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    // PropertyBuffer is null on the sizing call, which marshals as a NULL pointer.
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY PropertyKey,
        out uint PropertyType, byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);
}
