// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DrainRateTrackerTests
{
    static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N");

    [Fact]
    public void HoursToThreshold_FewerThanTwoSamples_IsUnknown()
    {
        var d = Unique("drain-lt2-");
        Assert.Equal(-1, DrainRateTracker.GetHoursToThreshold(d, 50, 10));
        DrainRateTracker.Record(d, 50, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(-1, DrainRateTracker.GetHoursToThreshold(d, 50, 10));
        Assert.Equal(-1, DrainRateTracker.GetHoursToEmpty(d, 50));
    }

    [Fact]
    public void DrainRate_IsDropOverHours()
    {
        var d = Unique("drain-rate-");
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        DrainRateTracker.Record(d, 20, t0);
        DrainRateTracker.Record(d, 10, t0.AddHours(2));
        Assert.Equal(5.0, DrainRateTracker.GetDrainRatePctPerHour(d), 3);
    }

    [Fact]
    public void HoursToThreshold_At10Pct_HalfPctPerHour_Is20h()
    {
        var d = Unique("drain-20h-");
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        DrainRateTracker.Record(d, 13, t0);
        DrainRateTracker.Record(d, 10, t0.AddHours(6));
        Assert.Equal(0.5, DrainRateTracker.GetDrainRatePctPerHour(d), 3);
        Assert.Equal(20.0, DrainRateTracker.GetHoursToThreshold(d, 10, 0), 3);
        Assert.Equal(20.0, DrainRateTracker.GetHoursToEmpty(d, 10), 3);
    }
}
