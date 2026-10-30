// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

// One prompted scroll probe at a time, for the whole process.
//
// The raw-input registration a probe's pump takes out is per PROCESS, not per
// window (WheelSink.cs:429-438): a second pump's RegisterRawInputDevices
// silently retargets WM_INPUT away from the first pump's window, and the
// unqualified RIDEV_REMOVE deregisters the process rather than one window, so
// whichever pump unwinds first silences the other for the rest of its window. A
// leg silenced after it had already resolved the target path and earned the
// sticky decoder proof reads downstream as a genuine zero-notch measurement -
// i.e. as "scroll is dead" on a healthy mouse. The overlap is reachable by
// hand: no dialog is on screen during a ten-second leg, so the tray menu stays
// clickable and a second "Check for problems now" walks straight back in here.
//
// The gate owns the whole run rather than exposing claim/release: refusal is a
// value the caller has to handle (null), not a silent no-op, because a user who
// asked twice is owed an answer.
internal sealed class ScrollProbeGate
{
    // Reported when a fault escapes the probe. The probe's own measured
    // failures are logged by the probe itself, which knows which device it was
    // measuring.
    readonly Action<Exception>? _onFault;
    int _inFlight;

    internal ScrollProbeGate(Action<Exception>? onFault = null) => _onFault = onFault;

    // The run, or null when a probe is already in flight. start is invoked
    // synchronously on the caller's thread, so a caller that wants its setup on
    // the UI thread gets it there - and INSIDE the claim, because a throw
    // between claiming the slot and reaching the probe's own catch would
    // otherwise leak the claim and leave this process unable to run the probe
    // ever again. A start that throws releases, reports, and leaves a finished
    // run behind: nothing is flung back through a menu click handler.
    internal Task? TryStart(Func<Task> start)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return null;

        return Run(start);
    }

    async Task Run(Func<Task> start)
    {
        try
        {
            await start().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _onFault?.Invoke(ex);
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }
}
