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
    ///   -1  device not found, or it answered with something that is not a level
    ///   -2  device present but the battery report is not exposed
    /// The floor is 1, not 0: Apple firmware reports 1..100, so a zero is only ever
    /// seen from a dead or phantom interface. Every implementation's read path gates
    /// on the one predicate MouseBatteryDevice.IsRealLevel, because a real 0 would
    /// outrank -2 and -1 in AdaptivePoller.ReadingRank, end BestReading's scan of the
    /// group, and beat a live interface's failure sentinel.
    /// Which sentinel a rejected zero becomes is per read path, not uniform: the v3
    /// Input 0x90 path and the keyboard Feature path return -2, the mouse split and
    /// unified-Feature paths and the Logitech HID++ path return -1. Both rank below
    /// every real level, and each logs a distinct zero marker rather than reusing a
    /// blocked-read marker.
    /// </summary>
    int GetBatteryPercent();
}
