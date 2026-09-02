// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DriverHealthCheckerTests
{
    [Fact]
    public void Aggregate_BoundV1_Unbound2024_IsNotOk()
    {
        // Issue #4: mixed bound-v1 + unbound-2024 must not be global Ok.
        var devices = new DeviceDriverHealth[]
        {
            new("v1", "030d", DriverStatus.Ok, DriverPackageCatalog.AppleFilterServiceName),
            new("v3", "0323", DriverStatus.NotBound, DriverPackageCatalog.StockHidServiceName),
        };
        Assert.Equal(DriverStatus.NotBound, DriverHealthChecker.Aggregate(devices));
        Assert.NotEqual(DriverStatus.Ok, DriverHealthChecker.Aggregate(devices));
    }

    [Fact]
    public void Aggregate_BoundV1_Stock0323_IsNotOk()
    {
        var devices = new DeviceDriverHealth[]
        {
            new("v1", "030d", DriverStatus.Ok, DriverPackageCatalog.AppleFilterServiceName),
            new("v3", "0323", DriverStatus.StockKmdf, DriverPackageCatalog.StockHidServiceName),
        };
        Assert.Equal(DriverStatus.StockKmdf, DriverHealthChecker.Aggregate(devices));
        Assert.NotEqual(DriverStatus.Ok, DriverHealthChecker.Aggregate(devices));
    }

    [Fact]
    public void Aggregate_BoundV1_Patched0323_IsOk()
    {
        var devices = new DeviceDriverHealth[]
        {
            new("v1", "030d", DriverStatus.Ok, DriverPackageCatalog.AppleFilterServiceName),
            new("v3", "0323", DriverStatus.PatchedKmdf, DriverPackageCatalog.PatchedKmdfServiceName),
        };
        Assert.Equal(DriverStatus.Ok, DriverHealthChecker.Aggregate(devices));
    }

    [Fact]
    public void Aggregate_Empty_IsOk()
    {
        Assert.Equal(DriverStatus.Ok, DriverHealthChecker.Aggregate([]));
    }

    [Fact]
    public void Classify_0323_MagicMouseDriver_IsPatchedKmdf()
    {
        Assert.Equal(DriverStatus.PatchedKmdf,
            DriverHealthChecker.Classify("0323", "MagicMouseDriver", appleFilterPackagePresent: false, kmdfPackagePresent: true));
        Assert.Equal(DriverStatus.PatchedKmdf,
            DriverHealthChecker.Classify("0323", "magicmousedriver", appleFilterPackagePresent: true, kmdfPackagePresent: true));
    }

    [Fact]
    public void Classify_0323_MissingAppleLowerFilters_IsNotNotBound_WhenNoKmdfPackage()
    {
        // Live PC: LowerFilters applewirelessmouse missing, HidBth function driver.
        // Must NOT report NotBound just because AppleWirelessMouse is unbound.
        var status = DriverHealthChecker.Classify(
            "0323", "HidBth", appleFilterPackagePresent: true, kmdfPackagePresent: false);
        Assert.Equal(DriverStatus.StockKmdf, status);
        Assert.NotEqual(DriverStatus.NotBound, status);
        Assert.NotEqual(DriverStatus.Ok, status);
    }

    [Fact]
    public void Classify_0323_KmdfInstalledButHidBth_IsStockKmdf()
    {
        // Leftover MagicMouseDriver.sys on disk is not NotBound while HidBth is bound.
        var status = DriverHealthChecker.Classify(
            "0323", "HidBth", appleFilterPackagePresent: false, kmdfPackagePresent: true);
        Assert.Equal(DriverStatus.StockKmdf, status);
        Assert.NotEqual(DriverStatus.NotBound, status);
    }

    [Fact]
    public void Classify_0323_KmdfInstalledButUnbound_IsNotBound()
    {
        Assert.Equal(DriverStatus.NotBound,
            DriverHealthChecker.Classify("0323", null, appleFilterPackagePresent: false, kmdfPackagePresent: true));
        Assert.Equal(DriverStatus.NotBound,
            DriverHealthChecker.Classify("0323", "", appleFilterPackagePresent: false, kmdfPackagePresent: true));
        Assert.Equal(DriverStatus.StockKmdf,
            DriverHealthChecker.Classify("0323", null, appleFilterPackagePresent: false, kmdfPackagePresent: false));
    }

    [Fact]
    public void Classify_0323_AppleWirelessMouse_IsPathAPatched()
    {
        var status = DriverHealthChecker.Classify(
            "0323", "applewirelessmouse", appleFilterPackagePresent: true, kmdfPackagePresent: false);
        Assert.Equal(DriverStatus.PathAPatched, status);
        Assert.NotEqual(DriverStatus.StockKmdf, status);
        Assert.NotEqual(DriverStatus.PatchedKmdf, status);
        Assert.NotEqual(DriverStatus.Ok, status);
    }

    [Fact]
    public void Classify_0323_AppleWirelessMouse_WithKmdfPackage_IsPathAPatched()
    {
        // KMDF sitting on disk is not NotBound while PathA is bound.
        Assert.Equal(DriverStatus.PathAPatched,
            DriverHealthChecker.Classify("0323", "applewirelessmouse", appleFilterPackagePresent: true, kmdfPackagePresent: true));
        Assert.Equal(DriverStatus.StockKmdf,
            DriverHealthChecker.Classify("0323", "HidBth", appleFilterPackagePresent: true, kmdfPackagePresent: true));
    }

    [Fact]
    public void Classify_0323_PathAChoice_SurvivesMissingAppleLowerFilters_ModeA()
    {
        // Battery (FLIP:NoFilter) clears applewirelessmouse. Sticky pathA is not Stock.
        var modeA = DriverHealthChecker.Classify(
            "0323", "HidBth", appleFilterPackagePresent: true, kmdfPackagePresent: false,
            lastingChoice: Config.Driver0323PathA);
        Assert.Equal(DriverStatus.PathAPatched, modeA);
        Assert.NotEqual(DriverStatus.StockKmdf, modeA);
        Assert.NotEqual(DriverStatus.NotBound, modeA);

        Assert.Equal(DriverStatus.PathAPatched,
            DriverHealthChecker.Classify(
                "0323", "HidBth", appleFilterPackagePresent: true, kmdfPackagePresent: true,
                lastingChoice: Config.Driver0323PathA));

        // KMDF still wins if MagicMouseDriver is bound.
        Assert.Equal(DriverStatus.PatchedKmdf,
            DriverHealthChecker.Classify(
                "0323", "MagicMouseDriver", appleFilterPackagePresent: true, kmdfPackagePresent: true,
                lastingChoice: Config.Driver0323PathA));

        Assert.Equal(DriverStatus.StockKmdf,
            DriverHealthChecker.Classify("0323", "HidBth", true, false, lastingChoice: null));
        Assert.Equal(DriverStatus.StockKmdf,
            DriverHealthChecker.Classify("0323", "HidBth", true, true, lastingChoice: Config.Driver0323Stock));
    }

    [Fact]
    public void Classify_V1_AppleFilterBound_IsOk()
    {
        Assert.Equal(DriverStatus.Ok,
            DriverHealthChecker.Classify("030d", "applewirelessmouse", true, false));
        Assert.Equal(DriverStatus.NotBound,
            DriverHealthChecker.Classify("030d", null, true, false));
        Assert.Equal(DriverStatus.NotInstalled,
            DriverHealthChecker.Classify("0269", null, false, false));
    }

    [Fact]
    public void PreferredBoundName_0323_KmdfWinsThenAppleFilter()
    {
        Assert.Equal("MagicMouseDriver",
            DriverHealthChecker.PreferredBoundName("0323", "MagicMouseDriver", ["applewirelessmouse"]));
        Assert.Equal("MagicMouseDriver",
            DriverHealthChecker.PreferredBoundName("0323", "HidBth", ["MagicMouseDriver"]));
        Assert.Equal("applewirelessmouse",
            DriverHealthChecker.PreferredBoundName("0323", "HidBth", ["applewirelessmouse"]));
        Assert.Equal("HidBth",
            DriverHealthChecker.PreferredBoundName("0323", "HidBth", null));
        Assert.Equal("applewirelessmouse",
            DriverHealthChecker.PreferredBoundName("030d", "HidBth", ["applewirelessmouse"]));
    }

    [Fact]
    public void MergeBoundLayers_BthHidBth_HidKmdfFilter_IsPatchedKmdf()
    {
        var bound = DriverHealthChecker.MergeBoundLayers(
            "0323", "HidBth", null, null, ["MagicMouseDriver"]);
        Assert.Equal("MagicMouseDriver", bound);
        Assert.Equal(DriverStatus.PatchedKmdf,
            DriverHealthChecker.Classify("0323", bound, appleFilterPackagePresent: false, kmdfPackagePresent: true));
    }

    [Fact]
    public void MergeBoundLayers_BthHidBth_HidKmdfService_IsPatchedKmdf()
    {
        var bound = DriverHealthChecker.MergeBoundLayers(
            "0323", "HidBth", null, "MagicMouseDriver", null);
        Assert.Equal("MagicMouseDriver", bound);
        Assert.Equal(DriverStatus.PatchedKmdf,
            DriverHealthChecker.Classify("0323", bound, appleFilterPackagePresent: false, kmdfPackagePresent: true));
    }

    [Fact]
    public void MergeBoundLayers_V1_BthAppleFilter_StillOk()
    {
        var bound = DriverHealthChecker.MergeBoundLayers(
            "030d", "HidBth", ["applewirelessmouse"], "mouhid", null);
        Assert.Equal("applewirelessmouse", bound);
        Assert.Equal(DriverStatus.Ok,
            DriverHealthChecker.Classify("030d", bound, appleFilterPackagePresent: true, kmdfPackagePresent: false));
    }

    [Fact]
    public void MergeBoundLayers_HidBthOnly_IsStockKmdf()
    {
        var bound = DriverHealthChecker.MergeBoundLayers(
            "0323", "HidBth", null, null, null);
        Assert.Equal("HidBth", bound);
        Assert.Equal(DriverStatus.StockKmdf,
            DriverHealthChecker.Classify("0323", bound, appleFilterPackagePresent: false, kmdfPackagePresent: true));
    }

    [Fact]
    public void MergeBoundLayers_BthKmdfFilter_HidMouhid_IsPatchedKmdf()
    {
        // Live: BTHENUM hardware-id LowerFilters=MagicMouseDriver, instance Service=HidBth,
        // HID Service=mouhid. KMDF still wins.
        var bound = DriverHealthChecker.MergeBoundLayers(
            "0323", "HidBth", ["MagicMouseDriver"], "mouhid", null);
        Assert.Equal("MagicMouseDriver", bound);
        Assert.Equal(DriverStatus.PatchedKmdf,
            DriverHealthChecker.Classify("0323", bound, appleFilterPackagePresent: false, kmdfPackagePresent: true));
    }


    [Fact]
    public void DeviceDriverHealth_ExposesContractShape()
    {
        var row = new DeviceDriverHealth("id", "0323", DriverStatus.PatchedKmdf, "MagicMouseDriver");
        Assert.Equal("id", row.DeviceId);
        Assert.Equal("0323", row.Pid);
        Assert.Equal(DriverStatus.PatchedKmdf, row.Status);
        Assert.Equal("MagicMouseDriver", row.BoundDriverName);
    }
}
