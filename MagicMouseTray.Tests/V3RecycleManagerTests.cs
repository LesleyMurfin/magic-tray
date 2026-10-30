// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// What userland can actually SEE of the v3 Magic Mouse, measured from the
// present HID device interface paths and nothing else.
//
// The measurement has to be TRI-STATE, because the two ways of "not looking
// like scroll mode" have opposite consequences:
//
//   InModeA        the v3 paths have split into &col0x collections. The wheel
//                  is dead right now and the user has to be told.
//   NotObservable  there is no v3 path on the bus at all - the mouse is
//                  switched off, asleep, out of range or unpaired. NOTHING is
//                  proven about its mode, least of all a failure.
//   InModeB        at least one v3 path, none of them split: the shape scroll
//                  depends on.
//
// A single bool collapses the first two, and every caller that reads that bool
// as "restore did not work" then turns a mouse that is simply not here into a
// dead-wheel warning - or, worse, into a verdict that re-elevates a restore
// that already landed.
public class V3RecycleManagerTests
{
    const string V3Base =
        @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0323";
    const string Tail = @"#9&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    // Mode B: one unified path, no collection suffix.
    const string Unified = V3Base + Tail;

    // Mode A: the stack splits and the vendor collection appears beside the
    // pointer one.
    const string Col01 = V3Base + "&col01" + Tail;
    const string Col02 = V3Base + "&col02" + Tail;

    // Another Apple HID device on the same bus. It is not this mouse and may
    // never stand in for it.
    const string Keyboard =
        @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&000205ac_pid&0239&col02#9&bt";

    [Fact]
    public void ObserveV3Mode_WithNoV3PathOnTheBus_SaysSoInsteadOfGuessing()
    {
        // The mouse is off, asleep or out of range. This is the state that used
        // to be indistinguishable from Mode A, and the one that must never be
        // reported as a dead wheel.
        Assert.Equal(ModeBObservation.NotObservable, V3RecycleManager.ObserveV3Mode([]));
        Assert.Equal(ModeBObservation.NotObservable, V3RecycleManager.ObserveV3Mode([Keyboard]));
    }

    [Fact]
    public void ObserveV3Mode_WithASplitCollection_IsModeA()
    {
        // Both collections present is the measured Mode A shape...
        Assert.Equal(ModeBObservation.InModeA, V3RecycleManager.ObserveV3Mode([Col01, Col02]));
        // ...and so is either half of it on its own: a split path means the
        // unified path scroll needs is gone.
        Assert.Equal(ModeBObservation.InModeA, V3RecycleManager.ObserveV3Mode([Col02]));
        Assert.Equal(ModeBObservation.InModeA, V3RecycleManager.ObserveV3Mode([Col01]));
        // Mid-re-enumeration both shapes can be present at once. A split path
        // is the conservative reading: scroll is not safe to claim.
        Assert.Equal(ModeBObservation.InModeA, V3RecycleManager.ObserveV3Mode([Unified, Col02]));
        // Another device's collection does not make this mouse split.
        Assert.Equal(ModeBObservation.InModeB, V3RecycleManager.ObserveV3Mode([Unified, Keyboard]));
    }

    [Fact]
    public void ObserveV3Mode_WithOneUnifiedPath_IsModeB()
    {
        Assert.Equal(ModeBObservation.InModeB, V3RecycleManager.ObserveV3Mode([Unified]));
    }

    [Fact]
    public void ObserveV3Mode_ReadsPathsTheWayWindowsSpellsThem()
    {
        // Device interface paths arrive in whatever case the stack felt like.
        Assert.Equal(
            ModeBObservation.InModeB,
            V3RecycleManager.ObserveV3Mode([Unified.ToUpperInvariant()]));
        Assert.Equal(
            ModeBObservation.InModeA,
            V3RecycleManager.ObserveV3Mode([Col02.ToUpperInvariant()]));
    }

    [Fact]
    public void ObserveV3Mode_AbsenceIsNeverOneOfTheTwoModes()
    {
        // Stated as its own property because this is the whole point of the
        // third state: an empty bus is not Mode A and not Mode B, and any
        // caller that branches on two values has to handle it explicitly.
        var absent = V3RecycleManager.ObserveV3Mode([]);

        Assert.NotEqual(ModeBObservation.InModeA, absent);
        Assert.NotEqual(ModeBObservation.InModeB, absent);
    }
}
