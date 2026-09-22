// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// The KMDF filter's Diag block is the only trace the two silent v3 defects
// leave where usermode can see them, and every decision made from it is a
// decision about MISSING or MOVING evidence. So this file tests exactly that:
// what the reader says when a value is absent, when the key is absent, when the
// token may not look, when a counter climbs, and when a driver reinstall has
// zeroed it.
//
// The registry is lifted out behind IDiagValueSource - one method, one named
// value - so every one of these runs with no driver, no mouse and no hive. What
// stays untested is RegistryDiagValueSource itself and
// FilterDiagReader.ResolveFromLiveStack, for the same reason DeviceDiagReader's
// PointerChildLive stays untested (DeviceDiagReaderTests.cs:14-18): they need a
// live HKLM\...\Services and HKLM\...\Enum\HID, and faking them would only test
// the fake.
//
// Isolation is by unique service name per test, the convention this repo already
// uses for DeviceDiagReader's per-service sampling state
// (DeviceDiagReaderTests.cs:20-24): that state keeps static per-service history
// on purpose - a real run must never forget a proven advance - so distinct
// names, not a reset hook, are what keep tests out of each other's state. The
// battery probe no longer keeps any: its pair is taken around one read and
// handed in, which is what these tests drive.
public class FilterDiagReaderTests
{
    static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N");

    static readonly DateTimeOffset T0 = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    // The measured truncation frame: A1-90-04-16, whose 0x16 is the 22 % the
    // filter discarded on the reference PC.
    static readonly byte[] TruncationFrame = [0xA1, 0x90, 0x04, 0x16];

    // Stands in for one service's Diag key. Records which service it was asked
    // about, because "which key did you actually read" is itself a decision the
    // reader must get right.
    sealed class FakeDiagValues : IDiagValueSource
    {
        readonly Dictionary<string, DiagValue> _values = new(StringComparer.OrdinalIgnoreCase);

        internal List<string> AskedServices { get; } = new();

        // When set, the KEY answered rather than the value: missing, or
        // unreadable by this token.
        internal DiagValueStatus? KeyAnswer { get; set; }

        internal FakeDiagValues Set(string name, object raw)
        {
            _values[name] = DiagValue.Present(raw);
            return this;
        }

        internal FakeDiagValues Drop(string name)
        {
            _values.Remove(name);
            return this;
        }

        public DiagValue Read(string serviceKeyName, string valueName)
        {
            AskedServices.Add(serviceKeyName);
            if (KeyAnswer is DiagValueStatus answer)
                return new DiagValue(answer, null);
            return _values.TryGetValue(valueName, out var value) ? value : DiagValue.Missing;
        }
    }

    // REG_DWORD arrives from the hive as a boxed int, high bit included once a
    // counter passes 2^31 - which these counters do.
    static object Dword(ulong value) => unchecked((int)(uint)value);

    // A complete, healthy-looking Diag block: every value the snapshot carries
    // is present, so the reader has no excuse for anything but Ok.
    static FakeDiagValues FullBlock(ulong rid12) => new FakeDiagValues()
        .Set("Rid12Count", Dword(rid12))
        .Set("AclTranslateCount", Dword(142271))
        .Set("AclInterceptCount", Dword(3117))
        .Set("AclOutCount", Dword(58))
        .Set("LastAclReceived", Dword(23))
        .Set("LastAclCapacity", Dword(9))
        .Set("LastOutHdr", Dword(0x41))
        .Set("LastOutBufferSize", Dword(1))
        .Set("LastAclBytes", TruncationFrame)
        .Set("ScrollStep", Dword(12))
        .Set("MtEnableStatus", Dword(0xC0000206))
        .Set("MtEnableTries", Dword(3));

    static FilterDiagReader ReaderFor(FakeDiagValues values, string service) =>
        new(values, () => service);

    [Fact]
    public void MissingValue_IsUnknownNotZero()
    {
        var service = Unique("MagicMouseDriver204Missing");
        // An older filter build simply does not publish ScrollStep. That is not
        // a fault and it is not a 0: a 0 in this block is a real measurement
        // (ScrollStep 0 would be a live configuration), so forging one from an
        // absent value would invent evidence the machine never gave us.
        var values = FullBlock(1000).Drop("ScrollStep");

        var snapshot = ReaderFor(values, service).Read();

        Assert.Equal(DiagAvailability.ValueMissing, snapshot.Availability);
        Assert.Null(snapshot.ScrollStep);
        // The values that WERE there still answer - a partial block is still
        // evidence about the counters it does publish.
        Assert.Equal(1000UL, snapshot.Rid12Count);
    }

    [Fact]
    public void LastAclBytes_RoundTripsAsBytes()
    {
        var service = Unique("MagicMouseDriver204Binary");

        var snapshot = ReaderFor(FullBlock(1000), service).Read();

        Assert.Equal(DiagAvailability.Ok, snapshot.Availability);
        // REG_BINARY stays bytes. The percentage the truncation defect throws
        // away is in this frame, so a consumer gets the frame verbatim rather
        // than a stringified value it can no longer parse.
        Assert.Equal(TruncationFrame, snapshot.LastAclBytes);
    }

    [Fact]
    public void MissingServiceKey_IsAllUnknown()
    {
        var service = Unique("MagicMouseDriver204Absent");
        var values = FullBlock(1000);
        values.KeyAnswer = DiagValueStatus.ServiceKeyMissing;

        var snapshot = ReaderFor(values, service).Read();

        Assert.Equal(DiagAvailability.ServiceKeyMissing, snapshot.Availability);
        // "The filter is not installed" must not read as "the stream delivered
        // nothing", which is what a 0 here would mean.
        Assert.Null(snapshot.Rid12Count);
        Assert.Null(snapshot.LastAclCapacity);
        Assert.Null(snapshot.LastAclBytes);
        // It still names the service it tried, so the log and the planner can
        // tell which key was absent.
        Assert.Equal(service, snapshot.ServiceKeyName);
    }

    [Fact]
    public void AccessDenied_IsDistinctFromAbsentAndCarriesNoValues()
    {
        var service = Unique("MagicMouseDriver204Denied");
        var values = FullBlock(1000);
        values.KeyAnswer = DiagValueStatus.AccessDenied;

        var snapshot = ReaderFor(values, service).Read();

        // "Cannot look" and "not there" prescribe opposite things - one is a
        // hardened machine, the other an uninstalled driver - so they may never
        // collapse into one state.
        Assert.Equal(DiagAvailability.AccessDenied, snapshot.Availability);
        Assert.Null(snapshot.Rid12Count);
        Assert.Null(snapshot.MtEnableStatus);
    }

    [Fact]
    public void UnresolvableService_ReadsTheInstalledNameAndSaysSo()
    {
        var values = FullBlock(1000);
        // Resolution failed (nothing KMDF-family on the live stack).
        var snapshot = new FilterDiagReader(values, () => null).Read();

        Assert.Equal(FilterDiagReader.FallbackServiceKeyName, snapshot.ServiceKeyName);
        // The snapshot names the service it ACTUALLY read. Reading the
        // installed-name literal is fine; naming one service while reading
        // another is not.
        Assert.All(values.AskedServices,
            asked => Assert.Equal(FilterDiagReader.FallbackServiceKeyName, asked));
    }

    [Fact]
    public void NonFamilyService_IsNeverRead()
    {
        var values = FullBlock(1000);
        // HidBth is the stock driver, not a member of either filter family, and
        // it has no Diag block. A resolver that returns something outside the
        // family must not be allowed to aim this reader at it - the same gate
        // that stops a caller-supplied string from climbing out of Services\.
        var snapshot = new FilterDiagReader(values, () => "HidBth").Read();

        Assert.Equal(FilterDiagReader.FallbackServiceKeyName, snapshot.ServiceKeyName);
        Assert.DoesNotContain("HidBth", values.AskedServices);
    }

    [Fact]
    public void TouchStreamAdvanced_CounterIncreased_IsTrue()
    {
        var service = Unique("MagicMouseDriver204Rising");
        var values = FullBlock(169000);
        var reader = ReaderFor(values, service);

        var before = reader.Read();
        values.Set("Rid12Count", Dword(169098));
        var after = reader.Read();

        Assert.True(IFilterDiagReader.TouchStreamAdvanced(before, after));
    }

    [Fact]
    public void TouchStreamAdvanced_CounterResetToZero_IsFalse()
    {
        var service = Unique("MagicMouseDriver204Reset");
        var values = FullBlock(2095323);
        var reader = ReaderFor(values, service);

        var before = reader.Read();
        // A driver reinstall zeroes every counter in the block, and a device
        // restart was measured on this machine collapsing Rid12Count
        // 2095323 -> 521. Either way the stream did not advance, and a
        // backwards counter must never be read as a huge delta.
        values.Set("Rid12Count", Dword(0));
        var afterReinstall = reader.Read();
        values.Set("Rid12Count", Dword(521));
        var afterRestart = reader.Read();

        Assert.False(IFilterDiagReader.TouchStreamAdvanced(before, afterReinstall));
        Assert.False(IFilterDiagReader.TouchStreamAdvanced(before, afterRestart));
    }

    [Fact]
    public void TouchStreamAdvanced_UnusableSnapshot_IsFalse()
    {
        var service = Unique("MagicMouseDriver204Unusable");
        var values = FullBlock(169000);
        var reader = ReaderFor(values, service);
        var before = reader.Read();

        // The counter climbed, but the second read could not see the key at
        // all. A number compared against no measurement is not a delta.
        values.Set("Rid12Count", Dword(169098));
        values.KeyAnswer = DiagValueStatus.ServiceKeyMissing;
        var unreadable = reader.Read();

        Assert.False(IFilterDiagReader.TouchStreamAdvanced(before, unreadable));
        Assert.False(IFilterDiagReader.TouchStreamAdvanced(null, before));
        Assert.False(IFilterDiagReader.TouchStreamAdvanced(before, null));
    }

    [Fact]
    public void TouchStreamAdvanced_CounterPastHighBit_StillAdvances()
    {
        var service = Unique("MagicMouseDriver204HighBit");
        var values = FullBlock(0x7FFFFFFF);
        var reader = ReaderFor(values, service);

        var before = reader.Read();
        // REG_DWORD marshals to int, so this value arrives negative. Widened
        // wrongly it would look like a counter that went backwards - i.e. a
        // healthy, busy mouse would report as mid-restart.
        values.Set("Rid12Count", Dword(0x80000001));
        var after = reader.Read();

        Assert.Equal(2147483649UL, after.Rid12Count);
        Assert.True(IFilterDiagReader.TouchStreamAdvanced(before, after));
    }

    [Fact]
    public void TouchStreamAdvanced_BuildMissingAnUnrelatedValue_StillAnswers()
    {
        var service = Unique("MagicMouseDriver204Partial");
        // A filter build that does not publish ScrollStep: the exact shape
        // MissingValue_IsUnknownNotZero pins as legitimate, and a value this
        // question never reads. While the gate was Availability.Ok - which
        // means all twelve values - one absent, unrelated value made the
        // touch-stream question permanently unanswerable on that machine, so
        // the truncation detector was silent there forever and the silence read
        // as health.
        var values = FullBlock(169000).Drop("ScrollStep");
        var reader = ReaderFor(values, service);

        var before = reader.Read();
        values.Set("Rid12Count", Dword(169098));
        var after = reader.Read();

        Assert.Equal(DiagAvailability.ValueMissing, after.Availability);
        Assert.True(IFilterDiagReader.TouchStreamAdvanced(before, after));

        // And the whole rule answers, not just the predicate: a zero report
        // beside a stream that climbed across this read is the finding.
        values.Set("Rid12Count", Dword(169200));
        var probe = DeviceDiagReader.TakeBatteryProbe(
            zeroReport: true, reader, before, T0);

        Assert.True(RepairPlanner.BatteryAnswerTruncated(probe));
    }

    [Fact]
    public void TruncationCorroboration_RequiresTheControlChannelFrame()
    {
        var service = Unique("MagicMouseDriver204Corrob");
        // The ordinary sample, measured identically on a broken AND a restored
        // filter build: the interrupt channel owns the shared last-inbound slot,
        // received 23 into capacity 9. Nothing to corroborate.
        var values = FullBlock(1000);

        Assert.False(ReaderFor(values, service).Read().TruncationCorroborated);

        // Sampled immediately after a control-channel GET_REPORT: a 78-byte
        // response received into a ONE byte caller buffer, which is the
        // truncation itself.
        values.Set("LastAclCapacity", Dword(1)).Set("LastAclReceived", Dword(78));

        Assert.True(ReaderFor(values, service).Read().TruncationCorroborated);

        // The same slot, the same frame, on a build that publishes eleven of
        // the twelve values. Requiring Availability.Ok here discarded the
        // corroboration - and with it the measured percentage the finding's
        // detail quotes - on every filter build that is not the reference one.
        Assert.True(ReaderFor(values.Drop("ScrollStep"), service).Read().TruncationCorroborated);
    }

    [Fact]
    public void BatteryProbe_PairsTheZeroReportWithTheReadsOwnInterval()
    {
        var service = Unique("MagicMouseDriver204Probe");
        var values = FullBlock(169000);
        var reader = ReaderFor(values, service);

        // No pre-read sample: no interval, so there is nothing to say about the
        // zero report whatever the counter reads.
        var blind = DeviceDiagReader.TakeBatteryProbe(
            zeroReport: true, reader, before: null, T0);

        Assert.Null(blind.DiagBefore);
        Assert.False(RepairPlanner.BatteryAnswerTruncated(blind));

        // The production shape: one sample immediately before the GET_REPORT,
        // one immediately after, and the counter climbed in between. The zero
        // report now sits beside a stream that was alive across THIS read,
        // which is the whole discriminator between the truncation defect and an
        // idle mouse.
        var before = reader.Read();
        values.Set("Rid12Count", Dword(169098));
        var probe = DeviceDiagReader.TakeBatteryProbe(
            zeroReport: true, reader, before, T0.AddSeconds(3));

        Assert.True(probe.ZeroReport);
        Assert.Same(before, probe.DiagBefore);
        Assert.True(RepairPlanner.BatteryAnswerTruncated(probe));

        // The same climb measured from a sample one poll interval old is the
        // false positive: this is the pair the old static store handed over, and
        // it accused a mouse that had been untouched for the whole interval.
        // Back-dating the pre-read sample is all it takes to reproduce it.
        var stalePair = DeviceDiagReader.TakeBatteryProbe(
            zeroReport: true,
            reader,
            before with { TakenAt = before.TakenAt - TimeSpan.FromMinutes(5) },
            T0.AddSeconds(3));

        Assert.True(stalePair.ZeroReport);
        Assert.False(RepairPlanner.BatteryAnswerTruncated(stalePair));
    }

    [Fact]
    public void BatteryProbe_AfterDeviceRestart_HasNoAdvance()
    {
        var service = Unique("MagicMouseDriver204Restart");
        var values = FullBlock(2095323);
        var reader = ReaderFor(values, service);

        var before = reader.Read();
        values.Set("Rid12Count", Dword(2095999));
        var advancing = DeviceDiagReader.TakeBatteryProbe(
            zeroReport: false, reader, before, T0);

        Assert.True(IFilterDiagReader.TouchStreamAdvanced(
            advancing.DiagBefore, advancing.DiagAfter));

        // Now the device restarts: Rid12Count collapsed 2095323 -> 521 within
        // ~14 s of a MOUSE_RID90_FAILED err=21 on this machine. A lower number
        // is a new baseline, never a negative delta - a restart must not be
        // diagnosed as a defect whose repair is another restart.
        var beforeRestart = reader.Read();
        values.Set("Rid12Count", Dword(521));
        var restarted = DeviceDiagReader.TakeBatteryProbe(
            zeroReport: null, reader, beforeRestart, T0.AddSeconds(6));

        Assert.Null(restarted.ZeroReport);
        Assert.False(IFilterDiagReader.TouchStreamAdvanced(
            restarted.DiagBefore, restarted.DiagAfter));
    }

    [Fact]
    public void PublishedProbe_ExpiresRatherThanGoingStale()
    {
        var pid = Unique("p");
        var probe = new BatteryProbe(T0, ZeroReport: true, null, null);
        DeviceDiagReader.PublishBatteryProbe(pid, probe);

        Assert.Same(probe, DeviceDiagReader.LatestBatteryProbe(pid, T0.AddMinutes(1)));
        // The poll interval stretches to a day when nothing changes, so a probe
        // the poller took hours ago is not evidence about now. null is no
        // evidence and raises nothing; a stale claim would raise a finding.
        Assert.Null(DeviceDiagReader.LatestBatteryProbe(
            pid, T0 + DeviceDiagReader.BatteryProbeMaxAge.Add(TimeSpan.FromSeconds(1))));
        Assert.Null(DeviceDiagReader.LatestBatteryProbe(Unique("p"), T0));
    }
}
