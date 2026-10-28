// SPDX-License-Identifier: MIT
using System.IO;
using System.Security.Cryptography;

namespace MagicMouseTray;

// What checking the downloaded driver package against its pin concluded.
internal enum PackageIntegrity
{
    // The bytes that arrived hash to the pinned SHA-256.
    Verified,

    // Something else arrived: a tampered mirror, a proxy-rewritten body, a
    // truncated download, or a package that was changed under the pin.
    DigestMismatch,

    // Nobody ever said which bytes were meant - no pin, a blank pin or a
    // string that is not a SHA-256. Never "nothing to check against, so
    // continue".
    NoExpectedDigest,
}

internal sealed class PackageIntegrityResult
{
    internal PackageIntegrityResult(PackageIntegrity status, string? expectedDigest, string actualDigest)
    {
        Status = status;
        ExpectedDigest = expectedDigest;
        ActualDigest = actualDigest;
    }

    internal PackageIntegrity Status { get; }

    // The pin as the catalogue carries it, or null when there was none.
    internal string? ExpectedDigest { get; }

    // The digest of what actually arrived, so the user can say what they saw.
    internal string ActualDigest { get; }

    internal bool Verified => Status == PackageIntegrity.Verified;
}

// A stated refusal to go on: the outcome the UI reports and the sentence it
// shows. Distinct from a download error, which is retryable.
internal sealed class DriverPackageRefusal
{
    internal DriverPackageRefusal(InstallOutcome outcome, string message)
    {
        Outcome = outcome;
        Message = message;
    }

    internal InstallOutcome Outcome { get; }

    internal string Message { get; }
}

// The gate between "bytes arrived over the network" and "an elevated cmd.exe
// runs them". A pinned commit says which bytes were meant; only this hash says
// the bytes that arrived are those bytes.
internal static class DriverPackageVerifier
{
    internal static string ComputeSha256(Stream payload)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(payload)).ToLowerInvariant();
    }

    internal static PackageIntegrityResult Verify(string? expectedDigest, Stream payload)
    {
        var actual = ComputeSha256(payload);

        var pin = expectedDigest?.Trim();
        if (string.IsNullOrEmpty(pin) || !IsSha256Hex(pin))
            return new PackageIntegrityResult(PackageIntegrity.NoExpectedDigest, expectedDigest, actual);

        // Hex case is not a trust signal: a pin copied out of a release page
        // in upper case names the same 32 bytes.
        return string.Equals(pin, actual, StringComparison.OrdinalIgnoreCase)
            ? new PackageIntegrityResult(PackageIntegrity.Verified, expectedDigest, actual)
            : new PackageIntegrityResult(PackageIntegrity.DigestMismatch, expectedDigest, actual);
    }

    // Null when the package verified and the install may continue.
    internal static DriverPackageRefusal? RefusalFor(PackageIntegrityResult result) => result.Status switch
    {
        PackageIntegrity.Verified => null,

        PackageIntegrity.DigestMismatch => new DriverPackageRefusal(
            InstallOutcome.IntegrityFailed,
            $"The bytes that arrived are not the {DriverPackageCatalog.V3RepoName} package this " +
            "version pins. That can be a proxy or captive portal rewriting the download, an " +
            "upstream archive that changed, or tampering - this check cannot tell which. " +
            $"Expected SHA-256 {result.ExpectedDigest}, got {result.ActualDigest}. " +
            "Nothing was extracted or run. Try again on a network you trust; if it repeats, " +
            "report both digests."),

        _ => new DriverPackageRefusal(
            InstallOutcome.IntegrityFailed,
            $"No usable SHA-256 is pinned for the {DriverPackageCatalog.V3RepoName} package, " +
            "so what arrived cannot be checked against the bytes that were meant. " +
            "Nothing was extracted or run."),
    };

    static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
            return false;
        foreach (var c in value)
        {
            var hex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!hex)
                return false;
        }
        return true;
    }
}
