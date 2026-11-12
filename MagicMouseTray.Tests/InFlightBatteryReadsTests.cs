// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public sealed class InFlightBatteryReadsTests : IDisposable
{
    readonly List<TaskCompletionSource<int>> _blocked = new();
    readonly List<string> _logs = new();

    // No sleeps, hardware, native handles or parked workers: an incomplete Task is the exact
    // boundary the guard waits on. Every incomplete fake is completed even after an assertion.
    TaskCompletionSource<int> Blocked()
    {
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _blocked.Add(source);
        return source;
    }

    public void Dispose()
    {
        foreach (var source in _blocked)
            source.TrySetResult(-1);
    }

    [Fact]
    public void Timeout_RefreshesAndCooldownExpiry_DoNotLaunchAnotherRead()
    {
        var blocked = Blocked();
        int launches = 0;
        var reads = new InFlightBatteryReads(_logs.Add, _ => { launches++; return blocked.Task; });
        var wedges = new WedgeTracker();
        var starts = new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase);
        var device = new Device("path-a");
        int Scan() => AdaptivePoller.ReadDevice("mouse", [device], TimeSpan.Zero,
            TimeSpan.FromSeconds(5), starts, wedges, reads.Read);

        Assert.Equal(-1, Scan());
        // Even a backoff earned before this call must not erase outstanding native ownership.
        for (int i = 1; i < WedgeTracker.WedgeTimeoutStrikes; i++)
            wedges.RecordTimeout(device.DevicePath);
        Assert.True(wedges.ShouldSkip(device.DevicePath));

        for (int cycle = 0; cycle < WedgeTracker.WedgeBackoffTicks + 5; cycle++)
        {
            wedges.Tick();
            Assert.Equal(-1, Scan());
        }

        Assert.False(wedges.ShouldSkip(device.DevicePath));
        Assert.Equal(1, launches);
        Assert.Single(_logs, line => line.StartsWith("POLL_DEVICE_TIMEOUT"));
        Assert.Contains(_logs, line => line.StartsWith("POLL_DEVICE_READ_PENDING"));
    }

    [Fact]
    public void PendingPath_DoesNotEndScanOrClearItsTimeoutStrikes()
    {
        var blocked = Blocked();
        int launches = 0;
        var reads = new InFlightBatteryReads(_logs.Add, d =>
        {
            launches++;
            return d.DevicePath == "path-a" ? blocked.Task : Task.FromResult(64);
        });
        var a = new Device("path-a");
        Assert.True(reads.Read(a, TimeSpan.Zero).TimedOut);
        var wedges = new WedgeTracker();
        wedges.RecordTimeout(a.DevicePath);
        var starts = new Dictionary<string, ScanState>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(64, AdaptivePoller.ReadDevice("mouse", [a, new Device("path-b")],
            TimeSpan.Zero, TimeSpan.FromSeconds(5), starts, wedges, reads.Read));
        Assert.Equal(2, launches);

        // The pending skip was not an answer. Two more strikes reach the original threshold.
        for (int i = 1; i < WedgeTracker.WedgeTimeoutStrikes; i++)
            wedges.RecordTimeout(a.DevicePath);
        Assert.True(wedges.ShouldSkip(a.DevicePath));
    }

    [Fact]
    public void RediscoveryWithNewObjectAndDifferentCase_KeepsTheOriginalRead()
    {
        var blocked = Blocked();
        int launches = 0;
        var reads = new InFlightBatteryReads(_logs.Add, d =>
        {
            launches++;
            return d.DevicePath.Equals("path-a", StringComparison.OrdinalIgnoreCase)
                ? blocked.Task : Task.FromResult(50);
        });
        Assert.True(reads.Read(new Device("path-a"), TimeSpan.Zero).TimedOut);

        // Other devices are polled while A is absent. Discovery's objects are disposable
        // snapshots, so the protection must belong to the path, not to an object or live set.
        Assert.Equal(50, reads.Read(new Device("path-b"), TimeSpan.Zero).Pct);
        var rediscovered = reads.Read(new Device("PATH-A"), TimeSpan.Zero);

        Assert.True(rediscovered.AlreadyRunning);
        Assert.False(rediscovered.TimedOut);
        Assert.Equal(-1, rediscovered.Pct);
        Assert.Equal(2, launches);
    }

    [Fact]
    public void LateSuccess_IsDiscardedAndNextAttemptCanReadAgain()
    {
        var blocked = Blocked();
        int launches = 0;
        var reads = new InFlightBatteryReads(_logs.Add, _ =>
            ++launches == 1 ? blocked.Task : Task.FromResult(42));
        var device = new Device("path-a");
        Assert.True(reads.Read(device, TimeSpan.Zero).TimedOut);
        blocked.SetResult(99);

        var fresh = reads.Read(device, TimeSpan.Zero);

        Assert.Equal(42, fresh.Pct);
        Assert.False(fresh.TimedOut);
        Assert.False(fresh.AlreadyRunning);
        Assert.Equal(2, launches);
    }

    [Fact]
    public void LateFault_DoesNotEscapeOrPreventTheNextAttempt()
    {
        var blocked = Blocked();
        int launches = 0;
        var reads = new InFlightBatteryReads(_logs.Add, _ =>
            ++launches == 1 ? blocked.Task : Task.FromResult(37));
        var device = new Device("path-a");
        Assert.True(reads.Read(device, TimeSpan.Zero).TimedOut);
        blocked.SetException(new IOException("late fake failure"));

        Assert.Equal(37, reads.Read(device, TimeSpan.Zero).Pct);
        Assert.Equal(2, launches);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(1)]
    [InlineData(100)]
    public void ImmediateAnswers_PreserveSentinelsAndReleaseOwnership(int pct)
    {
        int launches = 0;
        var reads = new InFlightBatteryReads(_logs.Add, _ =>
        {
            launches++;
            return Task.FromResult(pct);
        });
        var device = new Device("path-a");

        Assert.Equal(pct, reads.Read(device, TimeSpan.Zero).Pct);
        Assert.Equal(pct, reads.Read(device, TimeSpan.Zero).Pct);
        Assert.Equal(2, launches);
    }

    [Fact]
    public void ImmediateFault_IsMinusOneAndDoesNotKeepOwnership()
    {
        int launches = 0;
        var reads = new InFlightBatteryReads(_logs.Add, _ =>
        {
            launches++;
            return Task.FromException<int>(new IOException("fake failure"));
        });
        var device = new Device("path-a");

        var first = reads.Read(device, TimeSpan.Zero);
        Assert.Equal(-1, first.Pct);
        Assert.False(first.TimedOut);
        Assert.Equal(-1, reads.Read(device, TimeSpan.Zero).Pct);
        Assert.Equal(2, launches);
    }

    sealed class Device(string path) : IBatteryDevice
    {
        public string DevicePath => path;
        public string DeviceName => "Magic Mouse";
        public string Pid => "0323";
        public DeviceKind Kind => DeviceKind.MagicMouseV3;
        public int GetBatteryPercent() =>
            throw new InvalidOperationException("The injected launcher must own this read.");
    }
}
