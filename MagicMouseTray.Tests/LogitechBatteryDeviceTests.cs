// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class LogitechBatteryDeviceTests
{
    // The HID++ response byte gets the same floor as the Apple paths, pinned at its boundaries.
    // raw=0 and raw=101 kill relaxing the gate to `raw is >= 0 and <= 100` (which rejects
    // nothing, since the response byte is a byte); raw=1 and raw=100 kill over-tightening it.
    // The expected -1 kills the -2 sentinel swap: this path completed a read, and -2 means the
    // battery report is not exposed, which is what opens the elevated SDP-cache offer
    // (TrayMenu.ShowFixKeyboard) and RepairPlanner rule 2d. The distinct markers kill logging
    // a rejected value as a blocked read, or logging nothing at all as this path used to.
    [Theory]
    [InlineData(0, -1, "LOGI_BATTERY_ZERO")]
    [InlineData(1, 1, "LOGI_BATTERY_OK")]
    [InlineData(100, 100, "LOGI_BATTERY_OK")]
    [InlineData(101, -1, "LOGI_BATTERY_BAD")]
    public void ClassifyLevelByte_FloorIsOne_AndRejectionsAreMinusOne(
        byte raw, int expectedPct, string expectedMarker)
    {
        Assert.Equal((expectedPct, expectedMarker), LogitechBatteryDevice.ClassifyLevelByte(raw));
    }

    // --- HID++ response matching (LogitechBatteryDevice.SendReceive's accept rule) ---------
    //
    // SendReceive reads one HID++ frame and then decides whether it is OUR answer before
    // ReadFeatureBattery reads byte 4 as a feature index (Root) or a battery percentage.
    // Both halves of that decision - who sent it, and whether enough of it arrived - are
    // pinned here, because everything around them needs a live HID handle.

    // The request LogitechBatteryDevice builds: [rid][0xFF deviceIndex][featureIndex]
    // [(funcIndex << 4) | swId][arg0][arg1]. Short report = 7 bytes, long = 20.
    static byte[] Request(byte rid = 0x10, byte featureIndex = 0x06, byte funcByte = 0x05, int len = 7)
    {
        var b = new byte[len];
        b[0] = rid; b[1] = 0xFF; b[2] = featureIndex; b[3] = funcByte;
        return b;
    }

    // A frame off the wire. `transferred` bytes are real; the rest stay zero, which is what
    // GC.AllocateArray hands SendReceive and what a truncated transfer therefore leaves behind.
    // devIndex is byte 1, the HID++ device index: 0xFF is the direct-connect value every
    // request carries, and anything else is a frame addressed to another paired device.
    static byte[] Frame(byte rid, byte featureIndex, byte funcByte, byte payload,
                        byte devIndex = 0xFF, int transferred = 7, int len = 7)
    {
        var b = new byte[len];
        byte[] full = [rid, devIndex, featureIndex, funcByte, payload, 0x00, 0x00];
        for (int i = 0; i < transferred && i < len && i < full.Length; i++) b[i] = full[i];
        return b;
    }

    // The accept case: our report id, our feature index, our function+swId, full transfer.
    [Fact]
    public void IsMatchingResponse_SameRidFeatureIndexAndSwId_Matches()
    {
        Assert.True(LogitechBatteryDevice.IsMatchingResponse(
            Request(), Frame(0x10, 0x06, 0x05, payload: 55), 7));
    }

    // byte 3 is (funcIndex << 4) | swId and is the only thing separating OUR reply from
    // another function's reply or an unsolicited HID++ notification on the same feature - a
    // battery feature pushes events on the index we just queried. Accepting one of those reads
    // a stranger's byte 4 as a percentage: 0x15 (fn1 of the same feature) and 0x06 (our
    // function, someone else's software id) both reach ClassifyLevelByte today. The rid and
    // feature-index rows keep the existing two comparisons from being dropped.
    [Theory]
    [InlineData(0x11, 0x06, 0x05)] // long-report frame answering a short-report request
    [InlineData(0x10, 0x07, 0x05)] // another feature's reply
    [InlineData(0x10, 0x06, 0x15)] // same feature, function 1 instead of function 0
    [InlineData(0x10, 0x06, 0x06)] // our function, a different software id (not our request)
    public void IsMatchingResponse_DifferingRidFeatureIndexOrSwId_Rejected(
        byte rid, byte featureIndex, byte funcByte)
    {
        Assert.False(LogitechBatteryDevice.IsMatchingResponse(
            Request(), Frame(rid, featureIndex, funcByte, payload: 55), 7));
    }

    // byte 1 is the HID++ device index. Our requests are direct-connect (0xFF), but a receiver
    // or any other multiplexing interface carries frames for every paired device on one report
    // id, and discovery only excludes receivers by a short PID substring list - any receiver
    // model outside it arrives here. Such a frame shares our report id, our feature index and
    // our function|swId, so byte 1 is the ONLY thing left standing between it and
    // ClassifyLevelByte reading a DIFFERENT device's battery as this row's percentage. Every
    // other fixture in this class carries 0xFF on both sides, so the compare was never varied:
    // rewriting it to `response[0] == request[0] && true && response[2] == request[2] &&
    // response[3] == request[3]` left the whole suite green. These rows are a perfect match in
    // every other byte, so the device index is the only thing under test.
    [Theory]
    [InlineData(0x01)] // first device paired to a receiver
    [InlineData(0x02)] // second device paired to the same receiver
    [InlineData(0xFE)] // any other index the interface multiplexes
    public void IsMatchingResponse_OtherPairedDevicesIndex_Rejected(byte devIndex)
    {
        Assert.False(LogitechBatteryDevice.IsMatchingResponse(
            Request(), Frame(0x10, 0x06, 0x05, payload: 55, devIndex: devIndex), 7));

        // Same frame with our own index accepted, so the row proves the compare and not some
        // other rejection.
        Assert.True(LogitechBatteryDevice.IsMatchingResponse(
            Request(), Frame(0x10, 0x06, 0x05, payload: 55, devIndex: 0xFF), 7));
    }

    // A HID++ 2.0 error frame carries 0xFF in the feature-index byte, the failed feature in
    // byte 3 and the error code in byte 5 - so its byte 4 is the echoed function+swId byte,
    // which would decode as 5%. 0xFF has to be rejected as a value, not just as a mismatch:
    // a device whose Root reply named feature index 0xFF (nothing rejects that today - only
    // index 0 is treated as absent) would otherwise be able to match its own error frames.
    [Fact]
    public void IsMatchingResponse_Hidpp20ErrorReply_Rejected()
    {
        // Error for our feature-0x06 function-0 request: [10][FF][FF][06][05][errorCode].
        Assert.False(LogitechBatteryDevice.IsMatchingResponse(
            Request(), Frame(0x10, 0xFF, 0x06, payload: 0x05), 7));

        // Same frame when the request itself carried 0xFF as its feature index.
        Assert.False(LogitechBatteryDevice.IsMatchingResponse(
            Request(featureIndex: 0xFF), Frame(0x10, 0xFF, 0x05, payload: 0x09), 7));
    }

    // F14: only `got == 0` is checked today, so a 4-byte transfer - a complete, correctly
    // matching HID++ header with the payload missing - leaves byte 4 at the allocation's zero
    // and is decoded as battery 0, logged LOGI_BATTERY_ZERO against the device. A truncated
    // transfer is not a zero battery; it is not an answer at all. Byte 4 is the first byte
    // either caller reads (Root's feature index, the feature's percentage), so 5 bytes is the
    // minimum that can be decoded and 4 is the boundary that must flip.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]   // header matches, payload never arrived
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    public void IsMatchingResponse_TransferShorterThanFiveBytes_Rejected(int transferred, bool expected)
    {
        Assert.Equal(expected, LogitechBatteryDevice.IsMatchingResponse(
            Request(), Frame(0x10, 0x06, 0x05, payload: 55, transferred: transferred), transferred));
    }

    // Lengths that cannot describe a decodable frame are rejected, not indexed into: a buffer
    // shorter than the matched prefix, a transfer count larger than the buffer that produced it
    // (both come from the driver via GetOverlappedResult), and a malformed request.
    [Fact]
    public void IsMatchingResponse_LengthsInconsistentWithTheBuffer_RejectedWithoutThrowing()
    {
        Assert.False(LogitechBatteryDevice.IsMatchingResponse(Request(), [0x10, 0xFF, 0x06], 3));
        Assert.False(LogitechBatteryDevice.IsMatchingResponse(
            Request(), Frame(0x10, 0x06, 0x05, payload: 55), 9));
        Assert.False(LogitechBatteryDevice.IsMatchingResponse(
            [0x10, 0xFF], Frame(0x10, 0x06, 0x05, payload: 55), 7));
    }
}
