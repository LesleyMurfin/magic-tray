// V3RecycleManager.cs - what Mode A and Mode B look like from userland.
//
// All that is left here are the three measurements: given the present HID
// device interface paths, is the v3 Magic Mouse (PID 0323) in Mode A (battery
// readable on a col02 collection, scroll dead) or Mode B (one unified path,
// scroll works, battery unreadable). TrayApp checks the mode radios with them
// and ModeFlip verifies the end state of a flip with them.
//
// Everything else that used to live in this file is gone:
//
//   - SubmitFlipAndWait / WriteRequest / StartTask wrote C:\mm-dev-queue\
//     request.txt and ran a developer scheduled task named MM-Dev-Cycle to do
//     the privileged half of a flip. Neither the queue folder nor the task
//     ever shipped, so on a user machine both halves failed silently and the
//     return value was discarded by the menu. That protocol was unshippable
//     and has been removed rather than documented; ModeFlip.cs now performs
//     the flip in the tray, with one UAC prompt and a verified restore.
//   - The idle auto-recycle loop (RunLoop, WaitForUserIdle, cursor idle
//     detection), ExecuteRecycleCycle, RestoreModeBWithRetry, the failure
//     caps, ForceReadNowAsync and the BatteryRead event. Nothing constructed
//     this class, Config.EnableV3Recycle defaults false and is ignored, so all
//     of it was unreachable.
//
// The empirical notes on the two shapes are kept verbatim - they are
// measurements, not commentary.
namespace MagicMouseTray;

// What userland can SEE of the v3 Magic Mouse's mode right now, measured from
// the present HID device interface paths and nothing else.
//
// Tri-state, because the two ways of "not looking like scroll mode" have
// opposite consequences: a split path means the wheel is dead right now, while
// no v3 path at all means the mouse is switched off, asleep, out of range or
// unpaired and NOTHING is proven about its mode. A single bool collapses those
// two, and a caller that reads that bool as "the restore did not work" turns a
// mouse which is simply not here into a dead-wheel warning.
internal enum ModeBObservation
{
    // No v3 path on the bus at all: no evidence either way.
    NotObservable,

    // At least one v3 path carries a &col0x collection suffix: the stack has
    // split and scroll is dead right now.
    InModeA,

    // At least one v3 path, none of them split: the shape scroll depends on.
    InModeB,
}

internal static class V3RecycleManager
{
    // True if any HID path is a v3 Magic Mouse in Mode A (col02 collection present in path).
    internal static bool IsV3InModeA() =>
        HidNative.EnumerateHidPaths().Any(p =>
            IsV3Path(p) &&
            p.Contains("col02", StringComparison.OrdinalIgnoreCase));

    // The mode the present HID interface paths show. Path shape only: no
    // kernel-stack property is read, so this is the shape scroll depends on
    // and not proof the Apple filter is loaded. The elevated script's
    // Test-ModeB measures the same shape.
    // Measured: mouhid.sys stays bound to col01 with DN_STARTED true in Mode
    // A, so it is a false positive; Mode B confirm latency ~563 ms (2026-05-07).
    internal static ModeBObservation ObserveV3Mode(IEnumerable<string> hidPaths)
    {
        ArgumentNullException.ThrowIfNull(hidPaths);

        var observed = ModeBObservation.NotObservable;
        foreach (var path in hidPaths)
        {
            if (!IsV3Path(path))
                continue;
            // Mid-re-enumeration both shapes can be present at once, and the
            // split one is the conservative reading: the unified path scroll
            // needs is gone, so scroll is not safe to claim.
            if (path.Contains("&col0", StringComparison.OrdinalIgnoreCase))
                return ModeBObservation.InModeA;
            observed = ModeBObservation.InModeB;
        }
        return observed;
    }

    // True if the HID device path belongs to a Magic Mouse v3 (BT or USB).
    // internal: ModeFlip picks the col02 collection out of the same path set.
    internal static bool IsV3Path(string path) =>
        (path.Contains("0001004c", StringComparison.OrdinalIgnoreCase) &&
         path.Contains("pid&0323", StringComparison.OrdinalIgnoreCase)) ||
        (path.Contains("vid_05ac", StringComparison.OrdinalIgnoreCase) &&
         path.Contains("pid_0323", StringComparison.OrdinalIgnoreCase));
}
