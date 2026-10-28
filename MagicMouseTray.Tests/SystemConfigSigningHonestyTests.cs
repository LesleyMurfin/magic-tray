// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// The three readings that feed SystemConfigChecker's signing gate, each driven
// through a pure seam so the machine stays outside: the signer set a driver
// file carries, the raw HVCI policy value, and the PE certificate table the
// signer set is decoded from. SystemConfigCheckerTests covers what Evaluate
// does with a verdict; this file covers how an HONEST verdict is produced, and
// what the facts are allowed to say when it could not be.
//
// The rule in SystemConfigChecker.cs:54-57 is the whole point: "Unknown (a read
// that failed, or a catalog-only file) may never claim a driver is self-signed
// and may never reach Blocking." Blocking on the signing facts tells the user
// to run bcdedit /set testsigning on and to turn Memory integrity off, so a
// verdict that is really "we could not read it" reaching Blocking is this
// program telling someone to weaken their machine on no evidence.
//
// The reference file for all of this is applewirelessmouse.sys as measured on
// the 2026-09-15 reference PC (SystemConfigChecker.cs:47-49): a self-issued WDK
// test signature FIRST, plus two CA-issued Microsoft WHCP signatures nested in
// the first one's unauthenticated attributes. Windows loads it with nothing
// switched off. Read completely it is NOT self-signed; read partially - one
// nested blob that will not decode, or a nesting depth past the collector's cap
// - it is UNKNOWN, and must never collapse to the self-issued primary alone.
//
// The PE fixtures below are built byte by byte in the test, the same way
// SdpPatchReaderTests drives its reader from bytes rather than from a file on
// disk: no signed .sys exists on a CI box, and a parser that is handed only
// well-formed input has not been tested at all.
public class SystemConfigSigningHonestyTests
{
    static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    static readonly DeviceDiagReader.F1WatcherState UnknownWatcher = new(null, null, null);

    static SystemConfigChecker.ConfigReadings Readings(
        bool? testSigning = null,
        bool? hvci = null,
        bool? package = null,
        bool? selfSigned = null) =>
        new(testSigning, hvci, package, UnknownWatcher, T0, selfSigned, null);

    static ConfigFact? Fact(IReadOnlyList<ConfigFact> facts, string id)
    {
        foreach (var f in facts)
            if (f.Id == id)
                return f;
        return null;
    }

    static IReadOnlyList<ConfigFact> Blocking(IReadOnlyList<ConfigFact> facts)
    {
        var list = new List<ConfigFact>();
        foreach (var f in facts)
            if (f.Severity == ConfigSeverity.Blocking)
                list.Add(f);
        return list;
    }

    // ---- the verdict over a signer set ------------------------------------

    const SystemConfigChecker.SignerTrust SelfIssued =
        SystemConfigChecker.SignerTrust.SelfIssued;
    const SystemConfigChecker.SignerTrust CaIssued =
        SystemConfigChecker.SignerTrust.CaIssued;
    const SystemConfigChecker.SignerTrust Unreadable =
        SystemConfigChecker.SignerTrust.Unreadable;

    // The reference file read completely: self-issued primary, two CA-issued
    // nested WHCP signatures. Windows has a chain to follow, so signing policy
    // is not what decides whether this file loads and the tray stays silent.
    [Fact]
    public void MeasuredAppleFilterSigners_WhenEverySignatureWasRead_AreNotSelfSigned()
    {
        Assert.False(
            SystemConfigChecker.SelfSignedVerdictFor([SelfIssued, CaIssued, CaIssued]));
    }

    // THE defect. Same file, same PC, one nested blob that would not decode (or
    // a nest deeper than the collector walks). The only certificate we actually
    // read is the self-issued WDK one - and reporting that as "self-signed"
    // turns a WHQL-signed Apple driver into a Blocking instruction to enable
    // test signing and switch Memory integrity off.
    [Fact]
    public void MeasuredAppleFilterSigners_WithANestedSignatureThatDidNotDecode_AreUnknown()
    {
        Assert.Null(
            SystemConfigChecker.SelfSignedVerdictFor([SelfIssued, Unreadable, Unreadable]));
    }

    [Fact]
    public void EverySignerSelfIssuedAndRead_IsSelfSigned()
    {
        // The v3 KMDF package: CN=MagicMouseFix, self-issued, the only
        // signature on the file. This is the positive reading the whole gate
        // exists for and it must keep working.
        Assert.True(SystemConfigChecker.SelfSignedVerdictFor([SelfIssued]));
        Assert.True(SystemConfigChecker.SelfSignedVerdictFor([SelfIssued, SelfIssued]));
    }

    [Fact]
    public void NoSignersAtAll_IsUnknown()
    {
        // A catalog-only driver such as stock HidBth.sys: nothing embedded to
        // read, so there is nothing to say about it either way.
        Assert.Null(SystemConfigChecker.SelfSignedVerdictFor([]));
    }

    [Fact]
    public void ACaIssuedSignerDecidesEvenAlongsideOneThatCouldNotBeRead()
    {
        // One readable CA-issued signature is positive evidence that Windows
        // has a chain to follow, whatever the unreadable one would have said:
        // Windows loads a file when ANY of its signatures is trusted.
        Assert.False(SystemConfigChecker.SelfSignedVerdictFor([CaIssued, Unreadable]));
        Assert.False(
            SystemConfigChecker.SelfSignedVerdictFor([SelfIssued, Unreadable, CaIssued]));
    }

    [Fact]
    public void AnySignerThatCouldNotBeRead_NeverYieldsASelfSignedClaim()
    {
        // Self-signed means EVERY signature on the file was issued by itself.
        // One signature we did not read is one we cannot say that about.
        SystemConfigChecker.SignerTrust[][] degraded =
        [
            [Unreadable],
            [Unreadable, Unreadable],
            [SelfIssued, Unreadable],
            [Unreadable, SelfIssued],
            [SelfIssued, SelfIssued, Unreadable],
            [SelfIssued, Unreadable, SelfIssued],
        ];

        foreach (var signers in degraded)
            Assert.Null(SystemConfigChecker.SelfSignedVerdictFor(signers));
    }

    [Fact]
    public void APkcs7BlobThatWillNotDecode_IsAnUnreadableSignerNotAnEmptySignerSet()
    {
        // The bug one level down: a decode failure that reports nothing at all
        // is indistinguishable from a signature that was read and found
        // self-issued, which is exactly how an unread nested blob disappears.
        byte[][] undecodable =
        [
            [],
            [0x00],
            [0x30, 0x82, 0xFF, 0xFF],
            [0xDE, 0xAD, 0xBE, 0xEF, 0xDE, 0xAD, 0xBE, 0xEF],
        ];

        foreach (var blob in undecodable)
        {
            var signers = SystemConfigChecker.SignerTrustIn(blob);
            Assert.Equal(Unreadable, Assert.Single(signers));
            Assert.Null(SystemConfigChecker.SelfSignedVerdictFor(signers));
        }
    }

    // ---- what a degraded verdict is allowed to do to the facts ------------

    [Fact]
    public void ADegradedSignatureReadNeverReachesBlocking()
    {
        // Worst-case machine for every other reading: test signing off, HVCI
        // on, package present. With a POSITIVE self-signed reading this is two
        // Blocking facts; with a verdict that only means "we could not finish
        // reading it" it must stay Advisory on every device and every status.
        var verdict = SystemConfigChecker.SelfSignedVerdictFor(
            [SelfIssued, Unreadable, Unreadable]);

        foreach (var status in new[]
        {
            DriverStatus.PatchedKmdf, DriverStatus.PathAPatched, DriverStatus.NotBound,
            DriverStatus.StockKmdf, DriverStatus.Ok, DriverStatus.NotInstalled,
        })
        {
            var readings = Readings(
                testSigning: false, hvci: true, package: true, selfSigned: verdict);

            var v3 = SystemConfigChecker.Evaluate(
                DeviceKind.MagicMouseV3, "0323", status, readings);
            Assert.Empty(Blocking(v3));

            var legacy = SystemConfigChecker.Evaluate(
                DeviceKind.MagicMouseV1, "030d", status, readings);
            Assert.Empty(Blocking(legacy));
        }
    }

    [Fact]
    public void ADegradedSignatureReadNeverCallsTheDriverSelfSigned()
    {
        // The wording is the other half of the same promise: the facts say out
        // loud that the signature could not be read, instead of asserting a
        // WHQL-signed Apple driver is self-signed.
        var verdict = SystemConfigChecker.SelfSignedVerdictFor(
            [SelfIssued, Unreadable, Unreadable]);

        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PathAPatched,
            Readings(testSigning: false, hvci: true, package: true, selfSigned: verdict));

        foreach (var id in new[]
        {
            SystemConfigChecker.TestModeFactId, SystemConfigChecker.MemoryIntegrityFactId,
        })
        {
            var fact = Fact(facts, id)!;
            Assert.Equal(ConfigSeverity.Advisory, fact.Severity);
            Assert.DoesNotContain("is self-signed", fact.Detail, StringComparison.Ordinal);
            Assert.Contains("could not read the signature", fact.Detail,
                StringComparison.Ordinal);
        }
    }

    // ---- HVCI: Enabled is whatever the policy writer put there -------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void AnIntegerEnabledValue_IsReadAsConfiguredOnOrOff(int value, bool expected)
    {
        Assert.Equal(expected, SystemConfigChecker.HvciFromRegistryValue(value));
        Assert.Equal(expected, SystemConfigChecker.HvciFromRegistryValue((long)value));
        Assert.Equal(expected, SystemConfigChecker.HvciFromRegistryValue((uint)value));
    }

    [Fact]
    public void AnAbsentEnabledValue_IsNotConfiguredOn()
    {
        // We looked and it is not there, the same reading a missing watcher
        // file gets: configured off, not unknown.
        Assert.False(SystemConfigChecker.HvciFromRegistryValue(null));
    }

    [Fact]
    public void AnEnabledValueOfAnUnexpectedType_IsUnknownAndNeverAnAllClear()
    {
        // Policy, MDM and hand-edited hives all write this value, and a REG_SZ
        // or REG_BINARY Enabled is data this tray has never measured. Calling
        // it "off" would print "Nothing here is blocking a self-signed driver"
        // on a PC whose Memory integrity may well be on.
        object[] unreadable =
        [
            "1",
            "0",
            "Enabled",
            new byte[] { 0x01, 0x00, 0x00, 0x00 },
            new[] { "1" },
            new object(),
        ];

        foreach (var value in unreadable)
            Assert.Null(SystemConfigChecker.HvciFromRegistryValue(value));
    }

    [Fact]
    public void TheMemoryIntegrityFactFromAnUnreadableEnabledValue_IsNotAnAllClear()
    {
        var facts = SystemConfigChecker.Evaluate(
            DeviceKind.MagicMouseV3, "0323", DriverStatus.PatchedKmdf,
            Readings(
                testSigning: true,
                hvci: SystemConfigChecker.HvciFromRegistryValue("1"),
                package: true,
                selfSigned: true));

        var hvci = Fact(facts, SystemConfigChecker.MemoryIntegrityFactId)!;
        Assert.Equal(ConfigSeverity.Advisory, hvci.Severity);
        Assert.Contains("unknown", hvci.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Memory integrity is off", hvci.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing here is blocking", hvci.Detail, StringComparison.Ordinal);
    }

    // ---- the PE certificate table -----------------------------------------
    //
    // A minimal but structurally real image:
    //
    //   0x00  "MZ"
    //   0x3C  e_lfanew -> 0x80
    //   0x80  "PE\0\0", then the 20-byte COFF header
    //   0x98  optional header: Magic at +0, NumberOfRvaAndSizes as its last
    //         DWORD (PE32 +92, PE32+ +108)
    //   0x98 + 96 or 112   the data directory table; entry 4 (offset 0x20 into
    //         it) is IMAGE_DIRECTORY_ENTRY_SECURITY: file offset, then size
    //   after 16 physical directory slots: the WIN_CERTIFICATE -
    //         dwLength (payload + its own 8-byte header), wRevision 0x0200,
    //         wCertificateType 0x0002 (PKCS#7), then the blob itself
    //
    // Sixteen slots are always PHYSICALLY present while NumberOfRvaAndSizes is
    // what the header DECLARES, because that is the hostile case: a loader that
    // never reads the count finds plausible-looking bytes where the security
    // directory would be and parses whatever is there.

    const int PeHeaderOffset = 0x80;
    const ushort Pe32 = 0x010B;
    const ushort Pe32Plus = 0x020B;
    const ushort Pkcs7 = 0x0002;
    const int SecuritySlot = 4;

    static readonly byte[] Payload =
        [0x30, 0x0A, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x02, 0x00];

    static void W16(byte[] image, int at, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at), value);

    static void W32(byte[] image, int at, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), value);

    static int FixedOptionalHeader(ushort magic) => magic == Pe32Plus ? 112 : 96;

    static int DirectoryTable(ushort magic) =>
        PeHeaderOffset + 24 + FixedOptionalHeader(magic);

    static int CertificateStart(ushort magic) => DirectoryTable(magic) + 16 * 8;

    static byte[] PeImage(
        ushort magic = Pe32Plus,
        uint dataDirectoryCount = 16,
        ushort certificateType = Pkcs7,
        byte[]? payload = null,
        int? lfanew = null,
        uint? declaredCertificateLength = null,
        uint? declaredDirectorySize = null,
        uint? certificateFileOffset = null,
        uint peSignature = 0x00004550)
    {
        payload ??= Payload;
        int fixedOptional = FixedOptionalHeader(magic);
        int directories = DirectoryTable(magic);
        int certificate = CertificateStart(magic);
        var image = new byte[certificate + 8 + payload.Length];

        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        W32(image, 0x3C, unchecked((uint)(lfanew ?? PeHeaderOffset)));

        W32(image, PeHeaderOffset, peSignature);
        W16(image, PeHeaderOffset + 4, 0x8664);                       // Machine
        // Agrees with the DECLARED directory count, so a header that says
        // "four directories" is internally consistent and the only thing
        // wrong with reading entry four is that it is not there.
        W16(image, PeHeaderOffset + 4 + 16,                           // SizeOfOptionalHeader
            (ushort)(fixedOptional + 8 * Math.Min(dataDirectoryCount, 16)));
        W16(image, PeHeaderOffset + 4 + 18, 0x0022);                  // Characteristics

        W16(image, PeHeaderOffset + 24, magic);
        W32(image, PeHeaderOffset + 24 + fixedOptional - 4, dataDirectoryCount);

        W32(image, directories + SecuritySlot * 8,
            certificateFileOffset ?? (uint)certificate);
        W32(image, directories + SecuritySlot * 8 + 4,
            declaredDirectorySize ?? (uint)(8 + payload.Length));

        W32(image, certificate, declaredCertificateLength ?? (uint)(8 + payload.Length));
        W16(image, certificate + 4, 0x0200);                          // WIN_CERT_REVISION_2_0
        W16(image, certificate + 6, certificateType);
        payload.CopyTo(image, certificate + 8);

        return image;
    }

    [Theory]
    [InlineData(Pe32Plus)]
    [InlineData(Pe32)]
    public void AWellFormedImage_YieldsExactlyThePkcs7Blob(ushort magic)
    {
        Assert.Equal(Payload, SystemConfigChecker.ReadCertificateTable(PeImage(magic: magic)));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public void AnImageWithNoSecurityDirectory_IsRejectedRatherThanMisParsed(uint count)
    {
        // NumberOfRvaAndSizes is how a PE says how many data directories it
        // actually has. Below five there is no IMAGE_DIRECTORY_ENTRY_SECURITY
        // at all, so whatever sits at that offset belongs to something else -
        // section headers, or whatever a hostile file put there. Reading it
        // anyway means the signer set, and then the Blocking signing advice,
        // come out of bytes that are not a signature.
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(dataDirectoryCount: count)));
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(magic: Pe32, dataDirectoryCount: count)));
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(16u)]
    public void AnImageThatDeclaresTheSecurityDirectory_IsStillRead(uint count)
    {
        Assert.Equal(Payload, SystemConfigChecker.ReadCertificateTable(
            PeImage(dataDirectoryCount: count)));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 23)]
    [InlineData(0x7FFFFFF0)]
    [InlineData(1 << 30)]
    [InlineData(-1)]
    [InlineData(-0x40)]
    [InlineData(0)]
    [InlineData(0x3C)]
    public void AHostileELfanew_IsRejectedWithoutOverflowingOrThrowing(int lfanew)
    {
        // e_lfanew comes out of the file, so every offset derived from it is
        // attacker-chosen. int.MaxValue is the one that matters: pe + 24 wraps
        // negative, so a bounds check done in int arithmetic passes and the
        // read goes wherever the wrapped value points.
        var image = PeImage(lfanew: lfanew);
        var error = Record.Exception(() => SystemConfigChecker.ReadCertificateTable(image));
        Assert.Null(error);
        Assert.Null(SystemConfigChecker.ReadCertificateTable(image));
    }

    [Fact]
    public void AnImageWithoutThePeSignature_IsRejected()
    {
        Assert.Null(SystemConfigChecker.ReadCertificateTable(PeImage(peSignature: 0x00005A4D)));
    }

    [Theory]
    [InlineData((ushort)0x0001)]   // WIN_CERT_TYPE_X509
    [InlineData((ushort)0x0003)]   // WIN_CERT_TYPE_RESERVED_1
    [InlineData((ushort)0x0100)]
    public void ACertificateEntryThatIsNotPkcs7_IsRejected(ushort certificateType)
    {
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(certificateType: certificateType)));
    }

    [Fact]
    public void ACertificateWhoseDeclaredSizeDoesNotFit_IsRejected()
    {
        // dwLength and the directory size both come out of the file and both
        // have to agree with it before a single byte is copied.
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(declaredCertificateLength: 8)));
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(declaredCertificateLength: 4)));
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(declaredCertificateLength: uint.MaxValue)));
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(declaredCertificateLength: (uint)(8 + Payload.Length) + 1)));
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(declaredDirectorySize: uint.MaxValue)));
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(declaredDirectorySize: 4)));
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(declaredDirectorySize: 0)));
    }

    [Fact]
    public void ACertificateOffsetOutsideTheImage_IsRejected()
    {
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(certificateFileOffset: 0)));
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(certificateFileOffset: uint.MaxValue)));
        Assert.Null(SystemConfigChecker.ReadCertificateTable(
            PeImage(certificateFileOffset: (uint)CertificateStart(Pe32Plus) + 0x1000)));
    }

    [Fact]
    public void AnImageTruncatedBeforeWhatItDescribes_IsRejected()
    {
        var full = PeImage();
        foreach (var length in new[]
        {
            0, 1, 0x3C, 0x40, PeHeaderOffset, PeHeaderOffset + 24,
            DirectoryTable(Pe32Plus), DirectoryTable(Pe32Plus) + SecuritySlot * 8,
            CertificateStart(Pe32Plus), CertificateStart(Pe32Plus) + 8, full.Length - 1,
        })
        {
            var error = Record.Exception(
                () => SystemConfigChecker.ReadCertificateTable(full[..length]));
            Assert.Null(error);
            Assert.Null(SystemConfigChecker.ReadCertificateTable(full[..length]));
        }
    }
}
