// P/Invoke declarations for HID and SetupDi APIs shared across all device readers.
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MagicMouseTray;

internal static class HidNative
{
    internal const uint FILE_SHARE_READ       = 0x00000001;
    internal const uint FILE_SHARE_WRITE      = 0x00000002;
    internal const uint OPEN_EXISTING         = 3;
    internal const uint DIGCF_PRESENT         = 0x02;
    internal const uint DIGCF_DEVICEINTERFACE = 0x10;
    internal const int  HIDP_STATUS_SUCCESS   = 0x00110000;
    internal const uint GENERIC_READ          = 0x80000000u;
    internal const uint FILE_FLAG_OVERLAPPED  = 0x40000000u;
    internal const uint WAIT_OBJECT_0         = 0;
    internal const uint ERROR_IO_PENDING      = 997;
    internal const uint ERROR_DEVICE_NOT_CONNECTED = 1167;
    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    internal static readonly Guid HidGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    // Overlapped ReadFile — lpNumberOfBytesRead must be IntPtr.Zero when lpOverlapped is used.
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer,
        uint nNumberOfBytesToRead, IntPtr lpNumberOfBytesRead, ref OVERLAPPED lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetOverlappedResult(SafeFileHandle hFile,
        ref OVERLAPPED lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset,
        bool bInitialState, IntPtr lpName);

    [DllImport("kernel32.dll")]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    internal static extern bool CancelIo(SafeFileHandle hFile);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetInputReport(SafeFileHandle HidDeviceObject,
        byte[] ReportBuffer, int ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetFeature(SafeFileHandle HidDeviceObject,
        byte[] ReportBuffer, int ReportBufferLength);

    // B2 (experimental, flag-gated): HID++ negotiation for directly-connected third-party devices.
    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_SetOutputReport(SafeFileHandle HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

    [DllImport("hid.dll")]
    internal static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject,
        out IntPtr PreparsedData);

    [DllImport("hid.dll")]
    internal static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetValueCaps(int ReportType,
        [In, Out] HIDP_VALUE_CAPS[] ValueCaps,
        ref ushort ValueCapsLength, IntPtr PreparsedData);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string? Enumerator,
        IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet,
        IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        ref SP_DEVICE_INTERFACE_DETAIL_DATA DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize, out uint RequiredSize,
        IntPtr DeviceInfoData);

    // Same API, unmanaged buffer: the managed struct below carries a fixed-size path field and
    // therefore cannot be grown to a RequiredSize it does not already cover.
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize, out uint RequiredSize,
        IntPtr DeviceInfoData);

    [DllImport("setupapi.dll")]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    // Enumerates all present HID device interface paths.
    internal static IEnumerable<string> EnumerateHidPaths()
    {
        var guid = HidGuid;
        var devs = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == IntPtr.Zero || devs == INVALID_HANDLE_VALUE)
            yield break;

        try
        {
            uint index = 0;
            while (true)
            {
                var iface = new SP_DEVICE_INTERFACE_DATA();
                iface.cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();
                if (!SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref guid, index++, ref iface))
                    yield break;

                if (TryGetInterfacePath(devs, ref iface, index - 1, out var path))
                    yield return path;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devs);
        }
    }

    // Reads one enumerated interface's device path.
    //
    // Two defects met here and both dropped interfaces in silence, which is the worst failure
    // this file can have: discovery, the tray rows and the repair offers can only ever see what
    // this method yields. The size argument was the literal 512 against a field declared
    // [MarshalAs(ByValTStr, SizeConst = 512)] - 512 WCHARs, i.e. a struct of 1028 bytes - so
    // the call was told it had barely half the buffer it really had, and any path longer than
    // ~254 characters came back ERROR_INSUFFICIENT_BUFFER. The return value was then discarded,
    // so that failure left DevicePath empty and the interface was skipped without a log line.
    //
    // The fixed struct stays the fast path, one call per interface, now sized from the
    // marshalled struct itself. When it genuinely is too small the call reports RequiredSize
    // and the retry runs against an unmanaged block of exactly that size: cbSize is the header
    // size the API expects (8 on 64-bit, 6 on 32-bit, a value chosen by alignment rules rather
    // than by the field layout), while the path itself always begins at offset 4, right after
    // that DWORD.
    //
    // This is a P/Invoke loop with no injectable seam, so it carries no unit test: the markers
    // are the only evidence that an interface was lost, and until now there were none.
    static bool TryGetInterfacePath(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA iface, uint index, out string path)
    {
        uint headerSize = IntPtr.Size == 8 ? 8u : 6u;

        var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA { cbSize = headerSize };
        if (SetupDiGetDeviceInterfaceDetail(devs, ref iface, ref detail,
                (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DETAIL_DATA>(), out uint required, IntPtr.Zero))
        {
            path = detail.DevicePath ?? string.Empty;
            if (path.Length != 0) return true;
            Logger.Log($"HID_ENUM_PATH_EMPTY index={index} (interface reported no device path; skipped)");
            return false;
        }

        int err = Marshal.GetLastWin32Error();
        if (required == 0)
        {
            // No size to retry with: the failure is not about the buffer.
            Logger.Log($"HID_ENUM_PATH_FAILED index={index} err={err} (device path unreadable; interface skipped)");
            path = string.Empty;
            return false;
        }

        // RequiredSize is only trustworthy when the call that produced it failed for the reason
        // we think it did. Taking it on faith cost three distinct ways to read or write outside
        // the allocation, all of them silent:
        //   - the WriteInt32 below is unconditional, so a required of 1..3 put four bytes into a
        //     block of one to three;
        //   - the path read used the unbounded PtrToStringUni overload, which walks forward to a
        //     UTF-16 NUL with no relation at all to what was allocated, so a short block or one
        //     the driver left unterminated kept reading into whatever followed it;
        //   - (int)required is unchecked, so anything above int.MaxValue arrived at AllocHGlobal
        //     negative, and the OutOfMemoryException it throws escapes TryGetInterfacePath, ends
        //     the whole EnumerateHidPaths walk and surfaces as POLL_CYCLE_ERROR - losing every
        //     later interface too, when the contract of this rewrite is that a bad interface is
        //     skipped with a marker and the walk continues.
        // A real detail buffer is the 4-byte header plus at least one WCHAR plus its NUL, and no
        // HID device path approaches 64 KB, so anything outside that is a lying or corrupt size
        // and is refused before a byte is allocated - with the same marker a hard failure gets,
        // because the user-visible outcome is identical: this interface has no usable path.
        if (required < 6 || required > 64 * 1024)
        {
            Logger.Log($"HID_ENUM_PATH_FAILED index={index} err={err} size={required} (implausible RequiredSize; interface skipped)");
            path = string.Empty;
            return false;
        }

        IntPtr block = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(block, (int)headerSize);
            if (!SetupDiGetDeviceInterfaceDetail(devs, ref iface, block, required, out _, IntPtr.Zero))
            {
                Logger.Log($"HID_ENUM_PATH_FAILED index={index} err={Marshal.GetLastWin32Error()} size={required} (device path unreadable; interface skipped)");
                path = string.Empty;
                return false;
            }
            // Bounded read: the path region is everything after the 4-byte header, so at most
            // (required - 4) / 2 WCHARs. The bounded overload does not stop at a NUL, it returns
            // the whole region, so the terminator is cut here; a driver that forgot to terminate
            // simply yields the full region rather than a walk off the end of the block.
            string raw = Marshal.PtrToStringUni(block + 4, (int)((required - 4) / 2)) ?? string.Empty;
            int nul = raw.IndexOf('\0');
            path = nul >= 0 ? raw[..nul] : raw;
        }
        finally { Marshal.FreeHGlobal(block); }

        if (path.Length == 0)
        {
            Logger.Log($"HID_ENUM_PATH_EMPTY index={index} size={required} (interface reported no device path; skipped)");
            return false;
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct SP_DEVICE_INTERFACE_DETAIL_DATA
    {
        public uint cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    // Must match Windows OVERLAPPED exactly: ULONG_PTR fields = 8 bytes on x64 (total 32 bytes).
    [StructLayout(LayoutKind.Sequential)]
    internal struct OVERLAPPED
    {
        public UIntPtr Internal;
        public UIntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    // Layout matches hidpi.h (Pack=4, 96 bytes on x64). Only IsRange=false fields used.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
        [MarshalAs(UnmanagedType.U1)] public bool HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved1, Reserved2, Reserved3, Reserved4, Reserved5;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin, LogicalMax;
        public int PhysicalMin, PhysicalMax;
        public ushort Usage;      // [NotRange] Usage (same slot as [Range] UsageMin)
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesigMin;
        public ushort DesigMax;
        public ushort DataIdxMin;
        public ushort DataIdxMax;
    }
}
