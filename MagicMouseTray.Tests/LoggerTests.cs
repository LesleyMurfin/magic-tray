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
}
