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
// and 0..100 once it is there.
//
// What the script does, byte for byte (Edit-Blob in
// kbd-patch-cachedservices.ps1):
//   * finds the RID marker `85 47`;
//   * finds the COL02 close `81 02 C0 C0` starting within the following 96
//     bytes;
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
    // A record contains the insertion marker; for a whole cache, at least one
    // record does.
    Applied,

    // A record carries the untouched COL02 close and no marker; for a whole
    // cache, no record carries the marker and at least one still carries the
    // close.
    NotApplied,

    // No MAC, no readable subtree, no candidate record, or a candidate record
    // whose shape matches neither fingerprint. No evidence - not a fault.
    Unknown,
}

// What one device's cache holds, counted instead of folded, because the two
// questions asked of it do not have the same answer. A report asks whether the
// unlock is in the cache; the gate on the elevated run asks whether anything
// is left to do, and that includes a candidate record neither fingerprint
// decides - the script counts those as candidates too and blocks on them.
// Candidates - Patched - Stock is how many are in neither shape.
internal readonly record struct SdpCacheCensus(int Candidates, int Patched, int Stock);

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

    // How far past `85 47` a close still belongs to the COL02 battery
    // collection, as a last candidate START index: the script's own
    // Find-ByteSequence -End, verbatim, so a close whose first byte is
    // anywhere in [rid+2, rid+2+96] is accepted by both halves. Beyond the
    // window the byte pair is some other collection's business and proves
    // nothing about the battery report - deliberately Unknown, not a guess in
    // either direction. Live delta is 281-258 = 23 bytes.
    const int RidWindowBytes = 96;

    // The script inserts the marker at closeOffset + 2 (between `81 02` and
    // `C0 C0`), so the furthest marker the script can write starts two bytes
    // past the furthest close it can accept.
    const int MarkerOffsetFromClose = 2;

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
    internal static SdpPatchState Classify(byte[]? record) => Classify(record, out _, out _);

    // markerOffset is the absolute offset of the patch marker when the answer
    // is Applied, and -1 otherwise; it exists for the log line. candidate says
    // the blob carried `85 47` at all - which Applied and NotApplied imply but
    // Unknown does not, and that is the point: an Unknown CANDIDATE is a
    // battery record in a shape this reader cannot decide, which the script
    // counts as work it could not finish. Both exist for a caller that has to
    // count records rather than name one.
    internal static SdpPatchState Classify(byte[]? record, out int markerOffset, out bool candidate)
    {
        markerOffset = -1;
        candidate = false;
        if (record is null)
            return SdpPatchState.Unknown;

        bool anyStockClose = false;

        // Two passes over the RID sites so that a patched site anywhere in the
        // blob outranks a stock close anywhere else in it.
        for (int i = 0; i + 1 < record.Length; i++)
        {
            if (record[i] != Rid47[0] || record[i + 1] != Rid47[1])
                continue;
            candidate = true;

            int at = IndexOf(
                record, PatchMarker, i + 2, i + 2 + RidWindowBytes + MarkerOffsetFromClose);
            if (at >= 0)
            {
                markerOffset = at;
                return SdpPatchState.Applied;
            }

            if (IndexOf(record, StockClose, i + 2, i + 2 + RidWindowBytes) >= 0)
                anyStockClose = true;
        }

        return anyStockClose ? SdpPatchState.NotApplied : SdpPatchState.Unknown;
    }

    // Registry half. Never writes, never needs elevation, never throws. Both
    // cache subkeys are enumerated into one census of this device's cache; an
    // empty census is what "nothing readable here" looks like.
    internal static SdpCacheCensus CensusForMac(string? mac)
    {
        var normalized = NormalizeMac(mac);
        if (normalized is null)
        {
            Log(mac is null || mac.Length == 0 ? "none" : "invalid", default, -1);
            return default;
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

                    var state = Classify(blob, out var at, out var candidate);
                    // Blobs without RID 0x47 are the non-HID service records
                    // (84/85 bytes live). They are not candidates and do not
                    // vote. A candidate in a shape neither fingerprint decides
                    // is counted and stays in neither column - the script sees
                    // the same record, blocks on it and dumps its bytes.
                    if (!candidate)
                        continue;

                    candidates++;
                    if (state == SdpPatchState.Applied)
                    {
                        patched++;
                        if (markerOffset < 0)
                            markerOffset = at;
                    }
                    else if (state == SdpPatchState.NotApplied)
                    {
                        stock++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // SecurityException on a hardened hive, IOException on a key deleted
            // mid-enumeration (a re-pair does exactly that), anything else the
            // registry can raise: we learned nothing. Not a fault.
            Logger.Log($"SDP_PATCH_FAILED mac={normalized} err={ex.Message}");
            return default;
        }

        var census = new SdpCacheCensus(candidates, patched, stock);
        Log(normalized, census, markerOffset);
        return census;
    }

    internal static SdpPatchState ForMac(string? mac) => CacheVerdict(CensusForMac(mac));

    // The verdict the STATUS consumers read - DriverAdvisor's battery sentence
    // and DeviceCapability's row. A report answers "is the unlock in this
    // cache", and `09 20 b1 02` beside RID 0x47 is positive evidence of it with
    // exactly one writer, so one patched record speaks for the cache: where
    // only some records carry it, hidbth may well be parsing one that does and
    // showing a percent, and a sentence calling the unlock absent would
    // contradict the number beside it. A stock record wins only when nothing is
    // patched, which is the same precedence Classify uses inside one blob.
    // "Is there still work for the script" is a different question with a
    // different answer and is asked elsewhere, on the census itself:
    // DriverInstaller.SkipsKeyboardSdpPatch.
    internal static SdpPatchState CacheVerdict(SdpCacheCensus cache) =>
        cache.Patched > 0 ? SdpPatchState.Applied
        : cache.Stock > 0 ? SdpPatchState.NotApplied
        : SdpPatchState.Unknown;

    // Resolves the MAC from the live BTHENUM instance for this PID, then asks
    // ForMac. The MAC lives in the instance-id tail
    // (...&E806884B0741_C00000000) and DriverInstaller.ParseMacFromInstance is
    // the repo's one parser for it - reused verbatim here rather than copied.
    // The PID matcher is DeviceSnapshotReader.BthenumKeyMatchesPid, the repo's
    // single BTHENUM convention (DeviceStackReader.cs:149,
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
    // the -Mac parameter of kbd-patch-cachedservices.ps1 applies to its
    // argument, so the key this reads is exactly the key that would be written.
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

    // `lastStart` is the last index the needle may START at, inclusive - the
    // convention Find-ByteSequence's -End uses - clamped to the record so a
    // short blob is read, never over-read.
    static int IndexOf(byte[] haystack, byte[] needle, int start, int lastStart)
    {
        int last = Math.Min(lastStart, haystack.Length - needle.Length);
        for (int i = Math.Max(0, start); i <= last; i++)
        {
            int k = 0;
            while (k < needle.Length && haystack[i + k] == needle[k])
                k++;
            if (k == needle.Length)
                return i;
        }
        return -1;
    }

    // One line per query, the shape DeviceStackReader.cs:94-95 established.
    // values = candidate records only (blobs carrying RID 0x47), decided or
    // not; the patched/stock split is printed because patched + stock short of
    // values is the cache holding a record this reader cannot decide, and
    // patched short of values is the half-done cache the gate refuses to skip.
    static void Log(string mac, SdpCacheCensus cache, int markerOffset)
    {
        Logger.Log($"SDP_PATCH mac={mac} state={Describe(CacheVerdict(cache))} "
            + $"values={cache.Candidates} patched={cache.Patched} stock={cache.Stock} "
            + $"marker_at={markerOffset}");
    }

    internal static string Describe(SdpPatchState state) => state switch
    {
        SdpPatchState.Applied => "applied",
        SdpPatchState.NotApplied => "not_applied",
        _ => "unknown",
    };
}
