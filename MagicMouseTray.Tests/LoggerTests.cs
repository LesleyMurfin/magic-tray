// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

/// <summary>
/// Covers the redaction Logger applies to every line it writes: debug.log is
/// shared with strangers directly, not only through the redacted bug report.
/// </summary>
public class LoggerTests
{
    /// <summary>
    /// A line carrying both a real address and a device instance ID loses the
    /// address in both shapes it is logged in (the mac= value and the BTHENUM
    /// instance tail) while the Bluetooth base UUID 00805f9b34fb - 12 hex
    /// digits, but a constant, and the identifier triage is read off - survives
    /// intact. The redacted token matches BugReport's, so the same device
    /// correlates between debug.log and a public issue.
    /// </summary>
    [Fact]
    public void RedactMacs_ScrubsAddresses_KeepsBluetoothBaseUuid()
    {
        const string line = @"DEVICE_DIAG_POINTER mac=e806884b0741 path=BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205AC_PID&0323\8&2bd7b9a1&0&E806884B0741_C00000000";

        var redacted = Logger.RedactMacs(line);

        Assert.Equal(
            @"DEVICE_DIAG_POINTER mac=<mac> path=BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205AC_PID&0323\8&2bd7b9a1&0&<mac>_C00000000",
            redacted);
        Assert.Equal(BugReport.Redact("mac=e806884b0741"), Logger.RedactMacs("mac=e806884b0741"));
    }

    /// <summary>
    /// BugReport.Redact keeps its established output now that the shared address
    /// rules live in Logger.RedactMacs: the colon-separated form is reachable
    /// only through that call, and the bare 12-hex form - here the Bluetooth
    /// base UUID, which Logger deliberately leaves alone so debug.log keeps its
    /// instance IDs - only through the public-issue belt BugReport still applies
    /// locally. Deleting either one changes this line.
    /// </summary>
    [Fact]
    public void BugReportRedact_ComposesSharedRulesWithPublicIssueBelt()
    {
        const string line = @"DEVICE_DIAG_POINTER mac=e806884b0741 bth=aa:bb:cc:dd:ee:ff path=BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205AC_PID&0323";

        Assert.Equal(
            @"DEVICE_DIAG_POINTER mac=<mac> bth=<mac> path=BTHENUM\{00001124-0000-1000-8000-<mac>}_VID&000205AC_PID&0323",
            BugReport.Redact(line));
    }

    /// <summary>
    /// The sink drops the Windows account name out of a logged script path and
    /// leaves the rest of the path alone. Both halves matter: the driver and
    /// device-enable paths under %TEMP% are C:\Users\&lt;name&gt;\AppData\Local\Temp
    /// (CWE-532, debug.log is shared as a file), and the file name carries the
    /// PID and the attempt nonce that triage is read off - a redaction that ate
    /// the tail would trade the leak for a useless log. The account name here
    /// contains a space, because a real profile directory can.
    /// </summary>
    [Fact]
    public void RedactIdentifiers_DropsProfileName_KeepsTempScriptNameAndNonce()
    {
        const string line = @"DEVICE_ENABLE pid=0323 script=C:\Users\Lesley Murfin\AppData\Local\Temp\mm-enable-0323-1758030000000.ps1";

        Assert.Equal(
            @"DEVICE_ENABLE pid=0323 script=C:\Users\<user>\AppData\Local\Temp\mm-enable-0323-1758030000000.ps1",
            Logger.RedactIdentifiers(line));
        // A trailing segment with no separator left still goes.
        Assert.Equal(@"C:\Users\<user>", Logger.RedactIdentifiers(@"C:\Users\lesley"));
        // Every line the app writes runs through this, so a line carrying no
        // profile path must come back as the same instance, not a copy.
        const string clean = "INFO BATTERY pid=0323 pct=71";
        Assert.Same(clean, Logger.RedactProfilePaths(clean));
    }

    /// <summary>
    /// BugReport.Redact's output is unchanged by moving the profile rule to the
    /// sink: one line carrying an address, a profile path and a device instance
    /// ID still comes back with the address gone in both its shapes, the
    /// account name replaced by the same &lt;user&gt; token the sink writes, and the
    /// Bluetooth base UUID rewritten by the public-issue-only belt that Logger
    /// does not apply. This is the composed order - shared address rules, belt,
    /// shared profile rule, local identifiers - pinned end to end.
    /// </summary>
    [Fact]
    public void BugReportRedact_UnchangedForMacProfilePathAndInstanceId()
    {
        const string line = @"DRIVER_SDP script=C:\Users\Lesley Murfin\AppData\Local\Temp\mm-sdp-0323.ps1 mac=e806884b0741 id=BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205AC_PID&0323";

        Assert.Equal(
            @"DRIVER_SDP script=C:\Users\<user>\AppData\Local\Temp\mm-sdp-0323.ps1 mac=<mac> id=BTHENUM\{00001124-0000-1000-8000-<mac>}_VID&000205AC_PID&0323",
            BugReport.Redact(line));
    }
}
