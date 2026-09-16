// SPDX-License-Identifier: MIT
using System.Threading;

namespace MagicMouseTray;

// One allocator for every attempt nonce in the app, because a millisecond clock
// reading is not a unique id and the nonce is used as one.
//
// The nonce names this attempt's generated .ps1 and its status sidecar on both
// sides of the elevation boundary (DeviceEnable.StatusSidecarPath,
// DeviceRepair.StatusSidecarPath, ModeFlip.StatusSidecarPath), and nothing
// serializes the callers: TrayApp's StartRepairApply and StartStaleFilterRemoval
// each go straight to Task.Run and RunDriverActionAsync does not queue driver
// actions. Every site used to mint with DateTimeOffset.UtcNow
// .ToUnixTimeMilliseconds() (DeviceEnable.cs:533, DeviceRepair.cs:642 and 667,
// ModeFlip.cs:322 and 449 before 2026-09-16), so two attempts that started
// inside the same millisecond got the SAME value and shared both file names:
// one could overwrite the script the other was about to run, or consume the
// completed report the other was polling for and report an end state a
// different elevated process measured. Naming the device PID in the file closed
// the device-level collision only; this closes the millisecond-wide one.
//
// The value stays a Unix millisecond timestamp, so a support log still reads as
// a time and sorts by attempt order. Under contention the counter runs at most
// one millisecond ahead of the clock per overlapping attempt.
//
// Scope: this process. Two tray processes minting in the same millisecond are
// separated by Environment.ProcessId, which DeviceEnable and DeviceRepair carry
// as its own segment in the file name next to the nonce.
internal static class AttemptNonce
{
    static long last;

    // Lock-free and allocation-free: take max(now, last + 1) so the result is
    // strictly greater than every value already handed out, and publish it with
    // CompareExchange. Losing the exchange means another thread published
    // first, and the retry recomputes against the value it actually observed -
    // so two callers can never leave with the same number.
    internal static long Next()
    {
        var previous = Interlocked.Read(ref last);
        while (true)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var next = now > previous ? now : previous + 1;
            var seen = Interlocked.CompareExchange(ref last, next, previous);
            if (seen == previous)
                return next;
            previous = seen;
        }
    }
}
