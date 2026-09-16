// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// DriverClaimReader.Join and DecodeVersionBlob are the whole "another installed
// package also claims this mouse" decision with the registry lifted out:
//   - DecodeVersionBlob turns the 48-byte DriverPackages Version blob into a
//     DriverVer date and version, and is driven here from the VERBATIM bytes
//     measured on the reference PC for all four claimants of the v3 hardware
//     id, so the fixture is the real byte layout and not a re-derivation of it;
//   - Join is the tri-state: DeviceIds unreadable is null, DeviceIds absent is
//     [], and IsBound is decided from the caller's bound INF name;
//   - OrderByTieBreak is the equal-rank tie-break PnP applies, newest date
//     first then higher version.
//
// The registry halves (ClaimsFor / ClaimsForPid) stay untested on purpose:
// they need a live HKLM\SYSTEM\DriverDatabase plus a paired Magic Mouse under
// Enum\BTHENUM, neither of which exists on a clean CI box, and faking the hive
// would only test the fake.
public class DriverClaimReaderTests
{
    // Verbatim REG_BINARY Version blobs read out of
    // HKLM\SYSTEM\DriverDatabase\DriverPackages\<package> on the reference PC,
    // 2026-09-16, non-elevated. Each decodes to exactly what
    // pnputil /enum-drivers prints for that package.
    const string Oem50Blob = // MagicMouseDriver-kmdf-204-scroll.inf, bound now
        "00ff090000000000a0175a74d374d011b6fe00a0c90f57da"
        + "00c0aa26a544dd010300040000000200" + "0000000000000000";
    const string Oem26Blob = // MagicMouseDriver.inf 23.14.8.22
        "00ff090000000000a0175a74d374d011b6fe00a0c90f57da"
        + "00c00e801238dd01160008000e001700" + "0000000000000000";
    const string Oem16Blob = // MagicMouseDriver.inf 4.47.14.717, unsigned
        "00ff090000000000a0175a74d374d011b6fe00a0c90f57da"
        + "00006ccad8d5dc01cd020e002f000400" + "0000000000000000";
    const string Oem8Blob = // Apple AppleWirelessMouse 6.2.0.0
        "00ff090000000000a0175a74d374d011b6fe00a0c90f57da"
        + "0080f1cb21d1dc010000000002000600" + "0000000000000000";

    const uint Authenticode = 0x0F000000;
    const uint Unsigned = 0x80000000;

    static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    static DriverClaimReader.PackageReading Package(
        string provider, string? signer, uint? score, string? hex) =>
        new(provider, signer, score is uint s ? unchecked((int)s) : null,
            hex is null ? null : Bytes(hex));

    static DriverClaimReader.DatabaseReading Reading(
        bool readable,
        IReadOnlyList<string> infNames,
        params (string Inf, DriverClaimReader.PackageReading Package)[] packages)
    {
        var map = new Dictionary<string, DriverClaimReader.PackageReading>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (inf, package) in packages)
            map[inf] = package;
        return new DriverClaimReader.DatabaseReading(readable, infNames, map);
    }

    // The measured four-claimant reality of the v3 hardware id
    // BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323.
    // Enumeration order is the order GetValueNames() returned it in.
    static DriverClaimReader.DatabaseReading ReferencePc() => Reading(
        readable: true,
        infNames: ["oem8.inf", "oem26.inf", "oem16.inf", "oem50.inf"],
        ("oem8.inf", Package("Apple Inc.", "MagicMouseFix", Authenticode, Oem8Blob)),
        ("oem26.inf", Package("Magic Mouse Driver", "MagicMouseFix", Authenticode, Oem26Blob)),
        ("oem16.inf", Package("Magic Mouse Driver", null, Unsigned, Oem16Blob)),
        ("oem50.inf", Package(
            "Magic Mouse Driver KMDF 204 scroll", "MagicMouseFix", Authenticode, Oem50Blob)));

    [Fact]
    public void DecodeVersionBlob_RealBytes_MatchesPnputil()
    {
        // FILETIME at offset 24, four UInt16 at 32..39 in REVERSE order. If
        // either offset or the reversal is wrong these all move together, so
        // four packages with four different dates and four different version
        // shapes is the check that pins the layout.
        AssertDecodes(Oem50Blob, 2026, 9, 15, new Version(2, 0, 4, 3));
        AssertDecodes(Oem26Blob, 2026, 8, 30, new Version(23, 14, 8, 22));
        AssertDecodes(Oem16Blob, 2026, 4, 27, new Version(4, 47, 14, 717));
        AssertDecodes(Oem8Blob, 2026, 4, 21, new Version(6, 2, 0, 0));
    }

    static void AssertDecodes(string hex, int year, int month, int day, Version version)
    {
        var decoded = DriverClaimReader.DecodeVersionBlob(Bytes(hex));

        Assert.Equal(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc), decoded.Date);
        Assert.Equal(version, decoded.Version);
    }

    [Theory]
    // Nothing to decode at all.
    [InlineData(null)]
    // Truncated before the FILETIME.
    [InlineData("00ff0900000000")]
    // Carries the date but is cut off inside the version words, i.e. 39 bytes:
    // the case a length check of ">= 32" would have accepted and then read
    // garbage version numbers out of.
    [InlineData("00ff090000000000a0175a74d374d011b6fe00a0c90f57da"
        + "00c0aa26a544dd0103000400000002")]
    public void DecodeVersionBlob_ShortOrMissing_IsNullAndDoesNotThrow(string? hex)
    {
        var decoded = DriverClaimReader.DecodeVersionBlob(hex is null ? null : Bytes(hex));

        Assert.Null(decoded.Date);
        Assert.Null(decoded.Version);
    }

    [Fact]
    public void DecodeVersionBlob_GarbageFiletime_IsNullDateNotAThrownException()
    {
        // 0xFFFFFFFFFFFFFFFF at offset 24 is what a half-written or
        // wrong-layout blob looks like. FromFileTimeUtc throws on it, and a
        // year-30828 date would sort as "newest" and invent a rival that
        // outranks the bound driver. Version is still readable, so it must
        // survive - one unreadable field must not discard the other.
        var blob = Bytes(Oem50Blob);
        for (int i = 24; i < 32; i++)
            blob[i] = 0xFF;

        var decoded = DriverClaimReader.DecodeVersionBlob(blob);

        Assert.Null(decoded.Date);
        Assert.Equal(new Version(2, 0, 4, 3), decoded.Version);
    }

    [Fact]
    public void DecodeVersionBlob_AllZeroVersionWords_IsNullNotZeroZeroZeroZero()
    {
        // An INF that carried no version at all. "0.0.0.0" would be COMPARED
        // by the tie-break; null keeps it out of the comparison.
        var blob = Bytes(Oem50Blob);
        for (int i = 32; i < 40; i++)
            blob[i] = 0x00;

        var decoded = DriverClaimReader.DecodeVersionBlob(blob);

        Assert.Equal(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc), decoded.Date);
        Assert.Null(decoded.Version);
    }

    [Fact]
    public void Join_ReferencePc_DecodesAllFourClaimantsAndMarksTheBoundOne()
    {
        var claims = DriverClaimReader.Join(ReferencePc(), "oem50.inf");

        Assert.NotNull(claims);
        Assert.Equal(4, claims.Count);

        var bound = Assert.Single(claims, c => c.IsBound);
        Assert.Equal("oem50.inf", bound.InfName);
        Assert.Equal("Magic Mouse Driver KMDF 204 scroll", bound.Provider);
        Assert.Equal("MagicMouseFix", bound.SignerName);
        Assert.Equal(Authenticode, bound.SignerScore);

        // The unsigned rival: SignerName is empty in the registry and must come
        // back as null (no evidence of a signer), while the score is the
        // measured 0x80000000 - which a REG_DWORD hands over as -2147483648 and
        // must NOT be clamped or dropped.
        var unsignedRival = Assert.Single(claims, c => c.InfName == "oem16.inf");
        Assert.Null(unsignedRival.SignerName);
        Assert.Equal(Unsigned, unsignedRival.SignerScore);
        Assert.False(unsignedRival.IsBound);
    }

    [Fact]
    public void OrderByTieBreak_ReferencePc_PutsTheBoundNewestDatedPackageFirst()
    {
        var claims = DriverClaimReader.Join(ReferencePc(), "oem50.inf");
        Assert.NotNull(claims);

        var ordered = DriverClaimReader.OrderByTieBreak(claims);

        // DATE decides, not version and not signature: oem26 is version
        // 23.14.8.22 against oem50's 2.0.4.3 and would win a version-first
        // sort, and three of the four share the MagicMouseFix publisher so
        // signer trust cannot separate them at all.
        Assert.Equal(
            new[] { "oem50.inf", "oem26.inf", "oem16.inf", "oem8.inf" },
            ordered.Select(c => c.InfName));
    }

    [Fact]
    public void OrderByTieBreak_SameDate_HigherVersionWinsAndEqualPairsKeepInputOrder()
    {
        var sameDay = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        var claims = new List<DriverClaim>
        {
            new("oem50.inf", null, null, null, sameDay, new Version(2, 0, 4, 3), true),
            new("oem26.inf", null, null, null, sameDay, new Version(23, 14, 8, 22), false),
            // Same date AND same version as the bound one: nothing separates
            // them, so registry enumeration order has to survive or the log
            // line would flip between identical reads.
            new("oem61.inf", null, null, null, sameDay, new Version(2, 0, 4, 3), false),
            // No date at all cannot be proven newest, so it sorts last however
            // high its version reads.
            new("oem70.inf", null, null, null, null, new Version(99, 0, 0, 0), false),
        };

        var ordered = DriverClaimReader.OrderByTieBreak(claims);

        Assert.Equal(
            new[] { "oem26.inf", "oem50.inf", "oem61.inf", "oem70.inf" },
            ordered.Select(c => c.InfName));
    }

    [Fact]
    public void Join_DeviceIdsKeyAbsent_IsEmptyListNotNull()
    {
        // The database read fine and nothing claims this hardware id. That is
        // positive evidence of no rival, so it must be distinguishable from
        // "we could not look".
        var claims = DriverClaimReader.Join(Reading(readable: true, infNames: []), "oem8.inf");

        Assert.NotNull(claims);
        Assert.Empty(claims);
    }

    [Fact]
    public void Join_DatabaseUnreadable_IsNull()
    {
        // Registry security, a stripped hive, a key deleted mid-walk: we
        // learned nothing. Never [] - that would read as "no rival exists".
        Assert.Null(DriverClaimReader.Join(
            Reading(readable: false, infNames: []), "oem50.inf"));

        // Even if a stale InfNames list came along with it, unreadable wins.
        Assert.Null(DriverClaimReader.Join(
            Reading(readable: false, infNames: ["oem50.inf"]), "oem50.inf"));
    }

    [Theory]
    // setupapi lines and INF sections spell the same file either way.
    [InlineData("oem50.inf")]
    [InlineData("OEM50.INF")]
    [InlineData("Oem50.Inf")]
    public void Join_BoundInfNameCasing_StillMarksTheSameClaimantBound(string boundInfName)
    {
        var claims = DriverClaimReader.Join(ReferencePc(), boundInfName);

        Assert.NotNull(claims);
        Assert.Equal("oem50.inf", Assert.Single(claims, c => c.IsBound).InfName);
    }

    [Fact]
    public void Join_BoundInfNameUnknown_MarksNothingBound()
    {
        // The bound INF could not be resolved off the devnode. Guessing one of
        // the four would be a fabricated "you are on the right driver".
        var claims = DriverClaimReader.Join(ReferencePc(), null);

        Assert.NotNull(claims);
        Assert.Equal(4, claims.Count);
        Assert.DoesNotContain(claims, c => c.IsBound);
    }

    [Fact]
    public void Join_ClaimantWithNoReadablePackage_StillReportsTheClaimWithNullFields()
    {
        // DeviceIds named oem99.inf but DriverInfFiles/DriverPackages did not
        // resolve. The claim itself is still a measured fact; only its details
        // are unknown.
        var claims = DriverClaimReader.Join(
            Reading(
                readable: true,
                infNames: ["oem50.inf", "oem99.inf"],
                ("oem50.inf", Package(
                    "Magic Mouse Driver KMDF 204 scroll", "MagicMouseFix",
                    Authenticode, Oem50Blob))),
            "oem50.inf");

        Assert.NotNull(claims);
        var orphan = Assert.Single(claims, c => c.InfName == "oem99.inf");
        Assert.Null(orphan.Provider);
        Assert.Null(orphan.SignerName);
        Assert.Null(orphan.SignerScore);
        Assert.Null(orphan.DriverDate);
        Assert.Null(orphan.DriverVersion);
        Assert.False(orphan.IsBound);
    }
}
