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
    /// Returns battery percentage (1-100), or a sentinel:
    ///   -1  device not found or disconnected, the read timed out, or the read succeeded
    ///       and answered with something that is not a level
    ///   -2  device present but the battery report is not arriving at all: the capability
    ///       is absent, the HID read itself failed, or the read succeeded and came back
    ///       zeroed on a path where a zeroed report IS the report not arriving (the
    ///       [90 00 00] arm of MouseBatteryDevice.ReadV3Rid90)
    /// The floor is 1, not 0: Apple firmware reports 1..100, so a zero is only ever
    /// seen from a dead or phantom interface. Every implementation's read path gates
    /// on the one predicate MouseBatteryDevice.IsRealLevel, because AdaptivePoller's
    /// BestReading stops at the first real percentage it sees in an interface group:
    /// an unfloored 0 would beat a live interface's failure sentinel, end the scan and
    /// alert at 0% on a healthy device.
    /// Which sentinel a rejected zero becomes is per read path, not uniform. It is -1
    /// on the keyboard Feature path, on the mouse split and unified-Feature paths, and
    /// on the Logitech HID++ path - all three completed a read, so the report is exposed
    /// and only its content is unusable. Only the v3 Input 0x90 path answers -2, because
    /// a v3 [90 00 00] is the battery report failing to arrive at all (an idle mouse or a
    /// charge-cable phantom). Both sentinels rank below every real level, and each rejected
    /// value logs a distinct marker rather than reusing a blocked-read marker.
    /// A fourth implementation picks by what its path can prove, not by whether the call
    /// returned: -2 only where it can show the battery report never arrived, -1 whenever
    /// the report did arrive and its content is not a level.
    /// </summary>
    int GetBatteryPercent();
}
