// SPDX-License-Identifier: MIT
using System.Diagnostics;

namespace MagicMouseTray;

// Owned by the single poll loop. A five-second wait bounds the caller, not the synchronous
// native HID call. Keep its task until it really completes, even when discovery loses the path
// or WedgeTracker's cooldown expires. There is no cancellation or forced handle disposal here.
// Different paths can still be read. This bounds outstanding work per path, not across an
// unlimited sequence of different paths; it does not repair the underlying HID failure.
internal sealed class InFlightBatteryReads
{
    readonly Dictionary<string, Task<int>> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _completed = new();
    readonly Action<string> _log;
    readonly Func<IBatteryDevice, Task<int>> _launch;

    internal InFlightBatteryReads(
        Action<string>? log = null,
        Func<IBatteryDevice, Task<int>>? launch = null)
    {
        _log = log ?? Logger.Log;
        _launch = launch ?? (device => Task.Run(device.GetBatteryPercent));
    }

    internal GuardedRead Read(IBatteryDevice device, TimeSpan timeout)
    {
        // Reap only completed tasks. In particular, discovery absence must never evict a
        // running task: the same path can return while the old handle is still inside HID.
        ReapCompleted();
        if (_pending.ContainsKey(device.DevicePath))
        {
            _log($"POLL_DEVICE_READ_PENDING device={device.DeviceName} (previous read still running; no new read)");
            return new GuardedRead(-1, false, TimeSpan.Zero, AlreadyRunning: true);
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            var read = _launch(device);
            _pending.Add(device.DevicePath, read);
            if (read.Wait(timeout))
            {
                _pending.Remove(device.DevicePath);
                return new GuardedRead(read.Result, false, Stopwatch.GetElapsedTime(started));
            }

            // Observe faults even if shutdown prevents another poll from reaping this task.
            // The continuation touches no reader/poller state and never publishes a result.
            _ = read.ContinueWith(
                completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _log($"POLL_DEVICE_TIMEOUT device={device.DeviceName} after={timeout} (read still running, treated as -1)");
            return new GuardedRead(-1, true, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex)
        {
            // Wait throws only once the task is faulted/cancelled. Observing the exception
            // here preserves the existing failure sentinel without keeping completed work.
            ReapCompleted();
            _log($"POLL_DEVICE_ERROR device={device.DeviceName} err={ex.GetBaseException().Message}");
            return new GuardedRead(-1, false, Stopwatch.GetElapsedTime(started));
        }
    }

    void ReapCompleted()
    {
        _completed.Clear();
        foreach (var entry in _pending)
        {
            if (!entry.Value.IsCompleted) continue;
            // Observe a late fault, but never publish a late percentage as a fresh reading.
            // Reap on the poll thread so no continuation races the single-writer dictionaries.
            _ = entry.Value.Exception;
            _completed.Add(entry.Key);
        }
        foreach (var path in _completed)
            _pending.Remove(path);
    }
}
