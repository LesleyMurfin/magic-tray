// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// Decisions the wheel sink has to get right, exercised through IRawMouseSource
// with synthetic records - no window, no raw input registration, no hardware.
//
// Every assertion here is observable: what a WheelObservation says. Nothing
// asserts how the pump is wired.
public class WheelSinkTests
{
    // The 0323 pointer collection, verbatim from the reference PC.
    const string TargetPath =
        @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0323&Col01#a&31e5d054&2a&0000";
    // A second mouse that contributed stray records in the same live runs.
    const string OtherMousePath = @"\\?\HID#VID_413C&PID_3012&Col01#7&1f2a3b4c&0&0000";

    static readonly IntPtr Target = new(0x5251099);
    static readonly IntPtr Other = new(0x1234567);

    // Short windows: these tests assert counts and flags, not durations.
    static readonly TimeSpan Window = TimeSpan.FromMilliseconds(150);

    static RawMouseRecord Motion(IntPtr device, int x, int y) => new(device, 0, 0, x, y);
    static RawMouseRecord Wheel(IntPtr device, short delta) => new(device, 0x0400, delta, 0, 0);
    static RawMouseRecord HWheel(IntPtr device, short delta) => new(device, 0x0800, delta, 0, 0);
    static RawMouseRecord Button(IntPtr device, ushort flags) => new(device, flags, 0, 0, 0);

    static FakeMouseSource Source(params RawMouseRecord[] records) =>
        new(new Dictionary<IntPtr, string?>
        {
            [Target] = TargetPath,
            [Other] = OtherMousePath,
        }, records);

    // A window in which the touch stream never moved is VOID: it is not a
    // negative result and consumers must not read it as one. The reference run
    // that forced this had all 305 of its mouse records in the first 5 s of a
    // 60 s window with the filter's report counter flat throughout.
    [Fact]
    public async Task NoTouchActivity_IsVoidWithNoCounts()
    {
        var sink = new RawInputWheelSink(Source(Wheel(Target, 120)), () => false);

        var obs = await sink.ObserveAsync(Window, CancellationToken.None);

        Assert.True(obs.Void);
        Assert.Equal(TimeSpan.Zero, obs.ActiveDuration);
        Assert.Equal(0, obs.MouseRecords);
        Assert.Equal(0, obs.WheelEvents);
        Assert.False(obs.DecoderValidated);
        Assert.Null(obs.TargetDevicePath);
    }

    // No evidence (the seam's null) is not inactivity, but it is still not a
    // measurement: the window stays void rather than being reported as a zero.
    [Fact]
    public async Task UnknownTouchActivity_IsVoid()
    {
        var sink = new RawInputWheelSink(Source(Wheel(Target, 120)), () => null);

        var obs = await sink.ObserveAsync(Window, CancellationToken.None);

        Assert.True(obs.Void);
        Assert.Equal(0, obs.WheelEvents);
    }

    // A prompted leg does not wait to arm - but a user who never touched the
    // surface must still come back VOID, never as a zero-wheel measurement.
    [Fact]
    public async Task PromptedLeg_WithoutTouchActivity_IsVoid()
    {
        var sink = new RawInputWheelSink(Source(Wheel(Target, 120)), () => null);

        var obs = await sink.ObservePromptedAsync(Window, CancellationToken.None);

        Assert.True(obs.Void);
    }

    [Fact]
    public async Task PromptedLeg_WithTouchActivity_MeasuresImmediately()
    {
        var sink = new RawInputWheelSink(Source(Wheel(Target, 120)), () => true);

        var obs = await sink.ObservePromptedAsync(Window, CancellationToken.None);

        Assert.False(obs.Void);
        Assert.Equal(1, obs.WheelEvents);
        Assert.True(obs.ActiveDuration > TimeSpan.Zero);
    }

    // Wheel and hwheel are counted per event, whatever the sign: a notch is a
    // notch up or down, and both directions have to survive the decode.
    [Fact]
    public async Task WheelAndHWheel_CountedForBothSigns()
    {
        var sink = new RawInputWheelSink(
            Source(
                Wheel(Target, 120), Wheel(Target, -120),
                HWheel(Target, 120), HWheel(Target, -120),
                Motion(Target, 5, -7)),
            () => true);

        var obs = await sink.ObserveAsync(Window, CancellationToken.None);

        Assert.False(obs.Void);
        Assert.Equal(TargetPath, obs.TargetDevicePath);
        Assert.Equal(5, obs.MouseRecords);
        Assert.Equal(2, obs.WheelEvents);
        Assert.Equal(2, obs.HWheelEvents);
        Assert.Equal(12, obs.AbsMotionSum);
        Assert.Equal(0, obs.ButtonEvents);
    }

    // Another mouse's wheel must never fill in for the 0323's zero.
    [Fact]
    public async Task NonTargetDevice_DoesNotContributeToTargetCounts()
    {
        var sink = new RawInputWheelSink(
            Source(
                Motion(Target, 3, 4),
                Wheel(Other, 120), Wheel(Other, 120), HWheel(Other, -120),
                Button(Other, 0x0001)),
            () => true);

        var obs = await sink.ObserveAsync(Window, CancellationToken.None);

        Assert.Equal(TargetPath, obs.TargetDevicePath);
        Assert.Equal(1, obs.MouseRecords);
        Assert.Equal(0, obs.WheelEvents);
        Assert.Equal(0, obs.HWheelEvents);
        Assert.Equal(0, obs.ButtonEvents);
        Assert.False(obs.DecoderValidated);
        Assert.Equal(7, obs.AbsMotionSum);
    }

    // The wheel bit cannot validate the offset it is itself read from: only a
    // real click (0x0001/0x0002) is proof. Without it a zero wheel count means
    // nothing, so the flag must stay false.
    [Fact]
    public async Task WheelFlagAlone_DoesNotValidateDecoder()
    {
        var sink = new RawInputWheelSink(
            Source(Wheel(Target, 120), HWheel(Target, 120), Motion(Target, 1, 1)),
            () => true);

        var obs = await sink.ObserveAsync(Window, CancellationToken.None);

        Assert.False(obs.DecoderValidated);
        Assert.Equal(0, obs.ButtonEvents);
    }

    [Fact]
    public async Task NonWheelButtonFlag_ValidatesDecoder()
    {
        var sink = new RawInputWheelSink(
            Source(Button(Target, 0x0001), Button(Target, 0x0002)),
            () => true);

        var obs = await sink.ObserveAsync(Window, CancellationToken.None);

        Assert.True(obs.DecoderValidated);
        Assert.Equal(2, obs.ButtonEvents);
    }

    // Sticky per device path: a prompted "scroll for ten seconds" leg gives the
    // user no reason to click, so validation earned in an earlier window has to
    // carry into later ones or the probe is inconclusive forever.
    [Fact]
    public async Task DecoderValidation_IsStickyPerDevicePathAcrossObservations()
    {
        var paths = new Dictionary<IntPtr, string?> { [Target] = TargetPath };
        var source = new FakeMouseSource(paths, [Button(Target, 0x0001)]);
        var sink = new RawInputWheelSink(source, () => true);

        var clicked = await sink.ObserveAsync(Window, CancellationToken.None);
        Assert.True(clicked.DecoderValidated);

        // A later window on the same device with no click in it at all.
        source.Script(Wheel(Target, 120));
        var later = await sink.ObserveAsync(Window, CancellationToken.None);
        Assert.Equal(0, later.ButtonEvents);
        Assert.True(later.DecoderValidated);

        // Stickiness is earned, never assumed: a sink that has seen no click
        // reports the same window as unvalidated.
        var unproven = await new RawInputWheelSink(
            new FakeMouseSource(paths, [Wheel(Target, 120)]), () => true)
            .ObserveAsync(Window, CancellationToken.None);
        Assert.False(unproven.DecoderValidated);
    }

    // A cancelled poll tick is not an error: the half-measured window is still
    // evidence, so the sink returns what it has instead of throwing.
    [Fact]
    public async Task Cancellation_ReturnsPartialObservation()
    {
        var sink = new RawInputWheelSink(
            Source(Wheel(Target, 120), Motion(Target, 2, 2)), () => true);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        var obs = await sink.ObserveAsync(TimeSpan.FromSeconds(30), cts.Token);

        Assert.False(obs.Void);
        Assert.Equal(TargetPath, obs.TargetDevicePath);
        Assert.Equal(2, obs.MouseRecords);
        Assert.Equal(1, obs.WheelEvents);
        Assert.True(obs.WallDuration < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void TargetDevicePath_IsThe0323PointerCollectionOnly()
    {
        Assert.True(RawInputWheelSink.IsTargetDevicePath(TargetPath));
        Assert.True(RawInputWheelSink.IsTargetDevicePath(TargetPath.ToUpperInvariant()));
        Assert.False(RawInputWheelSink.IsTargetDevicePath(OtherMousePath));
        // COL02 is the vendor/battery collection; it never carries a wheel.
        Assert.False(RawInputWheelSink.IsTargetDevicePath(
            TargetPath.Replace("Col01", "Col02", StringComparison.OrdinalIgnoreCase)));
        Assert.False(RawInputWheelSink.IsTargetDevicePath(null));
    }

    // Delivers a script of records off the caller's thread, then idles until
    // cancelled - the shape the real pump has. Script() replaces the script, so
    // one source can serve several consecutive windows.
    sealed class FakeMouseSource(
        Dictionary<IntPtr, string?> paths, IReadOnlyList<RawMouseRecord> records) : IRawMouseSource
    {
        IReadOnlyList<RawMouseRecord> _records = records;

        internal void Script(params RawMouseRecord[] next) => _records = next;

        public async Task PumpAsync(Action<RawMouseRecord> onRecord, CancellationToken ct)
        {
            await Task.Yield();
            foreach (var record in _records)
                onRecord(record);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public string? ResolveDevicePath(IntPtr device) =>
            paths.TryGetValue(device, out var path) ? path : null;
    }
}
