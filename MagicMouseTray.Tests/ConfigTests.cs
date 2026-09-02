// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class ConfigTests : IDisposable
{
    readonly string _dir;
    readonly string _path;

    public ConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mm-tray-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.ini");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public void MissingEnabledKey_DefaultsTrue()
    {
        var cfg = Config.Load(_path);
        Assert.True(cfg.IsDeviceEnabled("030d"));
        Assert.True(cfg.IsDeviceEnabled("0323"));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void SetDeviceEnabled_030dFalse_IndependentOf0323()
    {
        var cfg = Config.Load(_path);
        cfg.SetDeviceEnabled("030d", false);

        Assert.False(cfg.IsDeviceEnabled("030d"));
        Assert.True(cfg.IsDeviceEnabled("0323"));

        var ini = File.ReadAllText(_path);
        Assert.Contains("enabled_030d=false", ini);
        Assert.DoesNotContain("enabled_0323=", ini);

        var reloaded = Config.Load(_path);
        Assert.False(reloaded.IsDeviceEnabled("030d"));
        Assert.True(reloaded.IsDeviceEnabled("0323"));
    }

    [Fact]
    public void SetDeviceEnabled_PersistsTrueAndFalseIndependently()
    {
        var cfg = Config.Load(_path);
        cfg.SetDeviceEnabled("030d", false);
        cfg.SetDeviceEnabled("0323", true);

        Assert.False(cfg.IsDeviceEnabled("030d"));
        Assert.True(cfg.IsDeviceEnabled("0323"));

        var ini = File.ReadAllText(_path);
        Assert.Contains("enabled_030d=false", ini);
        Assert.Contains("enabled_0323=true", ini);

        var reloaded = Config.Load(_path);
        Assert.False(reloaded.IsDeviceEnabled("030d"));
        Assert.True(reloaded.IsDeviceEnabled("0323"));
    }

    [Fact]
    public void Enabled_DoesNotChangeThreshold()
    {
        var cfg = Config.Load(_path);
        cfg.SetThreshold("030d", 10);
        cfg.SetDeviceEnabled("030d", false);

        Assert.Equal(10, cfg.GetThreshold("030d"));
        Assert.False(cfg.IsDeviceEnabled("030d"));

        var ini = File.ReadAllText(_path);
        Assert.Contains("threshold_030d=10", ini);
        Assert.Contains("enabled_030d=false", ini);

        var reloaded = Config.Load(_path);
        Assert.Equal(10, reloaded.GetThreshold("030d"));
        Assert.False(reloaded.IsDeviceEnabled("030d"));
        Assert.True(reloaded.IsDeviceEnabled("0269"));
    }

    [Fact]
    public void DefaultGlobalThreshold_Is10()
    {
        var cfg = Config.Load(_path);
        Assert.Equal(10, cfg.GlobalThreshold);
        Assert.Equal(10, cfg.GetThreshold("030d"));
        Assert.Equal(new[] { 10, 5, 1 }, Config.ThresholdChoices);
        Assert.DoesNotContain(15, Config.ThresholdChoices);
        Assert.DoesNotContain(20, Config.ThresholdChoices);
        Assert.DoesNotContain(25, Config.ThresholdChoices);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(25)]
    public void SetThreshold_LegacyPercents_Rejected(int pct)
    {
        var cfg = Config.Load(_path);
        cfg.SetThreshold("030d", pct);
        cfg.SetGlobalThreshold(pct);

        Assert.Equal(10, cfg.GlobalThreshold);
        Assert.Equal(10, cfg.GetThreshold("030d"));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Load_LegacyThreshold20_IsNotLiveFloor()
    {
        File.WriteAllText(_path, "threshold=20\nthreshold_030d=20\n");
        var cfg = Config.Load(_path);

        Assert.Equal(10, cfg.GlobalThreshold);
        Assert.Equal(10, cfg.GetThreshold("030d"));

        cfg.SetThreshold("030d", 10);
        var ini = File.ReadAllText(_path);
        Assert.Contains("threshold=10", ini);
        Assert.Contains("threshold_030d=10", ini);
        Assert.DoesNotContain("threshold=20", ini);
        Assert.DoesNotContain("threshold_030d=20", ini);
    }

    [Fact]
    public void SetThreshold_10_Persists()
    {
        var cfg = Config.Load(_path);
        cfg.SetGlobalThreshold(10);
        cfg.SetThreshold("030d", 10);

        Assert.Equal(10, cfg.GlobalThreshold);
        Assert.Equal(10, cfg.GetThreshold("030d"));

        var ini = File.ReadAllText(_path);
        Assert.Contains("threshold=10", ini);
        Assert.Contains("threshold_030d=10", ini);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(5)]
    [InlineData(1)]
    public void IsValid_Accepts10_5_1(int pct)
    {
        Assert.True(Config.IsValid(pct));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(15)]
    [InlineData(25)]
    [InlineData(0)]
    [InlineData(11)]
    public void IsValid_RejectsNonChoices(int pct)
    {
        Assert.False(Config.IsValid(pct));
    }

    [Fact]
    public void SetThreshold_5_And_1_Persist()
    {
        var cfg = Config.Load(_path);
        cfg.SetGlobalThreshold(5);
        cfg.SetThreshold("030d", 1);

        Assert.Equal(5, cfg.GlobalThreshold);
        Assert.Equal(1, cfg.GetThreshold("030d"));

        var ini = File.ReadAllText(_path);
        Assert.Contains("threshold=5", ini);
        Assert.Contains("threshold_030d=1", ini);

        var reloaded = Config.Load(_path);
        Assert.Equal(5, reloaded.GlobalThreshold);
        Assert.Equal(1, reloaded.GetThreshold("030d"));
    }

    [Fact]
    public void Load_Threshold5_IsLiveFloor()
    {
        File.WriteAllText(_path, "threshold=5\nthreshold_0323=1\n");
        var cfg = Config.Load(_path);
        Assert.Equal(5, cfg.GlobalThreshold);
        Assert.Equal(1, cfg.GetThreshold("0323"));
        Assert.Equal(5, cfg.GetThreshold("030d"));
    }
}
