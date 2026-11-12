namespace MagicMouseTray;

public enum DeviceKind
{
    MagicMouseV1,
    MagicMouseV2,
    MagicMouseV3,
    MagicKeyboard,
    MagicTrackpadV1,
    MagicTrackpadV2,
    MagicTrackpadV3,
    LogitechMouse,
}

public interface IBatteryDevice
{
    string DeviceName { get; }
    string Pid { get; }
    DeviceKind Kind { get; }

    /// <summary>
    /// The HID interface path this instance reads. Unique per interface and stable within a
    /// discovery cycle, so per-interface state can be keyed by path. DeviceName cannot serve
    /// that purpose: it is per model, and AdaptivePoller deliberately groups several
    /// interfaces under one name.
    /// </summary>
    string DevicePath { get; }

    /// <summary>
    /// Returns battery percentage (1-100), or a sentinel:
    ///   -1  device not found or disconnected, the poller gave up waiting on a read that had
    ///       not come back yet, or the read completed and answered with something that is not
    ///       a level
    ///   -2  device present but the battery report is not arriving at all: the capability
    ///       is absent, the HID read call itself completed and reported failure (either mouse
    ///       path, and the keyboard Feature path), or the read succeeded and came back zeroed
    ///       on a path where a zeroed report IS the report not arriving (the [90 00 00] arm of
    ///       MouseBatteryDevice.ReadV3Rid90)
    /// The floor is 1, not 0: Apple firmware reports 1..100, so a zero is only ever
    /// seen from a dead or phantom interface. Every implementation's read path gates
    /// on the one predicate MouseBatteryDevice.IsRealLevel, because AdaptivePoller's
    /// BestReading stops at the first real percentage it sees in an interface group:
    /// an unfloored 0 would beat a live interface's failure sentinel, end the scan and
    /// alert at 0% on a healthy device.
    /// Which sentinel a rejected report that arrived becomes is uniform: -1 on the keyboard
    /// Feature path, on the mouse split and unified-Feature paths, on the Logitech HID++ path,
    /// and on the v3 Input 0x90 path whenever the report's content is merely unusable (a percent
    /// byte of 101..255, or a report id that is not 0x90). The single -2 among arrived reports is
    /// a v3 [90 xx 00], because there a zero IS the battery report failing to arrive at all (an
    /// idle mouse or a charge-cable phantom) - the only case a read path can prove. A read whose
    /// HidD_GetInputReport calls all came back FAILED is -2 on both mouse paths
    /// (MouseBatteryDevice.ClassifyReadFailure): the call completed and reported failure, which
    /// is evidence the report is not arriving. That is not the -1 above it: there the call never
    /// came back at all and AdaptivePoller.ReadBatteryGuarded stopped waiting on a read that is
    /// still running inside the driver, which proves nothing either way.
    /// Both sentinels rank below every real level, and each rejected value logs a distinct
    /// marker rather than reusing a blocked-read marker.
    /// Rule for any implementation added after these three: pick the sentinel by what the read
    /// path can prove about the battery report, not by how the call itself ended. -2 only where
    /// it can show the battery report never arrived; -1 whenever the report did arrive and its
    /// content is not a level, and whenever nothing at all was established.
    /// </summary>
    int GetBatteryPercent();
}
