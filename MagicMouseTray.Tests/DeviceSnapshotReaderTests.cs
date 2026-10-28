// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// DeviceSnapshotReader turns live-machine probes into the DeviceSnapshot the
// planner reasons over, so it is the exact place where "I could not read it"
// has to stay distinguishable from "I read it and it is broken". Both steps
// pinned here used to collapse a failed probe into a measured negative:
//
//   the filter-service loop wrote serviceRunning=false whenever
//   DriverHealthChecker.KernelServiceRunning (sc.exe query behind
//   catch { return false; } and a 2 s WaitForExit) did not come back RUNNING,
//   which is what let RepairPlanner rule 1 tell a healthy mouse "the scroll
//   driver stopped" and offer an elevated Bluetooth stack restart;
//
//   ServicePackagePresent ended catch { return false; }, which is what let
//   rule 6 tell the same user their scroll driver is not installed.
//
// Both are pure decision steps with the live reads lifted out, in the shape
// DeviceDiagReader.ScanWatcherLog and EvaluateCounterSample already use.
public class DeviceSnapshotReaderTests
{
    // The running scroll filter on the reference 0323, verbatim, and the
    // stopped leftover from an older install of the same package that sits
    // beside it in the device key's LowerFilters.
    const string BoundKmdfVariant = "MagicMouseDriver204Scroll";
    const string StaleKmdfVariant = "MagicMouseDriver";

    [Fact]
    public void ResolveBoundFilter_BindsTheCandidateThatIsRunning()
    {
        // Registry order is not precedence of truth: the leftover is named
        // first and the running filter second, and the stack's effective
        // filter is the one that runs.
        var running = DeviceSnapshotReader.ResolveBoundFilter(
            [(StaleKmdfVariant, false), (BoundKmdfVariant, true)], out var bound);

        Assert.True(running);
        Assert.Equal(BoundKmdfVariant, bound);
    }

    [Fact]
    public void ResolveBoundFilter_EveryCandidateAnsweredStopped_IsAMeasuredFalse()
    {
        // The USB-C charge incident: both names were queried, both answered,
        // neither is running. That is a real fault and the planner must still
        // get a false for it - with a service name to act on.
        var running = DeviceSnapshotReader.ResolveBoundFilter(
            [(StaleKmdfVariant, false), (BoundKmdfVariant, false)], out var bound);

        Assert.False(running);
        Assert.Equal(StaleKmdfVariant, bound);
    }

    [Fact]
    public void ResolveBoundFilter_AQueryThatFailedIsNotAStoppedService()
    {
        // The defect: sc.exe did not answer for the one filter on this stack,
        // and the snapshot said "stopped".
        var onlyName = DeviceSnapshotReader.ResolveBoundFilter(
            [(BoundKmdfVariant, (bool?)null)], out var bound);

        Assert.Null(onlyName);
        Assert.Equal(BoundKmdfVariant, bound);

        // One unreadable name beside a stopped one is still unknown overall:
        // the name that did not answer could be the running filter.
        var mixed = DeviceSnapshotReader.ResolveBoundFilter(
            [(StaleKmdfVariant, false), (BoundKmdfVariant, null)], out var mixedBound);

        Assert.Null(mixed);
        Assert.Equal(StaleKmdfVariant, mixedBound);
    }

    [Fact]
    public void ResolveBoundFilter_ARunningCandidateOutweighsAFailedQuery()
    {
        // Positive evidence beats missing evidence: something on this stack
        // IS running, so the stack has a working filter whatever the query
        // that failed would have said.
        var running = DeviceSnapshotReader.ResolveBoundFilter(
            [(StaleKmdfVariant, null), (BoundKmdfVariant, true)], out var bound);

        Assert.True(running);
        Assert.Equal(BoundKmdfVariant, bound);
    }

    [Fact]
    public void ResolveBoundFilter_NoCandidates_BindsNothingAndClaimsNothing()
    {
        // Nothing filter-like is on the stack, so no service was ever asked
        // about. "Stopped" would be a statement about a service that does not
        // enter into it.
        var running = DeviceSnapshotReader.ResolveBoundFilter([], out var bound);

        Assert.Null(running);
        Assert.Null(bound);
    }

    // keyPresent: true the service key opened, false there is no such key on
    //             this PC, null the registry read threw.
    // imageOnDisk: true the resolved ImagePath was found, false it was not,
    //              null ImagePath was missing or unparseable so the file was
    //              never looked for.
    [Theory]
    // Installed: key plus the .sys it points at.
    [InlineData(true, true, true)]
    // A leftover key whose driver file was deleted is not a usable package -
    // a real negative, and rule 6's guided install is the right answer.
    [InlineData(true, false, false)]
    // No service key at all: genuinely not installed.
    [InlineData(false, null, false)]
    [InlineData(false, true, false)]
    // The registry could not be read: nothing is known about the package, so
    // "not installed" must not be claimed.
    [InlineData(null, null, null)]
    [InlineData(null, true, null)]
    // The key opened but its ImagePath could not be resolved, so disk
    // presence was never tested: again nothing is known.
    [InlineData(true, null, null)]
    public void InterpretServicePackage_OnlyAProbeThatAnsweredIsAnAnswer(
        bool? keyPresent, bool? imageOnDisk, bool? expected)
    {
        Assert.Equal(expected, DeviceSnapshotReader.InterpretServicePackage(keyPresent, imageOnDisk));
    }
}
