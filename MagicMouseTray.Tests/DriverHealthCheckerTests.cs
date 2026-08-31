// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DriverHealthCheckerTests
{
    [Theory]
    [InlineData("MagicMouseDriver", "0323", true)]
    [InlineData("magicmousedriver", "0323", true)]
    [InlineData("applewirelessmouse", "0323", true)] // legacy 0323 bind: status-ok, do not nag
    [InlineData("applewirelessmouse", "030d", true)]
    [InlineData("applewirelessmouse", "030D", true)]
    [InlineData("applewirelessmouse", "0269", true)]
    [InlineData("applewirelessmouse", "0310", true)]
    [InlineData("MagicMouseDriver", "030d", false)] // 030D stays applewirelessmouse
    [InlineData("MagicMouseDriver", "0269", false)]
    [InlineData("MagicMouseDriver", "0310", false)]
    [InlineData("otherfilter", "0323", false)]
    [InlineData("", "0323", false)]
    public void FilterMatchesPid_HonorsLiveSplit(string filter, string pid, bool expected)
    {
        Assert.Equal(expected, DriverHealthChecker.FilterMatchesPid(filter, pid));
    }

    [Fact]
    public void FiltersIncludeMatch_0323_AcceptsSoleKmdf()
    {
        Assert.True(DriverHealthChecker.FiltersIncludeMatch(["MagicMouseDriver"], "0323"));
        Assert.False(DriverHealthChecker.FiltersIncludeMatch(["MagicMouseDriver"], "030d"));
        Assert.True(DriverHealthChecker.FiltersIncludeMatch(["applewirelessmouse"], "030d"));
        // Dual-filter on 0323 still counts as bound (we do not offer to change it).
        Assert.True(DriverHealthChecker.FiltersIncludeMatch(["MagicMouseDriver", "applewirelessmouse"], "0323"));
    }

    [Fact]
    public void PreferredBoundFilter_PrefersKmdfOn0323()
    {
        Assert.Equal("MagicMouseDriver",
            DriverHealthChecker.PreferredBoundFilter(["applewirelessmouse", "MagicMouseDriver"], "0323"));
        Assert.Equal("applewirelessmouse",
            DriverHealthChecker.PreferredBoundFilter(["applewirelessmouse"], "0323"));
        Assert.Equal("applewirelessmouse",
            DriverHealthChecker.PreferredBoundFilter(["applewirelessmouse"], "030d"));
        Assert.Null(DriverHealthChecker.PreferredBoundFilter(["MagicMouseDriver"], "030d"));
        Assert.Null(DriverHealthChecker.PreferredBoundFilter(null, "0323"));
    }

    [Fact]
    public void IsV3Pid_Only0323()
    {
        Assert.True(DriverHealthChecker.IsV3Pid("0323"));
        Assert.True(DriverHealthChecker.IsV3Pid("0323"));
        Assert.False(DriverHealthChecker.IsV3Pid("030d"));
        Assert.False(DriverHealthChecker.IsV3Pid("0269"));
    }
}
