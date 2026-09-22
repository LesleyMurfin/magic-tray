// SPDX-License-Identifier: MIT
using Microsoft.Win32;

namespace MagicMouseTray;

// Answers one question, read-only: is THIS project's SDP-cache patch present in
// the Bluetooth stack's cached HID Report Descriptor for a given device?
//
// Why the question exists. An Apple Wireless/Magic Keyboard declares RID 0x47
// ("Battery Strength") as Input-only, so it is unreadable in practice
// (KeyboardBatteryDevice.cs:6-22). scripts/kbd-patch-cachedservices.ps1 inserts
// the four bytes 09 20 B1 02 - HID Report Size/Count + Feature(Var,Abs) - at the
// COL02 close inside the descriptor blob that BTHPORT caches, which makes col02
// expose a Feature ValueCap so HidD_GetFeature(0x47) returns the percent.
// KeyboardBatteryDevice.GetBatteryPercent returns -2 while that cap is missing
// and 0..100 once it is there. Until now nothing ever READ BACK whether the
// patch landed, so the tray could only offer it, never report it.
//
// What the script does, byte for byte (kbd-patch-cachedservices.ps1:45-122):
//   * finds the RID marker `85 47`;
//   * finds the COL02 close `81 02 C0 C0` within the following 64 bytes;
//   * inserts `09 20 B1 02` BETWEEN the `81 02` and the `C0 C0`, i.e. at
//     closeOffset + 2, and bumps four SDP length fields by 4.
// So a patched record contains `09 20 B1 02` just after the RID and, at that
// same site, NO LONGER contains `81 02 C0 C0`. That asymmetry is the whole test:
// `09 20 B1 02` next to RID 0x47 is a fingerprint no stock Apple descriptor
// carries, and this repo's script is its only writer.
//
// LIVE EVIDENCE - reference PC, 2026-09-16, read at Medium IL, NOT elevated
// (whoami /groups -> "Mandatory Label\Medium Mandatory Level",
//  WindowsPrincipal.IsInRole(Administrator) -> False):
//   Keyboard MAC e806884b0741, key
//   HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\e806884b0741
//     CachedServices\00010000         458 bytes  85 47 @258  09 20 B1 02 @281  81 02 C0 C0: none
//     CachedServices\00010001          84 bytes  no 85 47 - not a candidate record
//     DynamicCachedServices\00010000  458 bytes  85 47 @258  09 20 B1 02 @281  81 02 C0 C0: none
//     DynamicCachedServices\00010001   85 bytes  no 85 47 - not a candidate record
//   Verdict: Applied (2 candidate records, both patched, zero stock closes).
// Elevation is genuinely not needed: the ACL on that subtree grants
// BUILTIN\Users ReadKey (measured alongside the values above), so Applied,
// NotApplied and Unknown are all distinguishable from a normal user token.
//
// NotApplied IS NOT A FAULT. Re-pairing the keyboard makes Windows rewrite
// CachedServices from the device's own SDP response, which erases the insertion;
// the stock state is the factory state and the correct thing to report is simply
// "the patch is not there, so the percent cannot be read", never an error.
// Unknown is likewise no evidence at all - never a fault, never a claim.
//
// Trackpads and mice never come here: they read battery from HID Input report
// 0x90 with nothing installed (MouseBatteryDevice), so there is no patch to
// look for. This reader only has an answer for the keyboards whose descriptor
// carries RID 0x47.
internal enum SdpPatchState
{
    // A candidate record (one carrying RID 0x47) contains the insertion marker.
    Applied,

    // A candidate record carries the untouched COL02 close and no marker.
    NotApplied,

    // No MAC, no readable subtree, no candidate record, or a candidate record
    // whose shape matches neither fingerprint. No evidence - not a fault.
    Unknown,
}

internal static class SdpPatchReader
{
    const string BthPortDevices =
        @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";

    static readonly string[] CacheSubkeys = ["CachedServices", "DynamicCachedServices"];

    // HID Report ID 0x47 - the Battery Strength report this whole patch exists
    // to expose. Its presence is what makes a blob a CANDIDATE record; a blob
    // without it (the 84/85-byte service records seen live) is not evidence of
    // anything and must not vote.
    static readonly byte[] Rid47 = [0x85, 0x47];

    // The insertion: Report Size(8)/Report Count(1) is already present ahead of
    // it, this adds `09 20` (Usage 0x20) + `B1 02` (Feature Var,Abs).
    static readonly byte[] PatchMarker = [0x09, 0x20, 0xB1, 0x02];

    // The stock COL02 close the script splits open: Input(Var,Abs) then two
    // End Collection bytes.
    static readonly byte[] StockClose = [0x81, 0x02, 0xC0, 0xC0];

    // How far past `85 47` a marker still belongs to the COL02 battery
    // collection. The script itself only searches closeOffset in
    // [rid+2, rid+64) and then inserts at closeOffset+2, so anything the script
    // could have written lands below rid+70; 96 is that bound with slack.
    // Beyond the window the byte pair is some other collection's business and
    // proves nothing about the battery report - deliberately Unknown, not a
    // guess in either direction. Live delta is 281-258 = 23 bytes.
    const int RidWindowBytes = 96;

    // Shortest blob that could carry `85 47` plus a four-byte fingerprint.
    const int MinCandidateBytes = 6;

    // Pure half - the whole decision, no registry. Decision table:
    //
    //   no `85 47` anywhere ................................ Unknown
    //   `85 47` + `09 20 B1 02` in window .................. Applied
    //   `85 47` + `81 02 C0 C0` in window, no marker ....... NotApplied
    //   `85 47`, neither fingerprint in window ............. Unknown
    //   both fingerprints in window ........................ Applied
    //
    // The last row is a deliberate choice. `09 20 B1 02` beside RID 0x47 has
    // exactly one writer - this repo's script - while `81 02 C0 C0` is an
    // ordinary Input+EndCollection sequence a multi-collection descriptor can
    // repeat. A record carrying both is a patched COL02 plus some other
    // collection's close, so the marker wins: it is positive evidence, and the
    // honesty rule only forbids claiming Applied WITHOUT the marker.
    // Set-ItemProperty replaces a value whole, so there is no torn half-write
    // to protect against inside a single blob.
    internal static SdpPatchState Classify(byte[]? record) => Classify(record, out _);

    // markerOffset is the absolute offset of the patch marker when the answer is
    // Applied, and -1 otherwise. It exists for the log line.
    internal static SdpPatchState Classify(byte[]? record, out int markerOffset)
    {
        markerOffset = -1;
        if (record is null || record.Length < MinCandidateBytes)
            return SdpPatchState.Unknown;

        bool anyCandidate = false;
        bool anyStockClose = false;

        // Two passes over the RID sites so that a patched site anywhere in the
        // blob outranks a stock close anywhere else in it.
        for (int i = 0; i + 1 < record.Length; i++)
        {
            if (record[i] != Rid47[0] || record[i + 1] != Rid47[1])
                continue;
            anyCandidate = true;

            int windowEnd = Math.Min(record.Length, i + 2 + RidWindowBytes);
            int at = IndexOf(record, PatchMarker, i + 2, windowEnd);
            if (at >= 0)
            {
                markerOffset = at;
                return SdpPatchState.Applied;
            }

            if (IndexOf(record, StockClose, i + 2, windowEnd) >= 0)
                anyStockClose = true;
        }

        if (!anyCandidate)
            return SdpPatchState.Unknown;

        return anyStockClose ? SdpPatchState.NotApplied : SdpPatchState.Unknown;
    }

    // Registry half. Never writes, never needs elevation, never throws.
    //
    // Both cache subkeys vote and a single Applied wins, which is what the
    // script's own loop implies - it patches every RID-0x47 record it finds in
    // both subkeys, so a record still carrying the marker means the insertion is
    // present in the cache hidbth reparses on the next BT toggle. A mixed
    // outcome (one patched, one stock - possible after a re-pair rewrites only
    // one cache) still reports Applied, and the log line prints both counts so
    // the mix is visible rather than silently folded away.
    internal static SdpPatchState ForMac(string? mac)
    {
        var normalized = NormalizeMac(mac);
        if (normalized is null)
        {
            Log(mac is null || mac.Length == 0 ? "none" : "invalid", SdpPatchState.Unknown, 0, 0, 0, -1);
            return SdpPatchState.Unknown;
        }

        int candidates = 0;
        int patched = 0;
        int stock = 0;
        int markerOffset = -1;

        try
        {
            foreach (var subkeyName in CacheSubkeys)
            {
                using var cache = Registry.LocalMachine.OpenSubKey(
                    $@"{BthPortDevices}\{normalized}\{subkeyName}", writable: false);
                if (cache is null)
                    continue;

                foreach (var valueName in cache.GetValueNames())
                {
                    if (cache.GetValueKind(valueName) != RegistryValueKind.Binary)
                        continue;
                    if (cache.GetValue(valueName) is not byte[] blob)
                        continue;

                    var state = Classify(blob, out var at);
                    if (state == SdpPatchState.Applied)
                    {
                        candidates++;
                        patched++;
                        if (markerOffset < 0)
                            markerOffset = at;
                    }
                    else if (state == SdpPatchState.NotApplied)
                    {
                        candidates++;
                        stock++;
                    }
                    // Unknown blobs are the non-HID service records (84/85 bytes
                    // live). They are not candidates and do not vote.
                }
            }
        }
        catch (Exception ex)
        {
            // SecurityException on a hardened hive, IOException on a key deleted
            // mid-enumeration (a re-pair does exactly that), anything else the
            // registry can raise: we learned nothing. Not a fault.
            Logger.Log($"SDP_PATCH_FAILED mac={normalized} err={ex.Message}");
            return SdpPatchState.Unknown;
        }

        var verdict = patched > 0 ? SdpPatchState.Applied
            : stock > 0 ? SdpPatchState.NotApplied
            : SdpPatchState.Unknown;

        Log(normalized, verdict, candidates, patched, stock, markerOffset);
        return verdict;
    }

    // Resolves the MAC from the live BTHENUM instance for this PID, then asks
    // ForMac. The MAC lives in the instance-id tail
    // (...&E806884B0741_C00000000) and DriverInstaller.ParseMacFromInstance is
    // the repo's one parser for it - reused verbatim here rather than copied.
    // The PID matcher is DeviceSnapshotReader.BthenumKeyMatchesPid, the repo's
    // single BTHENUM convention (DeviceStackReader.ReadDeviceStack,
    // DriverClaimReader.cs:131).
    //
    // Deliberately PID-targeted: DriverInstaller.TryDiscoverKeyboardMac returns
    // the FIRST keyboard MAC it finds regardless of which keyboard asked, which
    // would hand back a neighbour's MAC on a PC with two keyboards paired. A PID
    // that resolves no live instance yields Unknown, never someone else's cache.
    internal static SdpPatchState ForPid(string? pid)
    {
        var mac = MacForPid(pid);
        if (mac is null)
        {
            Logger.Log($"SDP_PATCH pid={(string.IsNullOrEmpty(pid) ? "none" : pid.ToLowerInvariant())} "
                + "mac=none state=unknown values=0 marker_at=-1");
            return SdpPatchState.Unknown;
        }
        return ForMac(mac);
    }

    internal static string? MacForPid(string? pid)
    {
        if (string.IsNullOrEmpty(pid))
            return null;

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                DeviceSnapshotReader.BtEnumBase, writable: false);
            if (root is null)
                return null;

            foreach (var deviceKeyName in root.GetSubKeyNames())
            {
                if (!DeviceSnapshotReader.BthenumKeyMatchesPid(deviceKeyName, pid))
                    continue;

                using var deviceKey = root.OpenSubKey(deviceKeyName, writable: false);
                if (deviceKey is null)
                    continue;

                foreach (var instanceName in deviceKey.GetSubKeyNames())
                {
                    var mac = DriverInstaller.ParseMacFromInstance(instanceName);
                    if (mac is not null)
                        return mac;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SDP_PATCH_MAC_FAILED pid={pid} err={ex.Message}");
        }
        return null;
    }

    // 12 hex digits, lowercase, separators tolerated - the same normalization
    // the patch script applies to its -Mac argument
    // (kbd-patch-cachedservices.ps1:23-26), so the key this reads is exactly the
    // key that would be written.
    internal static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac))
            return null;

        Span<char> buffer = stackalloc char[12];
        int n = 0;
        foreach (var c in mac)
        {
            if (!Uri.IsHexDigit(c))
                continue;
            if (n == 12)
                return null;
            buffer[n++] = char.ToLowerInvariant(c);
        }
        return n == 12 ? new string(buffer) : null;
    }

    static int IndexOf(byte[] haystack, byte[] needle, int start, int end)
    {
        for (int i = Math.Max(0, start); i + needle.Length <= end; i++)
        {
            int k = 0;
            while (k < needle.Length && haystack[i + k] == needle[k])
                k++;
            if (k == needle.Length)
                return i;
        }
        return -1;
    }

    // One line per query, the shape DeviceStackReader's REPAIR_STACK logging established.
    // values = candidate records only (blobs carrying RID 0x47); the patched/
    // stock split is printed so a mixed cache is visible in the log.
    static void Log(string mac, SdpPatchState state, int values, int patched, int stock, int markerOffset)
    {
        Logger.Log($"SDP_PATCH mac={mac} state={Describe(state)} values={values} "
            + $"patched={patched} stock={stock} marker_at={markerOffset}");
    }

    internal static string Describe(SdpPatchState state) => state switch
    {
        SdpPatchState.Applied => "applied",
        SdpPatchState.NotApplied => "not_applied",
        _ => "unknown",
    };
}
