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

internal static class V3RecycleManager
{
    // True if any HID path is a v3 Magic Mouse in Mode A (col02 collection present in path).
    internal static bool IsV3InModeA() =>
        HidNative.EnumerateHidPaths().Any(p =>
            IsV3Path(p) &&
            p.Contains("col02", StringComparison.OrdinalIgnoreCase));

    // True if a v3 Magic Mouse is in Mode B, measured from the present HID interface
    // paths ONLY: at least one v3 path exists and none of them carries a col0x split.
    // No kernel-stack property is read here, so this does not prove the Apple filter is
    // loaded - it proves the path shape scroll depends on. The same definition is used
    // by the elevated script's Test-ModeB, so both sides agree.
    // mouhid.sys DN_STARTED (Mouse class device) is a false positive in Mode A: mouhid
    // stays bound to col01 and DN_STARTED remains True even when the device is in Mode A.
    // Empirical: real Mode B confirm latency ~563ms (confirmed 2026-05-07).
    internal static bool IsV3InModeB()
    {
        var v3Paths = HidNative.EnumerateHidPaths()
            .Where(IsV3Path)
            .ToList();

        if (v3Paths.Count == 0) return false;

        // In Mode B all v3 HID paths are unified - no &col0x collection suffixes
        if (v3Paths.Any(p => p.Contains("&col0", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Unified v3 HID paths (no &col0x) means Mode B.
        return true;
    }

    // True if the HID device path belongs to a Magic Mouse v3 (BT or USB).
    // internal: ModeFlip picks the col02 collection out of the same path set.
    internal static bool IsV3Path(string path) =>
        (path.Contains("0001004c", StringComparison.OrdinalIgnoreCase) &&
         path.Contains("pid&0323", StringComparison.OrdinalIgnoreCase)) ||
        (path.Contains("vid_05ac", StringComparison.OrdinalIgnoreCase) &&
         path.Contains("pid_0323", StringComparison.OrdinalIgnoreCase));
}
