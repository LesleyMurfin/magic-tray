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
    ///   -1  device not found
    ///   -2  device present but the battery report is not exposed, or it answered 0
    /// The floor is 1, not 0: Apple firmware reports 1..100, so a zero is only ever
    /// seen from a dead or phantom interface. Both implementations enforce it with
    /// MouseBatteryDevice.MinValidPercent, because a real 0 would outrank -2 and -1
    /// in AdaptivePoller.ReadingRank and beat a live interface's failure sentinel.
    /// </summary>
    int GetBatteryPercent();
}
