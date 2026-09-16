// SPDX-License-Identifier: MIT
using System.Globalization;
using Microsoft.Win32;

namespace MagicMouseTray;

// The one signal this repo had no way to see: ANOTHER INSTALLED DRIVER PACKAGE
// ALSO CLAIMS THIS MOUSE, and could win the next rescan. That is the mechanism
// behind "my scroll was fine, then Windows put me back on the wrong driver" -
// nothing broke, PnP simply re-ranked a device that several packages match and
// bound a different one.
//
// MEASURED ON THE REFERENCE PC, READ-ONLY, NON-ELEVATED (2026-09-16/09-15).
// FOUR installed packages declare the v3 hardware id
// BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323, verbatim:
//
//   INF        Package                                    DriverVer    SignerName     SignerScore
//   oem50.inf  MagicMouseDriver-kmdf-204-scroll.inf 2.0.4.3  09/15/2026  MagicMouseFix  0x0F000000  <- BOUND NOW
//   oem26.inf  MagicMouseDriver.inf 23.14.8.22           08/30/2026  MagicMouseFix  0x0F000000
//   oem16.inf  MagicMouseDriver.inf 4.47.14.717          04/27/2026  (empty)        0x80000000 unsigned
//   oem8.inf   Apple AppleWirelessMouse 6.2.0.0          04/21/2026  MagicMouseFix  0x0F000000
//
// THREE OF THE FOUR SHARE THE SAME LOCAL PUBLISHER (MagicMouseFix), so signer
// trust gives NO DIFFERENTIAL here and this file never ranks on it - the score
// is carried through for the log and for the caller, not used as a comparator.
// Only oem8.inf claims 030D (the v1), so the v1 has no rival today.
//
// WHAT THE RANKING ACTUALLY DOES, also measured:
//   - Both live BTHENUM parents are bound at DEVPKEY_Device_DriverRank
//     16711680 = 0x00FF0000, the BEST exact-hardware-id rank. Every one of the
//     four claimants matches by EXACT hardware id at list index 0, so they all
//     TIE on rank. Rank cannot pick a winner here.
//   - The tie is broken by DriverVer DATE first, then version. oem50 wins today
//     only because it is the newest-dated, not because it is signed or newer in
//     version number (oem26 is version 23.14.8.22 against oem50's 2.0.4.3).
//
// READ-ONLY AND NON-ELEVATED BY CONSTRUCTION: registry reads with
// writable:false, no process spawn, no pnputil, no devnode restart, no write of
// any kind. Measured cost of the DeviceIds lookup with a plain user token:
// 12.7 ms. Deliberately NOT memoized - one read per caller Check is cheap, and
// a cached answer would keep advising about a rival package the user has since
// removed.
//
// HONESTY RULES, same as DeviceDiagReader / SystemConfigChecker:
//   - unreadable = null = NO EVIDENCE, never a fault;
//   - a rival claimant is NOT a problem in itself. Millions of PCs carry
//     several packages for one device and never rebind. This signal is at most
//     ADVISORY and can never reach Blocking;
//   - a malformed reading yields null for that field, never a guessed value.
internal sealed record DriverClaim(
    string InfName,        // oemNN.inf
    string? Provider,
    string? SignerName,
    uint? SignerScore,     // 0x0F000000 Authenticode, 0x80000000 unsigned, null unknown
    DateTime? DriverDate,  // from the Version blob
    Version? DriverVersion,
    bool IsBound);         // matches the device's current DEVPKEY_Device_DriverInfPath

internal static class DriverClaimReader
{
    const string DeviceIdsBase = @"SYSTEM\DriverDatabase\DeviceIds";
    const string DriverPackagesBase = @"SYSTEM\DriverDatabase\DriverPackages";
    const string DriverInfFilesBase = @"SYSTEM\DriverDatabase\DriverInfFiles";
    const string ClassBase = @"SYSTEM\CurrentControlSet\Control\Class";

    // One package's raw registry values, nothing interpreted. SignerScore is
    // int because that is what a REG_DWORD marshals to: the measured unsigned
    // 0x80000000 arrives as -2147483648 and is widened, not clamped.
    internal readonly record struct PackageReading(
        string? Provider, string? SignerName, int? SignerScore, byte[]? VersionBlob);

    // Everything one query read out of HKLM\SYSTEM\DriverDatabase, before any
    // interpretation. This is the seam that keeps the join testable: the
    // registry walk fills it in, Join() is pure.
    //
    // DeviceIdsReadable false means the DriverDatabase hive itself did not open
    // -> no evidence -> null. True with an empty InfNames means the database
    // read fine and NOTHING claims this hardware id -> [].
    internal readonly record struct DatabaseReading(
        bool DeviceIdsReadable,
        IReadOnlyList<string> InfNames,
        IReadOnlyDictionary<string, PackageReading> Packages);

    // null = could not read (no evidence). Empty list = read fine, nothing
    // claims it. Never throws.
    internal static IReadOnlyList<DriverClaim>? ClaimsFor(string hardwareId, string? boundInfName)
    {
        if (!IsSafeHardwareId(hardwareId))
            return null;

        IReadOnlyList<DriverClaim>? claims;
        try
        {
            claims = Join(ReadDatabase(hardwareId), boundInfName);
        }
        catch (Exception ex)
        {
            // Registry security, a key deleted mid-walk, a hive we are not
            // allowed into. All of them mean we learned nothing.
            Logger.Log($"DRIVER_CLAIMS_FAILED hwid={hardwareId} err={ex.Message}");
            return null;
        }

        Logger.Log(Describe(hardwareId, claims, boundInfName));
        return claims;
    }

    // What SystemConfigChecker actually has to hand: a four-hex PID. Resolves
    // the live BTHENUM parent for that PID, takes its exact hardware id and its
    // bound oemNN.inf out of the registry, then delegates. null when no live
    // parent resolves - a PC that has never paired this mouse must produce no
    // evidence rather than an empty list that reads as "nothing claims it".
    //
    // The PID matcher is DeviceSnapshotReader.BthenumKeyMatchesPid, the single
    // BTHENUM convention this repo has (DeviceStackReader and DeviceDiagReader
    // use the same one). No second VID table lives here.
    internal static IReadOnlyList<DriverClaim>? ClaimsForPid(string? pid)
    {
        if (string.IsNullOrEmpty(pid))
            return null;

        int instances = 0;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                DeviceSnapshotReader.BtEnumBase, writable: false);
            if (root is not null)
            {
                foreach (var deviceKeyName in root.GetSubKeyNames())
                {
                    if (!DeviceSnapshotReader.BthenumKeyMatchesPid(deviceKeyName, pid))
                        continue;

                    using var deviceKey = root.OpenSubKey(deviceKeyName, writable: false);
                    if (deviceKey is null)
                        continue;

                    foreach (var instanceName in deviceKey.GetSubKeyNames())
                    {
                        using var instanceKey = deviceKey.OpenSubKey(instanceName, writable: false);
                        if (instanceKey is null)
                            continue;
                        instances++;

                        var hardwareId = ExactHardwareId(instanceKey, pid);
                        if (hardwareId is null)
                            continue;

                        return ClaimsFor(hardwareId, BoundInfName(instanceKey));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_CLAIMS_PID_FAILED pid={pid} err={ex.Message}");
            return null;
        }

        // Only logged when nothing resolved: on the success path ClaimsFor has
        // already written the one line for this query, carrying hwid and bound.
        Logger.Log($"DRIVER_CLAIMS_PID pid={pid} resolved=none instances={instances}");
        return null;
    }

    // THE ORDER PNP USES TO BREAK THE TIE AMONG EQUAL-RANK EXACT-ID MATCHES:
    // newest DriverVer DATE first, then higher version. Exposed so callers do
    // not re-derive it.
    //
    // THIS MODELS THE TIE-BREAK ONLY. It does NOT model rank: rank is what
    // separates an exact hardware-id match (0x00FF0000, what all four
    // claimants score here) from a compatible-id or generic match, and this
    // file never computes it. The order below is only meaningful because every
    // claimant of one hardware id under DeviceIds matches that id EXACTLY, so
    // they are all on the same rank by construction.
    //
    // AND AN EXPLICIT INSTALL BYPASSES RANKING ENTIRELY. Historical proof on
    // this exact instance, from C:\Windows\INF\setupapi.dev.20260509_231419.log:
    //   2026/03/18 11:37  oem53.inf (magicmouse.inf 3.1.5.3, WHQL)
    //   Status: Selected | Designated   Driver Rank 00FF0000
    // installed as "best driver" because it was DESIGNATED, i.e. the user
    // pointed at it. A Designated install wins whatever this ordering says, so
    // being first here is not a prediction that Windows will pick you.
    //
    // Claims with no date (or no version) sort LAST: an unreadable DriverVer
    // can never be proven to be the newest. Stable within equal date+version,
    // so registry enumeration order survives and the log line is reproducible.
    internal static IReadOnlyList<DriverClaim> OrderByTieBreak(IReadOnlyList<DriverClaim> claims) =>
        claims
            .OrderByDescending(c => c.DriverDate)
            .ThenByDescending(c => c.DriverVersion)
            .ToList();

    // The pure half: raw readings in, claims out. null/[] tri-state lives here
    // rather than in the registry walk so it is testable without a hive.
    internal static IReadOnlyList<DriverClaim>? Join(in DatabaseReading reading, string? boundInfName)
    {
        if (!reading.DeviceIdsReadable)
            return null;

        var infNames = reading.InfNames;
        if (infNames is null || infNames.Count == 0)
            return Array.Empty<DriverClaim>();

        var claims = new List<DriverClaim>(infNames.Count);
        foreach (var infName in infNames)
        {
            if (string.IsNullOrEmpty(infName))
                continue;

            PackageReading package = default;
            reading.Packages?.TryGetValue(infName, out package);

            var (date, version) = DecodeVersionBlob(package.VersionBlob);
            claims.Add(new DriverClaim(
                InfName: infName,
                Provider: Blank(package.Provider),
                SignerName: Blank(package.SignerName),
                SignerScore: package.SignerScore is int score ? unchecked((uint)score) : null,
                DriverDate: date,
                DriverVersion: version,
                // Case-insensitive: the INF name is spelled oem50.inf under
                // DeviceIds and OEM50.INF in some setupapi lines.
                IsBound: boundInfName is not null
                    && string.Equals(infName, boundInfName, StringComparison.OrdinalIgnoreCase)));
        }

        return claims;
    }

    // The 48-byte DriverPackages Version blob. Layout verified BYTE-EXACT
    // against pnputil /enum-drivers output for all four packages above:
    //
    //   [0..7]   flags/type words (0x0009ff00 on every package measured)
    //   [8..23]  the package's class GUID
    //   [24..31] FILETIME, the DriverVer DATE (UTC midnight)
    //   [32..39] four UInt16 version words IN REVERSE ORDER:
    //            32=Revision 34=Build 36=Minor 38=Major
    //   [40..47] zero on every package measured
    //
    // e.g. oem50.inf: ...00c0aa26a544dd01 | 03 00 04 00 00 00 02 00
    //      -> 2026-09-15, words 3,4,0,2 reversed -> 2.0.4.3, exactly what
    //      pnputil prints.
    //
    // A missing, short or malformed blob yields null/null. Never throws, never
    // guesses. Length is checked against 40 - the last byte the fields we read
    // need - rather than exactly 48, so a future longer blob with the same
    // prefix still decodes, while a truncated one cannot.
    internal static (DateTime? Date, Version? Version) DecodeVersionBlob(byte[]? blob)
    {
        if (blob is null || blob.Length < 40)
            return (null, null);

        return (DecodeDate(blob), DecodeVersion(blob));
    }

    static DateTime? DecodeDate(byte[] blob)
    {
        long filetime = BitConverter.ToInt64(blob, 24);
        if (filetime <= 0)
            return null;

        DateTime date;
        try
        {
            date = DateTime.FromFileTimeUtc(filetime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        // Plausibility gate, not a guess: a DriverVer date outside this window
        // is a blob we misread, and a garbage year sorted as "newest" would
        // invent a rival that outranks the bound driver. The oldest real date
        // on the reference PC is hidbth.inf's 6-21-2006.
        return date.Year is >= 1990 and <= 2200 ? date : null;
    }

    static Version? DecodeVersion(byte[] blob)
    {
        int revision = BitConverter.ToUInt16(blob, 32);
        int build = BitConverter.ToUInt16(blob, 34);
        int minor = BitConverter.ToUInt16(blob, 36);
        int major = BitConverter.ToUInt16(blob, 38);

        // All four words zero is an INF that carried no version at all. Calling
        // that "0.0.0.0" would let it be COMPARED, and a comparison against a
        // version we never read is exactly the guess this file refuses.
        if ((major | minor | build | revision) == 0)
            return null;

        return new Version(major, minor, build, revision);
    }

    // Value NAMES under DeviceIds\<hardware id> are the claiming oemNN.inf
    // names; the value DATA is a 4-byte rank hint (01ff0000 on all four
    // measured) which this file deliberately does not interpret.
    static DatabaseReading ReadDatabase(string hardwareId)
    {
        using var deviceIds = Registry.LocalMachine.OpenSubKey(DeviceIdsBase, writable: false);
        if (deviceIds is null)
            return new DatabaseReading(false, Array.Empty<string>(), EmptyPackages);

        // Registry key lookup is case-insensitive, which is the whole reason
        // the hardware id can be passed in either spelling: the Enum hive
        // writes _VID&0001004c while DeviceIds is keyed _VID&0001004C.
        using var claimKey = deviceIds.OpenSubKey(hardwareId, writable: false);
        if (claimKey is null)
        {
            // The database opened and simply has no entry for this id. That is
            // [] - "nothing claims it" - not null.
            return new DatabaseReading(true, Array.Empty<string>(), EmptyPackages);
        }

        var infNames = claimKey.GetValueNames()
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        var packages = new Dictionary<string, PackageReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var infName in infNames)
        {
            if (packages.ContainsKey(infName))
                continue;
            var package = ReadPackage(infName);
            if (package is PackageReading reading)
                packages[infName] = reading;
        }

        return new DatabaseReading(true, infNames, packages);
    }

    static readonly IReadOnlyDictionary<string, PackageReading> EmptyPackages =
        new Dictionary<string, PackageReading>(StringComparer.OrdinalIgnoreCase);

    // oemNN.inf is NOT the DriverPackages subkey name. Measured mapping on the
    // reference PC:
    //   DriverInfFiles\oem50.inf  Active (REG_SZ)
    //     = magicmousedriver-kmdf-204-scroll.inf_amd64_98a8ba44bfbeebba
    //   DriverPackages\magicmousedriver-kmdf-204-scroll.inf_amd64_98a8ba44bfbeebba
    //     = Provider, InfName, SignerName, SignerScore, Version, ...
    // The default REG_MULTI_SZ under DriverInfFiles\oemNN.inf lists the same
    // package (plus any inactive ones), so it is the fallback when Active is
    // absent. null when neither resolves: we know the INF claims the device,
    // we just could not read its details.
    static PackageReading? ReadPackage(string infName)
    {
        if (!IsSafeInfName(infName))
            return null;

        using var infKey = Registry.LocalMachine.OpenSubKey(
            DriverInfFilesBase + "\\" + infName, writable: false);
        if (infKey is null)
            return null;

        var packageKeyName = infKey.GetValue("Active") as string;
        if (string.IsNullOrEmpty(packageKeyName))
            packageKeyName = (infKey.GetValue(null) as string[])?.FirstOrDefault();
        if (string.IsNullOrEmpty(packageKeyName) || !IsSafePackageKeyName(packageKeyName))
            return null;

        using var packageKey = Registry.LocalMachine.OpenSubKey(
            DriverPackagesBase + "\\" + packageKeyName, writable: false);
        if (packageKey is null)
            return null;

        return new PackageReading(
            Provider: packageKey.GetValue("Provider") as string,
            SignerName: packageKey.GetValue("SignerName") as string,
            SignerScore: packageKey.GetValue("SignerScore") as int?,
            VersionBlob: packageKey.GetValue("Version") as byte[]);
    }

    // The exact hardware id this instance is matched on: the entry of the
    // instance's HardwareID REG_MULTI_SZ that carries _PID&<pid>. The
    // _LOCALMFG& entry in the same list is the generic Bluetooth-manufacturer
    // id every profile of the radio shares, so it is never used - a claim on
    // _LOCALMFG& is not a claim on this mouse.
    static string? ExactHardwareId(RegistryKey instanceKey, string pid)
    {
        if (instanceKey.GetValue("HardwareID") is not string[] ids)
            return null;

        var needle = "_PID&" + pid;
        foreach (var id in ids)
        {
            if (!string.IsNullOrEmpty(id)
                && id.EndsWith(needle, StringComparison.OrdinalIgnoreCase)
                && IsSafeHardwareId(id))
                return id;
        }
        return null;
    }

    // The bound oemNN.inf, i.e. DEVPKEY_Device_DriverInfPath. That property is
    // backed by Control\Class\<class guid>\<NNNN>\InfPath, and the instance key
    // names the software key in its Driver value - measured on the reference PC:
    //   Enum\BTHENUM\{00001124-...}_VID&0001004c_PID&0323\...  Driver =
    //     {745a17a0-74d3-11d0-b6fe-00a0c90f57da}\0002
    //   Control\Class\{745a17a0-...}\0002  InfPath = oem50.inf
    // Read through the registry rather than CM_Get_DevNode_PropertyW so this
    // whole file stays registry-only: no P/Invoke, no devnode handle.
    // The instance key's own InfPath value is EMPTY on these BTHENUM parents,
    // which is why the software key is the one that gets read.
    // null when it cannot be resolved - then no claimant is marked bound, and
    // IsBound is false everywhere rather than guessed onto one of them.
    static string? BoundInfName(RegistryKey instanceKey)
    {
        if (instanceKey.GetValue("Driver") is not string driverRef
            || !IsSafeDriverRef(driverRef))
            return null;

        using var softwareKey = Registry.LocalMachine.OpenSubKey(
            ClassBase + "\\" + driverRef, writable: false);
        return Blank(softwareKey?.GetValue("InfPath") as string);
    }

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // DRIVER_CLAIMS hwid=<id> claims=4 bound=oem50.inf newest=oem50.inf
    //   rivals=oem26.inf@2026-08-30/23.14.8.22,...
    // One line per query, so the live log carries the signal with no UI at all.
    // Rivals are listed in tie-break order with the fields the tie-break uses,
    // which is what makes a later rebind readable after the fact.
    static string Describe(
        string hardwareId, IReadOnlyList<DriverClaim>? claims, string? boundInfName)
    {
        if (claims is null)
            return $"DRIVER_CLAIMS hwid={hardwareId} claims=unknown";

        var ordered = OrderByTieBreak(claims);
        var bound = claims.FirstOrDefault(c => c.IsBound)?.InfName
            ?? Blank(boundInfName) ?? "none";
        var newest = ordered.Count > 0 ? ordered[0].InfName : "none";
        var rivals = string.Join(",", ordered.Where(c => !c.IsBound).Select(Stamp));

        return $"DRIVER_CLAIMS hwid={hardwareId} claims={claims.Count} bound={bound} "
            + $"newest={newest} rivals={(rivals.Length == 0 ? "none" : rivals)}";
    }

    static string Stamp(DriverClaim claim) =>
        claim.InfName
        + "@" + (claim.DriverDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "nodate")
        + "/" + (claim.DriverVersion?.ToString() ?? "nover");

    // Three gates before any registry-sourced or caller-sourced string is
    // concatenated into a key path, same discipline as
    // DeviceDiagReader.IsSafeFamilyServiceName.
    //
    // A hardware id legitimately contains ONE backslash (the enumerator prefix,
    // "BTHENUM\{...}"), braces, ampersands and hyphens, so the gate allows
    // exactly that shape and rejects anything that could climb out of the key.
    static bool IsSafeHardwareId(string? hardwareId)
    {
        if (string.IsNullOrEmpty(hardwareId) || hardwareId.Length > 256)
            return false;
        if (hardwareId[0] == '\\' || hardwareId[^1] == '\\')
            return false;
        if (hardwareId.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (var c in hardwareId)
        {
            if (char.IsAsciiLetterOrDigit(c))
                continue;
            if (c is '\\' or '{' or '}' or '-' or '_' or '&' or '.')
                continue;
            return false;
        }
        return true;
    }

    // oemNN.inf: no separators at all.
    static bool IsSafeName(string? name, int maxLength)
    {
        if (string.IsNullOrEmpty(name) || name.Length > maxLength)
            return false;
        if (name.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
                return false;
        }
        return true;
    }

    static bool IsSafeInfName(string? infName) => IsSafeName(infName, maxLength: 64);

    // <inf name>_<arch>_<hash>, e.g.
    // magicmousedriver-kmdf-204-scroll.inf_amd64_98a8ba44bfbeebba.
    static bool IsSafePackageKeyName(string? name) => IsSafeName(name, maxLength: 160);

    // {class guid}\NNNN - exactly one backslash, nothing else path-like.
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
            if (c is '\\' or '{' or '}' or '-')
                continue;
            return false;
        }
        return true;
    }
}
