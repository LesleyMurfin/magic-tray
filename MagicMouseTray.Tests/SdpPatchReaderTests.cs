// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// SdpPatchReader.Classify is the whole "is this project's SDP-cache patch
// present" decision with the registry lifted out, so it is driven here from the
// VERBATIM bytes measured on the reference PC rather than from a re-derivation
// of the descriptor layout.
//
// The registry half (ForMac / ForPid) stays untested against a hive on purpose:
// it needs HKLM\...\BTHPORT\Parameters\Devices\<mac> plus a paired Apple
// keyboard under Enum\BTHENUM, neither of which exists on a clean CI box, and
// faking the hive would only test the fake. What IS asserted here is the part
// of that half which is machine-independent: garbage or absent input is Unknown
// and never an exception, because a caller drawing a menu row must never be
// handed a throw for a device it merely cannot identify.
public class SdpPatchReaderTests
{
    // HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices
    //   \e806884b0741\CachedServices : 00010000
    // Read 2026-09-16 at Medium IL, NOT elevated. 458 bytes. DynamicCachedServices
    // \00010000 is byte-identical in every field this decision looks at.
    //
    // Landmarks inside it (all verified by the live probe, not by inspection):
    //   offset   0  36 01 c7          top SDP record, 16-bit length 0x01c7 = 455
    //   offset 174  09 02 06          HIDDescriptorList attr id, then at
    //                                 178/180/184 the 35 ea / 35 e8 / 25 e4 lengths
    //   offset 258  85 47             Report ID 0x47 - Battery Strength
    //   offset 279  81 02             COL02 Input(Var,Abs)
    //   offset 281  09 20 b1 02       <-- THE PATCH, inserted between 81 02 and c0 c0
    //   offset 285  c0 c0             COL02 / top collection close
    //   `81 02 c0 c0` : zero occurrences anywhere in the blob
    // Lines below are the probe's 64-byte output rows, pasted verbatim.
    const string PatchedHex =
          "3601c70900000a000100000900013503191124090004350d350619010009001135031900110900053503191002090006350909656e09006a0901000900093508"
        + "350619112409010009000d350f350d3506190100090013350319001109010025174170706c6520576972656c657373204b6579626f61726409010125084b6579"
        + "626f617264090102250a4170706c6520496e632e090201090111090202084009020308210902042800090205280109020635ea35e8082225e405010906a10185"
        + "01050719e029e71500250175019508810275089501810175019505050819012905910275039501910175089506150026ff00050719002aff008100c0050c0901"
        + "a101854705010906a10205060920150026ff007508950181020920b102c0c0050c0901a10185111500250175019503810175019501050c09b8810206ff000903"
        + "8102750195038101050c8512150025017501950109cd810209b3810209b4810209b5810209b68102810181018101851315002501750195010601ff090a810206"
        + "01ff090c81227501950681018509090b75089501b10275089502b101c009020735083506090409090100090209280109020a280109020b09010009020c091f40"
        + "09020d280109020e2801";

    // Same key, value 00010001, 84 bytes, read in the same pass. A plain SDP
    // service record: no Report ID 0x47 anywhere, so it is not a candidate and
    // must not be allowed to vote either way. This is the realistic "neither
    // fingerprint" input the reader meets on every single live query.
    const string NonCandidateHex =
          "35520900000a000100010900013503191200090004350d3506190100090001350319000109000935083506191200090100090200090100090201"
        + "0905ac0902020902390902030900500902042801090205090002";

    const int RidOffset = 258;
    const int MarkerOffset = 281;

    static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty));

    static byte[] Patched() => Bytes(PatchedHex);

    // The stock blob, derived by running the script's surgery
    // (scripts/kbd-patch-cachedservices.ps1:110-122) BACKWARDS on the measured
    // patched bytes: delete the four inserted bytes and give the four SDP length
    // fields their four bytes back. No unpatched Apple keyboard is paired to this
    // PC, so this is the only honest way to get a stock fixture - it is the exact
    // pre-image of a blob the script is known to have produced.
    //
    // Classify never looks at the length fields; they are restored anyway so the
    // fixture is a real SDP record and not just a byte-string with a hole in it.
    static byte[] Stock()
    {
        var patched = Patched();
        var stock = new byte[patched.Length - 4];
        Array.Copy(patched, 0, stock, 0, MarkerOffset);
        Array.Copy(patched, MarkerOffset + 4, stock, MarkerOffset, patched.Length - MarkerOffset - 4);

        int attrId = IndexOf(stock, [0x09, 0x02, 0x06]);
        stock[attrId + 10] -= 4; // descriptor string length
        stock[attrId + 6] -= 4;  // inner sequence
        stock[attrId + 4] -= 4;  // outer sequence
        int top = (stock[1] << 8) + stock[2] - 4;
        stock[1] = (byte)(top >> 8);
        stock[2] = (byte)(top & 0xFF);
        return stock;
    }

    static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        for (int i = start; i + needle.Length <= haystack.Length; i++)
        {
            int k = 0;
            while (k < needle.Length && haystack[i + k] == needle[k])
                k++;
            if (k == needle.Length)
                return i;
        }
        return -1;
    }

    static int Count(byte[] haystack, byte[] needle)
    {
        int n = 0;
        for (int i = 0; (i = IndexOf(haystack, needle, i)) >= 0; i += 1)
            n++;
        return n;
    }

    // The fixture is the measurement: if these anchors ever drift the blob above
    // stopped being the bytes that were read off the reference PC, and every
    // verdict below would be about something else.
    [Fact]
    public void LiveFixtureHasTheMeasuredShape()
    {
        var blob = Patched();
        Assert.Equal(458, blob.Length);
        Assert.Equal(RidOffset, IndexOf(blob, [0x85, 0x47]));
        Assert.Equal(MarkerOffset, IndexOf(blob, [0x09, 0x20, 0xB1, 0x02]));
        Assert.Equal(0, Count(blob, [0x81, 0x02, 0xC0, 0xC0]));
        Assert.Equal(1, Count(blob, [0x09, 0x20, 0xB1, 0x02]));
    }

    [Fact]
    public void LiveKeyboardRecordReadsApplied()
    {
        Assert.Equal(SdpPatchState.Applied, SdpPatchReader.Classify(Patched()));
    }

    [Fact]
    public void AppliedReportsTheMeasuredMarkerOffset()
    {
        var state = SdpPatchReader.Classify(Patched(), out var at);
        Assert.Equal(SdpPatchState.Applied, state);
        Assert.Equal(MarkerOffset, at);
    }

    // A re-pair rewrites CachedServices from the device's own SDP response and
    // erases the insertion. That is the factory state, not a failure: the reader
    // must name it NotApplied and the caller must present it as "nothing is
    // installed, so the percent cannot be read".
    [Fact]
    public void StockRecordReadsNotApplied()
    {
        var stock = Stock();
        Assert.Equal(454, stock.Length);
        Assert.Equal(RidOffset, IndexOf(stock, [0x85, 0x47]));
        Assert.Equal(279, IndexOf(stock, [0x81, 0x02, 0xC0, 0xC0]));
        Assert.Equal(-1, IndexOf(stock, [0x09, 0x20, 0xB1, 0x02]));

        Assert.Equal(SdpPatchState.NotApplied, SdpPatchReader.Classify(stock));
    }

    [Fact]
    public void StockRecordReportsNoMarkerOffset()
    {
        SdpPatchReader.Classify(Stock(), out var at);
        Assert.Equal(-1, at);
    }

    // The live 84-byte sibling value. No Report ID 0x47 means this blob has no
    // opinion about the battery report, and reporting NotApplied for it would be
    // a fabricated fault on every query.
    [Fact]
    public void RecordWithoutReportId47IsUnknown()
    {
        var blob = Bytes(NonCandidateHex);
        Assert.Equal(84, blob.Length);
        Assert.Equal(-1, IndexOf(blob, [0x85, 0x47]));
        Assert.Equal(SdpPatchState.Unknown, SdpPatchReader.Classify(blob));
    }

    // Has the RID but neither fingerprint: a descriptor shape this reader does
    // not recognise. No evidence in either direction.
    [Fact]
    public void ReportId47WithNeitherFingerprintIsUnknown()
    {
        var blob = new byte[64];
        Array.Fill(blob, (byte)0x75);
        blob[10] = 0x85;
        blob[11] = 0x47;
        Assert.Equal(SdpPatchState.Unknown, SdpPatchReader.Classify(blob));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]  // exactly "85 47" and nothing after it
    [InlineData(5)]  // one byte short of room for a fingerprint
    public void EmptyOrTruncatedRecordIsUnknown(int length)
    {
        var blob = new byte[length];
        if (length >= 2)
        {
            blob[0] = 0x85;
            blob[1] = 0x47;
        }
        Assert.Equal(SdpPatchState.Unknown, SdpPatchReader.Classify(blob));
    }

    [Fact]
    public void NullRecordIsUnknown()
    {
        Assert.Equal(SdpPatchState.Unknown, SdpPatchReader.Classify(null));
    }

    // DECISION: both fingerprints in the same window -> Applied, the marker wins.
    //
    // `09 20 b1 02` beside Report ID 0x47 has exactly one writer in the world -
    // scripts/kbd-patch-cachedservices.ps1 - whereas `81 02 c0 c0` is an
    // ordinary Input+EndCollection+EndCollection sequence that a descriptor with
    // several collections can repeat. So a record carrying both is a patched
    // COL02 plus some other collection's close, and calling it NotApplied would
    // deny evidence that is actually present. The honesty rule runs the other
    // way: never claim Applied WITHOUT the marker.
    //
    // Set-ItemProperty replaces a REG_BINARY value whole, so "half-written blob"
    // is not a state the registry can be in; the mixed case this rule really
    // covers is a second collection, not a torn write.
    [Fact]
    public void RecordWithBothFingerprintsReadsApplied()
    {
        // Real patched blob with a second `81 02 c0 c0` stamped over filler that
        // still sits inside the RID window (258 + 2 .. 258 + 98).
        var blob = Patched();
        blob[290] = 0x81;
        blob[291] = 0x02;
        blob[292] = 0xC0;
        blob[293] = 0xC0;

        Assert.Equal(MarkerOffset, IndexOf(blob, [0x09, 0x20, 0xB1, 0x02]));
        Assert.Equal(290, IndexOf(blob, [0x81, 0x02, 0xC0, 0xC0]));

        var state = SdpPatchReader.Classify(blob, out var at);
        Assert.Equal(SdpPatchState.Applied, state);
        Assert.Equal(MarkerOffset, at);
    }

    // The window is what ties a fingerprint to the BATTERY collection. The
    // script only ever searches 64 bytes past the RID and inserts at close+2, so
    // a marker far away was written by something else and proves nothing.
    [Fact]
    public void PatchMarkerFarFromReportId47IsUnknown()
    {
        var blob = new byte[400];
        Array.Fill(blob, (byte)0x75);
        blob[0] = 0x85;
        blob[1] = 0x47;
        blob[200] = 0x09;
        blob[201] = 0x20;
        blob[202] = 0xB1;
        blob[203] = 0x02;
        Assert.Equal(SdpPatchState.Unknown, SdpPatchReader.Classify(blob));
    }

    [Fact]
    public void StockCloseFarFromReportId47IsUnknown()
    {
        var blob = new byte[400];
        Array.Fill(blob, (byte)0x75);
        blob[0] = 0x85;
        blob[1] = 0x47;
        blob[200] = 0x81;
        blob[201] = 0x02;
        blob[202] = 0xC0;
        blob[203] = 0xC0;
        Assert.Equal(SdpPatchState.Unknown, SdpPatchReader.Classify(blob));
    }

    // A record whose FIRST `85 47` site is unpatched while a later one carries
    // the marker still reads Applied: one patched battery collection is enough.
    [Fact]
    public void LaterPatchedSiteOutranksAnEarlierStockSite()
    {
        var blob = new byte[400];
        Array.Fill(blob, (byte)0x75);
        blob[0] = 0x85;
        blob[1] = 0x47;
        blob[10] = 0x81;
        blob[11] = 0x02;
        blob[12] = 0xC0;
        blob[13] = 0xC0;
        blob[200] = 0x85;
        blob[201] = 0x47;
        blob[210] = 0x09;
        blob[211] = 0x20;
        blob[212] = 0xB1;
        blob[213] = 0x02;

        var state = SdpPatchReader.Classify(blob, out var at);
        Assert.Equal(SdpPatchState.Applied, state);
        Assert.Equal(210, at);
    }

    // The key this reads must be the key the script would write
    // (kbd-patch-cachedservices.ps1:23-26): 12 hex digits, lowercase,
    // separators tolerated. Anything else addresses no device, so it is Unknown
    // rather than a lookup under a bogus path.
    [Theory]
    [InlineData("e806884b0741", "e806884b0741")]
    [InlineData("E8:06:88:4B:07:41", "e806884b0741")]
    [InlineData("E8-06-88-4B-07-41", "e806884b0741")]
    [InlineData("e806884b074", null)]       // 11 digits
    [InlineData("e806884b07411", null)]     // 13 digits
    [InlineData("", null)]
    [InlineData("not-a-mac", null)]
    [InlineData(null, null)]
    public void MacNormalizationMatchesThePatchScript(string? input, string? expected)
    {
        Assert.Equal(expected, SdpPatchReader.NormalizeMac(input));
    }

    // Never throws at the caller, whatever it is handed. A menu row for a device
    // the tray cannot identify has to say "cannot tell", not crash the poller.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-mac")]
    [InlineData("e806884b074")]
    public void UnusableMacIsUnknown(string? mac)
    {
        Assert.Equal(SdpPatchState.Unknown, SdpPatchReader.ForMac(mac));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AbsentPidIsUnknown(string? pid)
    {
        Assert.Equal(SdpPatchState.Unknown, SdpPatchReader.ForPid(pid));
    }

    // No BTHENUM parent can match this, on any machine, so the answer is
    // machine-independent: a PID that resolves no live instance must never
    // inherit another device's cache verdict.
    [Fact]
    public void PidWithNoLiveInstanceIsUnknown()
    {
        Assert.Null(SdpPatchReader.MacForPid("zzzz"));
        Assert.Equal(SdpPatchState.Unknown, SdpPatchReader.ForPid("zzzz"));
    }

    // The log line SDP_PATCH mac=... state=... is the diagnostic artifact this
    // reader exists to leave behind, so its vocabulary is part of the contract.
    [Fact]
    public void LogVerdictNamesAreStable()
    {
        Assert.Equal("applied", SdpPatchReader.Describe(SdpPatchState.Applied));
        Assert.Equal("not_applied", SdpPatchReader.Describe(SdpPatchState.NotApplied));
        Assert.Equal("unknown", SdpPatchReader.Describe(SdpPatchState.Unknown));
    }
}
