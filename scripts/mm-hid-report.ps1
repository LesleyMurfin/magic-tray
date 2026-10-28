<#
.SYNOPSIS
    Read-only HID capture for a device report. Answers the one question issue
    #124 could not: which HID collections does this Apple device expose, and
    which battery channel actually answers on each of them.

.DESCRIPTION
    For every PRESENT Apple HID interface (USB VID_05AC, Bluetooth _VID&000205AC
    and _VID&0001004C) this prints the interface path with the Bluetooth address
    removed, the PID, the transport, the collection token, the top-level usage,
    the report byte lengths, every input and feature value cap, and the result of
    ATTEMPTING both battery channels on that node:

      input90    HID Input report 0x90 via HidD_GetInputReport, percent at byte 2.
                 The vendor collection channel (usage page 0xFF00, usage 0x0014).
      feature47  HID Feature via HidD_GetFeature at the report id the descriptor
                 declares for Generic Device Controls (0x0006) Battery Strength
                 (0x0020), percent at byte 1. The declared id wins; 0x47 is only
                 the fallback guess when the node declares none.

    Both channels are tried on every node, exactly the way MouseBatteryDevice
    tries them (zero-access handle, FILE_SHARE_READ|FILE_SHARE_WRITE, 64-byte
    input buffer, three input tries 50ms apart, feature buffer sized from the
    descriptor), so what this prints is what the app would see.

    Read-only: it never sets a feature report, never writes the registry, never
    touches a device, and writes exactly one file, into %APPDATA%\MagicMouseTray.
    Everything except the driver-binding section runs unelevated. That section
    reads LowerFilters out of HKLM\SYSTEM\CurrentControlSet\Enum, which an
    unelevated process may be refused; when it is refused the report says the
    driver shape is unknown instead of naming a shape it could not see, so run
    it elevated if you need that one line to be conclusive.

.EXAMPLE
    .\mm-hid-report.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 2
$ErrorActionPreference = 'Continue'

# Mouse/trackpad PIDs from MouseBatteryDevice.KnownMice. Keep in sync with the
# catalog in diagnose-and-recover.ps1; this list only drives the driver-binding
# section, so an out-of-date entry costs a missing row, never a wrong reading.
$MousePids = @('0323', '030d', '0269', '0310', '0265', '030e', '0324')
$KmdfPrefix = 'MagicMouseDriver'
$ApplePrefix = 'applewirelessmouse'
# Stands in for a filter list that could not be READ, as opposed to one that was
# read and is empty. Without it a refused Enum key is indistinguishable from a
# PC with no filters installed, and the report asserts the wrong driver shape.
$UnreadableFilterSentinel = '(unreadable)'
$AppleVendorIds = @('05ac', '0001004c', '000205ac')

# 12-hex runs that are the SAME on every Windows PC, so they are not anybody's
# address and redacting them only destroys the two fields a maintainer reads
# first. 00805f9b34fb is the Bluetooth SIG base UUID tail, carried by every
# BTHENUM service GUID in these paths ({00001124-...} HID, {00001200-...} PnP
# information); 001111000030 is the tail of the HID device interface class GUID
# {4d1e55b2-f16f-11cf-88cb-001111000030} that ends every interface path. The
# list is a WHITELIST of constants, never a pattern that tries to recognise an
# address: a real address can look like anything, so the default stays redact.
$script:MacSafeConstants = @('00805f9b34fb', '001111000030')

$script:Lines = @()

# Every line goes through one sink, so the console text and the file text are
# the same text and neither can carry an address. Logger.RedactMacs uses the
# same argument: a policy at the sink is one the next line to be written cannot
# forget.
function Write-Log {
    param([string]$Text, [string]$Color = '')
    $safe = ConvertTo-MacSafeText $Text
    $script:Lines += $safe
    if ($Color) { Write-Host $safe -ForegroundColor $Color }
    else { Write-Host $safe }
}

# The redaction rules of BugReport.Redact, in its order: the punctuated pair
# forms, the Dev_ form, then the bare 12-hex belt - with the constant GUID
# tails above carved out of the belt, which is the one place this script has to
# differ from BugReport. BugReport scrubs a log tail, where a mangled GUID
# costs nothing; this report IS the device evidence, and a path whose service
# GUID reads <mac> no longer says which SDP service the node hangs off.
#
# Ahead of those three, one rule BugReport does not need: the third node of a
# USB instance id is the unit's iSerialNumber, which is not a MAC shape and so
# matched none of the three rules. A Lightning Magic Mouse 2 enumerates over USB
# while it charges, so the report would otherwise carry that serial under a
# "safe to paste" banner.
function ConvertTo-MacSafeText {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $t = [regex]::Replace($Text, '(?i)(USB\\VID_[0-9a-f]{4}&PID_[0-9a-f]{4}(&MI_[0-9a-f]{2})?\\)[^\\\s,;\)\]]+', '$1<serial>')
    $t = [regex]::Replace($t, '(?i)(?<![0-9a-f])([0-9a-f]{2}[:-]){5}[0-9a-f]{2}(?![0-9a-f])', '<mac>')
    $t = [regex]::Replace($t, '(?i)Dev_[0-9a-f]{12}', 'Dev_<mac>')
    return [regex]::Replace($t, '(?i)(?<![0-9a-f])[0-9a-f]{12}(?![0-9a-f])', {
        param($m)
        if ($script:MacSafeConstants -contains $m.Value.ToLowerInvariant()) { return $m.Value }
        return '<mac>'
    })
}

# --- HID interop ------------------------------------------------------------
# Same entry points and the same call shape as HidNative.cs / MouseBatteryDevice,
# so a disagreement between this capture and the app's debug.log is a real
# difference in the device, not a difference in how it was asked.
$hidSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MagicTrayHidReport
{
    public class ValueCapInfo
    {
        public int UsagePage;
        public int Usage;
        public int ReportId;
        public int BitSize;
        public int ReportCount;
        public int LinkCollection;
    }

    public class ChannelResult
    {
        public string Channel;
        public int ReportId;
        public bool Declared;
        public bool Ok;
        public int Percent;
        public int Error;
        public string Bytes;
        public string Reason;
    }

    public class NodeInfo
    {
        public string Path;
        public bool Opened;
        public int OpenError;
        public bool HasCaps;
        public int CapsError;
        // CapsError holds a Win32 code on the HidD_GetPreparsedData path and an
        // NTSTATUS on the HidP_GetCaps path. The two number spaces overlap, so a
        // pasted report has to say which one it is printing.
        public bool CapsErrorIsNtStatus;
        public int UsagePage;
        public int Usage;
        public int InputLen;
        public int OutputLen;
        public int FeatureLen;
        public int LinkCollectionCount;
        public int InputValueCapCount;
        public int FeatureValueCapCount;
        public ValueCapInfo[] InputCaps;
        public ValueCapInfo[] FeatureCaps;
        public ChannelResult[] Channels;
    }

    public static class Probe
    {
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint OPEN_EXISTING = 3;
        const uint DIGCF_PRESENT = 0x02;
        const uint DIGCF_DEVICEINTERFACE = 0x10;
        const int HIDP_STATUS_SUCCESS = 0x00110000;
        const int HIDP_INPUT = 0;
        const int HIDP_FEATURE = 2;
        const int UP_GENERIC_DEVICE = 0x0006;
        const int USG_BATTERY_STRENGTH = 0x0020;
        const int UP_VENDOR_BATTERY = 0xFF00;
        const int USG_VENDOR_BATTERY = 0x0014;
        const int INPUT_REPORT_ID = 0x90;
        const int FALLBACK_FEATURE_REPORT_ID = 0x47;
        const int MIN_VALID_PERCENT = 1;

        static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        static Guid HidGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetInputReport(IntPtr HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetFeature(IntPtr HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

        // SetLastError, or GetLastWin32Error below reports whatever the last
        // SetLastError-enabled call left behind - in practice the SUCCESSFUL
        // CreateFile, i.e. 0.
        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetPreparsedData(IntPtr HidDeviceObject, out IntPtr PreparsedData);

        [DllImport("hid.dll")]
        static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

        [DllImport("hid.dll")]
        static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);

        [DllImport("hid.dll")]
        static extern int HidP_GetValueCaps(int ReportType, [In, Out] HIDP_VALUE_CAPS[] ValueCaps,
            ref ushort ValueCapsLength, IntPtr PreparsedData);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string Enumerator,
            IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
            ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            ref SP_DEVICE_INTERFACE_DETAIL_DATA DeviceInterfaceDetailData,
            uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public uint cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
            public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
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

        // hidpi.h layout, Pack=4. Only the [NotRange] fields are read.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct HIDP_VALUE_CAPS
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
            public ushort Usage;
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesigMin;
            public ushort DesigMax;
            public ushort DataIdxMin;
            public ushort DataIdxMax;
        }

        public static string[] Paths()
        {
            List<string> found = new List<string>();
            IntPtr devs = SetupDiGetClassDevs(ref HidGuid, null, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devs == IntPtr.Zero || devs == INVALID_HANDLE_VALUE)
                return found.ToArray();
            try
            {
                uint index = 0;
                while (true)
                {
                    SP_DEVICE_INTERFACE_DATA iface = new SP_DEVICE_INTERFACE_DATA();
                    iface.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref HidGuid, index++, ref iface))
                        break;
                    SP_DEVICE_INTERFACE_DETAIL_DATA detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                    detail.cbSize = IntPtr.Size == 8 ? 8u : 6u;
                    uint needed;
                    SetupDiGetDeviceInterfaceDetail(devs, ref iface, ref detail, 512, out needed, IntPtr.Zero);
                    if (!string.IsNullOrEmpty(detail.DevicePath))
                        found.Add(detail.DevicePath);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devs);
            }
            return found.ToArray();
        }

        static string Head(byte[] buf, int count)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < count && i < buf.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(buf[i].ToString("X2"));
            }
            return sb.ToString();
        }

        static ValueCapInfo[] ReadCaps(IntPtr preparsed, int reportType, ushort count)
        {
            if (count == 0) return new ValueCapInfo[0];
            HIDP_VALUE_CAPS[] raw = new HIDP_VALUE_CAPS[count];
            ushort len = count;
            if (HidP_GetValueCaps(reportType, raw, ref len, preparsed) != HIDP_STATUS_SUCCESS)
                return new ValueCapInfo[0];
            List<ValueCapInfo> outCaps = new List<ValueCapInfo>();
            for (int i = 0; i < len; i++)
            {
                ValueCapInfo c = new ValueCapInfo();
                c.UsagePage = raw[i].UsagePage;
                // [NotRange] Usage shares its slot with [Range] UsageMin; a
                // ranged cap therefore prints its first usage, which is the
                // right thing for a report that is looking for one usage.
                c.Usage = raw[i].Usage;
                c.ReportId = raw[i].ReportID;
                c.BitSize = raw[i].BitSize;
                c.ReportCount = raw[i].ReportCount;
                c.LinkCollection = raw[i].LinkCollection;
                outCaps.Add(c);
            }
            return outCaps.ToArray();
        }

        // Input channel: MouseBatteryDevice.TryInput90 verbatim - a 64-byte buffer
        // and three IOCTLs 50ms apart, because a Bluetooth mouse that has just
        // woken refuses the first call.
        static ChannelResult TryInput90(IntPtr h, bool declared)
        {
            ChannelResult r = new ChannelResult();
            r.Channel = "input90";
            r.ReportId = INPUT_REPORT_ID;
            r.Declared = declared;
            r.Reason = "";
            r.Bytes = "";
            byte[] buf = new byte[64];
            for (int n = 0; n < 3; n++)
            {
                Array.Clear(buf, 0, buf.Length);
                buf[0] = (byte)INPUT_REPORT_ID;
                if (HidD_GetInputReport(h, buf, buf.Length))
                {
                    r.Bytes = Head(buf, 3);
                    int pct = buf[2];
                    if (buf[0] == INPUT_REPORT_ID && pct >= MIN_VALID_PERCENT && pct <= 100)
                    {
                        r.Ok = true;
                        r.Percent = pct;
                    }
                    else
                    {
                        r.Percent = -1;
                        r.Reason = pct == 0 ? "zero_byte" : "out_of_range";
                    }
                    return r;
                }
                if (n < 2) System.Threading.Thread.Sleep(50);
            }
            r.Percent = -1;
            r.Error = Marshal.GetLastWin32Error();
            r.Reason = "call_failed";
            return r;
        }

        // Feature channel: MouseBatteryDevice.TryFeature verbatim - buffer sized
        // from the descriptor when the node declares feature reports at all, 64
        // for a blind try on a node that declares none.
        static ChannelResult TryFeature(IntPtr h, int reportId, bool declared, int featureLen)
        {
            ChannelResult r = new ChannelResult();
            r.Channel = "feature47";
            r.ReportId = reportId;
            r.Declared = declared;
            r.Reason = "";
            r.Bytes = "";
            int size = featureLen > 0 ? Math.Max(featureLen, 2) : 64;
            byte[] buf = new byte[size];
            buf[0] = (byte)reportId;
            if (!HidD_GetFeature(h, buf, buf.Length))
            {
                r.Percent = -1;
                r.Error = Marshal.GetLastWin32Error();
                r.Reason = "call_failed";
                return r;
            }
            r.Bytes = Head(buf, 2);
            int pct = buf[1];
            if (pct >= MIN_VALID_PERCENT && pct <= 100)
            {
                r.Ok = true;
                r.Percent = pct;
            }
            else
            {
                r.Percent = -1;
                r.Reason = pct == 0 ? "zero_byte" : "out_of_range";
            }
            return r;
        }

        public static NodeInfo Inspect(string path)
        {
            NodeInfo n = new NodeInfo();
            n.Path = path;
            n.InputCaps = new ValueCapInfo[0];
            n.FeatureCaps = new ValueCapInfo[0];
            n.Channels = new ChannelResult[0];

            // Zero access plus share read|write is what avoids err 5 on an
            // interface a filter (mouhid, applewirelessmouse) already owns.
            IntPtr h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE)
            {
                n.OpenError = Marshal.GetLastWin32Error();
                return n;
            }
            n.Opened = true;
            try
            {
                IntPtr preparsed;
                if (!HidD_GetPreparsedData(h, out preparsed))
                {
                    n.CapsError = Marshal.GetLastWin32Error();
                    return n;
                }
                int declaredFeatureRid = -1;
                try
                {
                    HIDP_CAPS caps = new HIDP_CAPS();
                    int status = HidP_GetCaps(preparsed, ref caps);
                    if (status != HIDP_STATUS_SUCCESS)
                    {
                        n.CapsError = status;
                        n.CapsErrorIsNtStatus = true;
                        return n;
                    }
                    n.HasCaps = true;
                    n.UsagePage = caps.UsagePage;
                    n.Usage = caps.Usage;
                    n.InputLen = caps.InputReportByteLength;
                    n.OutputLen = caps.OutputReportByteLength;
                    n.FeatureLen = caps.FeatureReportByteLength;
                    n.LinkCollectionCount = caps.NumberLinkCollectionNodes;
                    n.InputValueCapCount = caps.NumberInputValueCaps;
                    n.FeatureValueCapCount = caps.NumberFeatureValueCaps;
                    n.InputCaps = ReadCaps(preparsed, HIDP_INPUT, caps.NumberInputValueCaps);
                    n.FeatureCaps = ReadCaps(preparsed, HIDP_FEATURE, caps.NumberFeatureValueCaps);
                    for (int i = 0; i < n.FeatureCaps.Length; i++)
                    {
                        if (n.FeatureCaps[i].UsagePage == UP_GENERIC_DEVICE &&
                            n.FeatureCaps[i].Usage == USG_BATTERY_STRENGTH)
                        {
                            declaredFeatureRid = n.FeatureCaps[i].ReportId;
                            break;
                        }
                    }
                }
                finally
                {
                    HidD_FreePreparsedData(preparsed);
                }

                bool vendorInput = n.UsagePage == UP_VENDOR_BATTERY && n.Usage == USG_VENDOR_BATTERY
                    && n.InputLen >= 3;
                bool featureDeclared = declaredFeatureRid >= 0;
                int featureRid = featureDeclared ? declaredFeatureRid : FALLBACK_FEATURE_REPORT_ID;

                List<ChannelResult> results = new List<ChannelResult>();
                results.Add(TryInput90(h, vendorInput));
                results.Add(TryFeature(h, featureRid, featureDeclared, n.FeatureLen));
                n.Channels = results.ToArray();
            }
            finally
            {
                CloseHandle(h);
            }
            return n;
        }
    }
}
'@

if (-not ('MagicTrayHidReport.Probe' -as [type])) {
    Add-Type -TypeDefinition $hidSource -Language CSharp -ErrorAction Stop
}

# --- Path shape helpers -----------------------------------------------------
# The vendor test is Test-InstanceMatchesPid's: an Apple vendor id must be
# PRESENT, because a PID alone is not unique across vendors.
function Get-AppleVendorId {
    param([string]$Path)
    $m = [regex]::Match($Path.ToLowerInvariant(), '(?:^|[^a-z0-9])vid(?:_|&)([0-9a-f]+)(?=[^0-9a-f]|$)')
    if (-not $m.Success) { return '' }
    if ($AppleVendorIds -notcontains $m.Groups[1].Value) { return '' }
    return $m.Groups[1].Value
}

function Get-DevicePidToken {
    param([string]$Path)
    $m = [regex]::Match($Path.ToLowerInvariant(), 'pid(?:_|&)([0-9a-f]{4})')
    if ($m.Success) { return $m.Groups[1].Value } else { return '????' }
}

# The &col0N fragment, lowercased, or 'none' when the path has no &col fragment
# (the 030D shape, which carries its battery Feature on the mouse collection).
function Get-CollectionToken {
    param([string]$Path)
    $m = [regex]::Match($Path.ToLowerInvariant(), '&(col[0-9a-f]{2})')
    if ($m.Success) { return $m.Groups[1].Value } else { return 'none' }
}

# VID_05AC with an underscore is the USB/HID enumerator form; the long
# _VID&000205AC / _VID&0001004C forms only ever come off BTHENUM.
function Get-TransportName {
    param([string]$VendorId)
    if ($VendorId -eq '05ac') { return 'USB' }
    return 'Bluetooth'
}

function Format-UsagePair {
    param([int]$UsagePage, [int]$Usage)
    return ('0x{0:X4}/0x{1:X4}' -f $UsagePage, $Usage)
}

# The tray starts this script UNELEVATED: MagicMouseTray/DriverInstaller.cs and
# the DiagnosticScripts launcher use UseShellExecute with no 'runas' verb, so a
# non-admin token is the normal case, not the exception. That matters one
# section down: an 'UNKNOWN shape - LowerFilters could not be read' line can
# mean the Enum key refused us, or it can mean the hardware genuinely has no
# filter - and only the token separates permissions from hardware. So the token
# is reported, in the header, where a reader meets it before the verdict.
#
# $null, never $false, when there is no Windows token to ask (Linux/macOS pwsh,
# where WindowsIdentity throws PlatformNotSupportedException): $false there
# would read as 'ran unelevated' and send a reader hunting a permission problem
# that never existed. Unknown is a third answer, not a flavour of no.
function Test-IsAdmin {
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        if (-not $identity) { return $null }
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        return [bool]$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        Write-Verbose "Windows token unavailable: $($_.Exception.Message)"
        return $null
    }
}

# --- Report -----------------------------------------------------------------
$stamp = Get-Date
Write-Log ''
Write-Log '=== MAGIC TRAY HID REPORT ===' 'Cyan'
Write-Log ("  Captured:    {0}" -f $stamp.ToString('yyyy-MM-dd HH:mm:ss'))

$osCaption = '(unknown)'
$osVersion = [string][Environment]::OSVersion.Version
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $osCaption = [string]$os.Caption
    $osVersion = [string]$os.Version
} catch { Write-Verbose "Win32_OperatingSystem unavailable: $($_.Exception.Message)" }
$displayVersion = ''
try {
    $cv = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
    if ($cv.PSObject.Properties['DisplayVersion']) { $displayVersion = [string]$cv.DisplayVersion }
    if ($cv.PSObject.Properties['UBR']) { $osVersion = "$osVersion.$($cv.UBR)" }
} catch { Write-Verbose "CurrentVersion key unreadable: $($_.Exception.Message)" }
Write-Log ("  Windows:     {0} {1} build {2}" -f $osCaption, $displayVersion, $osVersion)
Write-Log ("  PowerShell:  {0}" -f $PSVersionTable.PSVersion)
# Field, not prose: 'Admin=' is one grep away in a pasted report. Three answers,
# because unknown is not no.
$isAdmin = Test-IsAdmin
if ($null -eq $isAdmin) {
    $adminText = 'unknown (no Windows token on this PowerShell host)'
    $adminColor = 'Yellow'
} elseif ($isAdmin) {
    $adminText = 'yes (elevated)'
    $adminColor = ''
} else {
    $adminText = 'no (not elevated; an UNKNOWN shape below may be permissions, not hardware)'
    $adminColor = 'Yellow'
}
Write-Log ("  Admin={0}" -f $adminText) $adminColor

# Magic Tray version: the exe ships beside this script (csproj Content link), so
# look next to the script and one level up before giving up. An unknown version
# is stated as unknown - a guessed version would misdate the whole report.
$trayVersion = '(unknown - MagicMouseTray.exe not found next to this script)'
foreach ($dir in @($PSScriptRoot, (Split-Path -Parent $PSScriptRoot))) {
    if (-not $dir) { continue }
    $exe = Join-Path $dir 'MagicMouseTray.exe'
    if (Test-Path -LiteralPath $exe) {
        $trayVersion = [string](Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
        break
    }
}
Write-Log ("  Magic Tray:  {0}" -f $trayVersion)

# --- Apple HID interfaces ---------------------------------------------------
Write-Log ''
Write-Log '=== APPLE HID INTERFACES ===' 'Cyan'
$allPaths = @([MagicTrayHidReport.Probe]::Paths())
$applePaths = @($allPaths | Where-Object { (Get-AppleVendorId $_) -ne '' } | Sort-Object)
Write-Log ("  HID interfaces present: {0}   Apple: {1}" -f $allPaths.Count, $applePaths.Count)

if ($applePaths.Count -eq 0) {
    Write-Log '  No Apple HID interface is present. Pair or plug the device in and run this again.' 'Yellow'
}

$index = 0
foreach ($path in $applePaths) {
    $index++
    $vid = Get-AppleVendorId $path
    $hex = Get-DevicePidToken $path
    $col = Get-CollectionToken $path
    Write-Log ''
    Write-Log ("  [{0}] pid={1} transport={2} col={3} vid={4}" -f $index, $hex.ToUpperInvariant(), (Get-TransportName $vid), $col, $vid) 'White'
    Write-Log ("      path: {0}" -f $path)

    $node = [MagicTrayHidReport.Probe]::Inspect($path)
    if (-not $node.Opened) {
        Write-Log ("      OPEN FAILED err={0} - nothing below could be read on this interface." -f $node.OpenError) 'Red'
        continue
    }
    if (-not $node.HasCaps) {
        if ($node.CapsErrorIsNtStatus) {
            Write-Log ("      CAPS UNAVAILABLE ntstatus=0x{0:X8}" -f $node.CapsError) 'Red'
        } else {
            Write-Log ("      CAPS UNAVAILABLE err={0}" -f $node.CapsError) 'Red'
        }
        continue
    }

    Write-Log ("      top-level usage: {0}" -f (Format-UsagePair $node.UsagePage $node.Usage))
    Write-Log ("      report lengths:  input={0} output={1} feature={2}" -f $node.InputLen, $node.OutputLen, $node.FeatureLen)
    Write-Log ("      value caps:      input={0} feature={1} linkcollections={2}" -f $node.InputValueCapCount, $node.FeatureValueCapCount, $node.LinkCollectionCount)

    Write-Log '      input value caps:'
    if (@($node.InputCaps).Count -eq 0) { Write-Log '        (none)' }
    foreach ($c in $node.InputCaps) {
        Write-Log ("        {0} rid 0x{1:X2} col={2} bits={3} count={4}" -f (Format-UsagePair $c.UsagePage $c.Usage), $c.ReportId, $c.LinkCollection, $c.BitSize, $c.ReportCount)
    }
    Write-Log '      feature value caps:'
    if (@($node.FeatureCaps).Count -eq 0) { Write-Log '        (none)' }
    foreach ($c in $node.FeatureCaps) {
        $tag = ''
        if ($c.UsagePage -eq 0x0006 -and $c.Usage -eq 0x0020) { $tag = '   <- Generic Device battery strength' }
        Write-Log ("        {0} rid 0x{1:X2} col={2} bits={3} count={4}{5}" -f (Format-UsagePair $c.UsagePage $c.Usage), $c.ReportId, $c.LinkCollection, $c.BitSize, $c.ReportCount, $tag)
    }

    foreach ($r in $node.Channels) {
        if ($r.Ok) {
            Write-Log ("      {0,-9} OK   rid=0x{1:X2} declared={2} bytes=[{3}] percent={4}" -f $r.Channel, $r.ReportId, $r.Declared.ToString().ToLowerInvariant(), $r.Bytes, $r.Percent) 'Green'
        } elseif ($r.Reason -eq 'call_failed') {
            Write-Log ("      {0,-9} FAIL rid=0x{1:X2} declared={2} err={3}" -f $r.Channel, $r.ReportId, $r.Declared.ToString().ToLowerInvariant(), $r.Error) 'Yellow'
        } else {
            Write-Log ("      {0,-9} FAIL rid=0x{1:X2} declared={2} bytes=[{3}] reason={4}" -f $r.Channel, $r.ReportId, $r.Declared.ToString().ToLowerInvariant(), $r.Bytes, $r.Reason) 'Yellow'
        }
    }
}

# --- Which driver is bound --------------------------------------------------
# Same registry walk as diagnose-and-recover.ps1: the device Enum key, its
# Device Parameters subkey, the parent, then the class key. LowerFilters is what
# says PnP was TOLD to load a filter; the service snapshot says whether that
# filter exists at all. No new registry walking is invented here.
Write-Log ''
Write-Log '=== DRIVER BOUND PER APPLE MOUSE PID ===' 'Cyan'
$svc = Get-Service -Name $ApplePrefix -ErrorAction SilentlyContinue
if ($svc) {
    Write-Log ("  {0} service: present  status={1} start={2}" -f $ApplePrefix, $svc.Status, $svc.StartType)
} else {
    Write-Log ("  {0} service: absent (no Apple / Boot Camp filter installed)" -f $ApplePrefix)
}
$driverFile = Join-Path $env:SystemRoot 'System32\drivers\applewirelessmouse.sys'
Write-Log ("  applewirelessmouse.sys present: {0}" -f (Test-Path -LiteralPath $driverFile))

$allDev = @()
try {
    $allDev = @(Get-PnpDevice -ErrorAction Stop)
} catch {
    Write-Log "  Get-PnpDevice failed: $($_.Exception.Message)" 'Red'
}

function Get-LowerFilterName {
    param([string]$InstanceId)
    $lf = @()
    $enumPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\$InstanceId"
    try {
        # No Test-Path guard: it answers $false for a key this process may not
        # read exactly as it does for a key that is not there, and those two
        # facts mean opposite things here. Get-Item -ErrorAction Stop tells them
        # apart by the exception type.
        $k = Get-Item -LiteralPath $enumPath -ErrorAction Stop
        $v = $k.GetValue('LowerFilters')
        if ($v -is [string[]]) { $lf += $v } elseif ($v) { $lf += [string]$v }
        $dp = Join-Path $enumPath 'Device Parameters'
        if (Test-Path -LiteralPath $dp) {
            $v = (Get-Item -LiteralPath $dp).GetValue('LowerFilters')
            if ($v -is [string[]]) { $lf += $v } elseif ($v) { $lf += [string]$v }
        }
        $parentPath = Split-Path -Parent $enumPath
        if ($parentPath -and (Test-Path -LiteralPath $parentPath)) {
            $pv = (Get-Item -LiteralPath $parentPath).GetValue('LowerFilters')
            if ($pv -is [string[]]) { $lf += $pv } elseif ($pv) { $lf += [string]$pv }
        }
        $cls = [string]$k.GetValue('Driver', '')
        if ($cls) {
            $ck = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$cls"
            if (Test-Path -LiteralPath $ck) {
                $cv = (Get-Item -LiteralPath $ck).GetValue('LowerFilters')
                if ($cv -is [string[]]) { $lf += $cv } elseif ($cv) { $lf += [string]$cv }
            }
        }
    } catch [System.Management.Automation.ItemNotFoundException] {
        # Confirmed absent: the device has no Enum key, so it has no filters.
        Write-Verbose "No Enum key for ${InstanceId}: $($_.Exception.Message)"
        return @()
    } catch {
        # Anything else - access denied, no HKLM: drive, a broken hive - left the
        # list unknown. A typed catch above means this one still sees those.
        Write-Verbose "LowerFilters read failed for ${InstanceId}: $($_.Exception.Message)"
        return @($UnreadableFilterSentinel)
    }
    return @($lf | Select-Object -Unique)
}

# The whole point of the section: name the shape in words, so the report states
# plainly which of the two captures this is instead of leaving the reader to
# decode a filter name.
function Get-DriverShapeName {
    param([string[]]$Filters)
    foreach ($f in $Filters) {
        if ($f -and $f.StartsWith($ApplePrefix, [StringComparison]::OrdinalIgnoreCase)) { return 'APPLE / BOOT CAMP shape' }
    }
    foreach ($f in $Filters) {
        if ($f -and $f.StartsWith($KmdfPrefix, [StringComparison]::OrdinalIgnoreCase)) { return 'KMDF filter shape' }
    }
    # Last but one on purpose: the caller concatenates the filter lists of every
    # node behind one PID, so one unreadable node must not hide a filter another
    # node did report. A node that answered outranks a node that did not.
    foreach ($f in $Filters) {
        if ($f -ceq $UnreadableFilterSentinel) { return 'UNKNOWN shape - LowerFilters could not be read; re-run elevated to decide this line' }
    }
    return 'STOCK WINDOWS shape'
}

$mousePidRows = 0
foreach ($hex in $MousePids) {
    $needleA = "PID_$hex"
    $needleB = "PID&$hex"
    $devMatches = @($allDev | Where-Object {
        $_.InstanceId -and
        (Get-AppleVendorId ([string]$_.InstanceId)) -ne '' -and
        (([string]$_.InstanceId).ToLowerInvariant().Contains($needleA.ToLowerInvariant()) -or
         ([string]$_.InstanceId).ToLowerInvariant().Contains($needleB.ToLowerInvariant()))
    })
    if ($devMatches.Count -eq 0) { continue }
    $mousePidRows++
    $filters = @()
    foreach ($d in $devMatches) { $filters += Get-LowerFilterName ([string]$d.InstanceId) }
    $filters = @($filters | Select-Object -Unique)
    $shown = if ($filters.Count -gt 0) { $filters -join ', ' } else { '(none)' }
    Write-Log ''
    Write-Log ("  PID {0}: {1} node(s)  LowerFilters={2}" -f $hex.ToUpperInvariant(), $devMatches.Count, $shown)
    Write-Log ("    {0}" -f (Get-DriverShapeName $filters))
    foreach ($d in $devMatches) {
        Write-Log ("    [{0}] {1}" -f [string]$d.Status, [string]$d.InstanceId)
    }
}
# An empty section and a section that was never reached read the same in a
# pasted report, so the absent case says so in words.
if ($mousePidRows -eq 0) {
    Write-Log '  No Apple mouse PID from the catalog is present on this machine.' 'Yellow'
}

# --- Save -------------------------------------------------------------------
$outDir = Join-Path $env:APPDATA 'MagicMouseTray'
if (-not (Test-Path -LiteralPath $outDir -PathType Container)) {
    New-Item -ItemType Directory -Path $outDir -Force -ErrorAction Stop | Out-Null
}
$outFile = Join-Path $outDir ("hid-report-{0}.txt" -f $stamp.ToString('yyyyMMdd-HHmmss'))

Write-Log ''
Write-Log '=== RUN THIS ONCE PER DRIVER ===' 'Cyan'
# Issue #124, comment 6266748000 (2026-10-19): on a PID 0269 Magic Mouse,
# getting scroll back after a driver switch required RE-PAIRING the mouse, and
# the re-pair "makes magicmousetray inoperable". The two-capture advice below is
# only free if that cost is stated first, so it is stated first.
Write-Log '  WARNING: switching drivers can cost you the pairing. On issue #124 (comment' 'Yellow'
Write-Log '  6266748000, 2026-10-19) a PID 0269 Magic Mouse needed a full RE-PAIR to get' 'Yellow'
Write-Log '  scroll back after a driver switch, and that re-pair left Magic Tray' 'Yellow'
Write-Log '  inoperable. Capture the driver you are on now first, and switch only if you' 'Yellow'
Write-Log '  are willing to re-pair the mouse.' 'Yellow'
Write-Log ''
Write-Log '  If you can switch this mouse between drivers (the tray menu Driver item),'
Write-Log '  run this script once on EACH driver you can try and attach every report.'
Write-Log '  The difference between the captures is the whole answer: the collections'
Write-Log '  Windows exposes, and the battery channel that answers, both change with'
Write-Log '  the driver. One capture shows a state; two show the cause.'
Write-Log ''
Write-Log 'Safe to paste into a GitHub issue: every line above has had the Bluetooth' 'Green'
Write-Log 'address replaced with <mac> and the USB serial number replaced with <serial>.' 'Green'

$script:Lines | Set-Content -LiteralPath $outFile -Encoding UTF8 -ErrorAction Stop
Write-Host ''
Write-Host ("Saved: {0}" -f $outFile) -ForegroundColor Green
Write-Host 'Attach that file to your GitHub issue at https://github.com/LesleyMurfin/magic-tray/issues'
exit 0
