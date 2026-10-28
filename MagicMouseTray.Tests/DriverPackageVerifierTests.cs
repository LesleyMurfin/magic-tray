// SPDX-License-Identifier: MIT
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// The v3 package is downloaded over the network, extracted into the driver
// cache and then handed to an elevated cmd.exe. Everything between "bytes
// arrived" and "administrator runs them" is this gate. Published SHA-256
// vectors are used as the expected values so no test is marking its own
// homework with the production hash function.
public class DriverPackageVerifierTests
{
    const string Fox = "The quick brown fox jumps over the lazy dog";
    const string FoxDigest = "d7a8fbb307d7809469ca9abcb0082e4f8d5651e46d3cdb762d02d0bf37c9e592";
    const string AbcDigest = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    const string AbdDigest = "a52d159f262b2c6ddb724a61840befc36eb30c88877a4030b65cbe86298449c9";
    const string EmptyDigest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    static MemoryStream Text(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void ComputeSha256_IsLowercaseHexOverEveryByteOfThePayload()
    {
        Assert.Equal(AbcDigest, DriverPackageVerifier.ComputeSha256(Text("abc")));
        Assert.Equal(FoxDigest, DriverPackageVerifier.ComputeSha256(Text(Fox)));
        Assert.Equal(EmptyDigest, DriverPackageVerifier.ComputeSha256(Text("")));
    }

    [Fact]
    public void Verify_BytesThatMatchThePin_AreTheVerifiedPackage()
    {
        var result = DriverPackageVerifier.Verify(FoxDigest, Text(Fox));

        Assert.True(result.Verified);
        Assert.Equal(PackageIntegrity.Verified, result.Status);
        Assert.Equal(FoxDigest, result.ActualDigest);
    }

    // Hex case is not a trust signal: a pin copied out of a release page in
    // upper case is the same 32 bytes, and must not fail closed on cosmetics.
    [Fact]
    public void Verify_PinLetterCase_DoesNotDecideTrust()
    {
        var result = DriverPackageVerifier.Verify(FoxDigest.ToUpperInvariant(), Text(Fox));

        Assert.True(result.Verified);
        Assert.Equal(PackageIntegrity.Verified, result.Status);
    }

    // One byte is all a swapped payload needs to be a different program.
    [Fact]
    public void Verify_OneFlippedByte_IsNotThePinnedPackage()
    {
        var result = DriverPackageVerifier.Verify(AbcDigest, Text("abd"));

        Assert.False(result.Verified);
        Assert.Equal(PackageIntegrity.DigestMismatch, result.Status);
        Assert.Equal(AbcDigest, result.ExpectedDigest);
        // the digest of what actually arrived, not of what was wanted
        Assert.Equal(AbdDigest, result.ActualDigest);

        var tampered = Encoding.UTF8.GetBytes(Fox);
        tampered[16] ^= 0x01;
        var inTheMiddle = DriverPackageVerifier.Verify(FoxDigest, new MemoryStream(tampered));

        Assert.False(inTheMiddle.Verified);
        Assert.Equal(PackageIntegrity.DigestMismatch, inTheMiddle.Status);
        Assert.NotEqual(FoxDigest, inTheMiddle.ActualDigest);
    }

    // A connection that dies mid-download leaves a short file. Hashing only
    // what turned up must not pass, and a prefix of the real package is not
    // the real package.
    [Fact]
    public void Verify_EmptyOrTruncatedPayload_IsRefused()
    {
        var empty = DriverPackageVerifier.Verify(FoxDigest, Text(""));

        Assert.False(empty.Verified);
        Assert.Equal(PackageIntegrity.DigestMismatch, empty.Status);
        Assert.Equal(EmptyDigest, empty.ActualDigest);

        var full = Encoding.UTF8.GetBytes(Fox);
        var truncated = DriverPackageVerifier.Verify(
            FoxDigest, new MemoryStream(full, 0, full.Length - 1));

        Assert.False(truncated.Verified);
        Assert.Equal(PackageIntegrity.DigestMismatch, truncated.Status);
        Assert.NotEqual(FoxDigest, truncated.ActualDigest);
    }

    // No pin, a blank pin or a pin that is not a SHA-256 means nobody ever
    // said which bytes were meant. That is a refusal, never "nothing to check
    // against, so continue".
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ba7816bf")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Verify_WithoutAUsablePin_RefusesInsteadOfTrustingWhatArrived(string? pin)
    {
        var result = DriverPackageVerifier.Verify(pin, Text(Fox));

        Assert.False(result.Verified);
        Assert.Equal(PackageIntegrity.NoExpectedDigest, result.Status);
    }

    [Fact]
    public void RefusalFor_VerifiedPackage_IsNoRefusal_SoTheInstallContinues()
    {
        var verified = DriverPackageVerifier.Verify(FoxDigest, Text(Fox));

        Assert.Null(DriverPackageVerifier.RefusalFor(verified));
    }

    // "Install failed" is what a 404 and a timeout look like, and a user who
    // reads it retries. Bytes that did not match the pin are a different
    // event with a different answer: stop, and do not retry into the same
    // tampered mirror. The outcome has to carry that difference.
    [Fact]
    public void RefusalFor_Mismatch_IsAStatedIntegrityFailure_NotAGenericInstallFailure()
    {
        var result = DriverPackageVerifier.Verify(AbcDigest, Text("abd"));

        var refusal = DriverPackageVerifier.RefusalFor(result);

        Assert.NotNull(refusal);
        Assert.Equal(InstallOutcome.IntegrityFailed, refusal!.Outcome);
        Assert.NotEqual(InstallOutcome.Failed, refusal.Outcome);
        Assert.NotEqual(InstallOutcome.Confirmed, refusal.Outcome);

        // both digests, so the user can say what they saw
        Assert.Contains(AbcDigest, refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AbdDigest, refusal.Message, StringComparison.OrdinalIgnoreCase);
        // what it refused, and the fact that refusing happened in time
        Assert.Contains(DriverPackageCatalog.V3RepoName, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was extracted or run", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not pull", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusalFor_MissingPin_SaysItRefused_RatherThanBlamingTheDownload()
    {
        var result = DriverPackageVerifier.Verify(null, Text(Fox));

        var refusal = DriverPackageVerifier.RefusalFor(result);

        Assert.NotNull(refusal);
        Assert.Equal(InstallOutcome.IntegrityFailed, refusal!.Outcome);
        Assert.Contains("SHA-256", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was extracted or run", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not pull", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractVerifiedPackage_PayloadThatMatchesThePin_IsExtracted()
    {
        var root = TempRoot();
        try
        {
            var zip = MakeZip(root, "v2-kmdf-driver/Install-KMDF.cmd", "@echo off");
            var extractDir = Path.Combine(root, "extract");

            var result = DriverInstaller.ExtractVerifiedPackage(zip, Sha256OfFile(zip), extractDir);

            Assert.True(result.Verified);
            Assert.Equal(PackageIntegrity.Verified, result.Status);
            Assert.True(File.Exists(
                Path.Combine(extractDir, "v2-kmdf-driver", "Install-KMDF.cmd")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The check has to happen before the filesystem the elevated step reads
    // from is touched at all: not after extraction, not after the cached copy
    // that did pass a check has been deleted to make room.
    [Fact]
    public void ExtractVerifiedPackage_PayloadThatFailsThePin_ExtractsNothing_AndKeepsTheVerifiedCache()
    {
        var root = TempRoot();
        try
        {
            var zip = MakeZip(root, "v2-kmdf-driver/Install-KMDF.cmd", "@echo off\r\nwhoami");
            var extractDir = Path.Combine(root, "extract");
            Directory.CreateDirectory(Path.Combine(extractDir, "v2-kmdf-driver"));
            var cached = Path.Combine(extractDir, "v2-kmdf-driver", "Install-KMDF.cmd");
            File.WriteAllText(cached, "@echo off :: the copy that passed a check");

            var good = Sha256OfFile(zip);
            var pin = good[..63] + (good[63] == '0' ? '1' : '0');

            var result = DriverInstaller.ExtractVerifiedPackage(zip, pin, extractDir);

            Assert.False(result.Verified);
            Assert.Equal(PackageIntegrity.DigestMismatch, result.Status);
            Assert.Equal(good, result.ActualDigest);
            Assert.Equal("@echo off :: the copy that passed a check", File.ReadAllText(cached));
            Assert.Single(Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ExtractVerifiedPackage_WithoutAPin_RefusesEvenAnIntactZip()
    {
        var root = TempRoot();
        try
        {
            var zip = MakeZip(root, "v2-kmdf-driver/Install-KMDF.cmd", "@echo off");
            var extractDir = Path.Combine(root, "extract");

            var result = DriverInstaller.ExtractVerifiedPackage(zip, "", extractDir);

            Assert.False(result.Verified);
            Assert.Equal(PackageIntegrity.NoExpectedDigest, result.Status);
            Assert.False(Directory.Exists(extractDir));
        }
        finally
        {
            Cleanup(root);
        }
    }

    static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-tray-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static void Cleanup(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    static string MakeZip(string root, string entryPath, string content)
    {
        var zipPath = Path.Combine(root, "package.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return zipPath;
    }

    static string Sha256OfFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
