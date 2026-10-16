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

    /// <summary>
    /// The thank-you label must survive a restart: a user who already went to
    /// star the repo should never be asked again.
    /// </summary>
    [Fact]
    public void StarClicked_DefaultsFalse_AndSurvivesReload()
    {
        var cfg = Config.Load(_path);
        Assert.False(cfg.StarClicked);

        cfg.SetStarClicked(true);
        Assert.True(cfg.StarClicked);

        Assert.True(Config.Load(_path).StarClicked);
    }

    [Fact]
    public void HasDeviceEnabledEntry_OnlyTrueForAPidThatWasActuallyToggled()
    {
        // The repair planner treats "no entry" as "this PC does not own the
        // device", so an untouched PID must never look like a written one -
        // including the enabled_<pid>=true case, which IsDeviceEnabled cannot
        // tell apart from a missing key.
        var cfg = Config.Load(_path);
        Assert.False(cfg.HasDeviceEnabledEntry("030d"));
        Assert.False(cfg.HasDeviceEnabledEntry("0323"));

        cfg.SetDeviceEnabled("030d", false);
        cfg.SetDeviceEnabled("0323", true);

        Assert.True(cfg.HasDeviceEnabledEntry("030d"));
        Assert.True(cfg.HasDeviceEnabledEntry("0323"));
        Assert.False(cfg.HasDeviceEnabledEntry("0269"));

        var reloaded = Config.Load(_path);
        Assert.True(reloaded.HasDeviceEnabledEntry("030d"));
        Assert.True(reloaded.HasDeviceEnabledEntry("0323"));
        Assert.False(reloaded.HasDeviceEnabledEntry("0269"));
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

    // The learned battery answer is what lets the tray read a figured-out mouse with one HID
    // call instead of a probe walk, so it has to survive the restart that throws the in-memory
    // device objects away.
    [Fact]
    public void BatteryChannel_RoundTripsThroughTheFile()
    {
        var cfg = Config.Load(_path);
        Assert.Null(cfg.GetBatteryChannel("0269"));

        Assert.True(cfg.SetBatteryChannel("0269",
            new BatteryChannelAnswer("col02", BatteryChannelAnswer.Input90, 0x90)));
        Assert.True(cfg.SetBatteryChannel("030d",
            new BatteryChannelAnswer(BatteryChannelAnswer.NoCollection, BatteryChannelAnswer.Feature47, 0x47)));

        var reloaded = Config.Load(_path);
        Assert.Equal(new BatteryChannelAnswer("col02", BatteryChannelAnswer.Input90, 0x90),
            reloaded.GetBatteryChannel("0269"));
        Assert.Equal(new BatteryChannelAnswer(BatteryChannelAnswer.NoCollection, BatteryChannelAnswer.Feature47, 0x47),
            reloaded.GetBatteryChannel("030d"));
        Assert.Null(reloaded.GetBatteryChannel("0323"));
    }

    // Clearing is how a driver switch heals itself: the stale answer must be gone from the
    // file too, or the next run would read the channel that has just stopped working.
    [Fact]
    public void BatteryChannel_Cleared_IsGoneFromTheFileAndReportsWhetherItHadAnything()
    {
        var cfg = Config.Load(_path);
        cfg.SetBatteryChannel("0269", new BatteryChannelAnswer("col02", BatteryChannelAnswer.Input90, 0x90));

        Assert.True(cfg.ClearBatteryChannel("0269"));
        Assert.Null(cfg.GetBatteryChannel("0269"));
        Assert.Null(Config.Load(_path).GetBatteryChannel("0269"));

        // Nothing left to forget - the caller uses this to log one relearn, not one per poll.
        Assert.False(cfg.ClearBatteryChannel("0269"));
    }

    // The poll loop runs every 30 seconds and calls this after every successful read.
    // Re-learning the same answer must not touch the file: deleting it and finding it still
    // gone proves no write happened.
    [Fact]
    public void BatteryChannel_UnchangedAnswer_IsNotWrittenAgain()
    {
        var cfg = Config.Load(_path);
        var answer = new BatteryChannelAnswer("col02", BatteryChannelAnswer.Input90, 0x90);
        cfg.SetBatteryChannel("0269", answer);
        File.Delete(_path);

        Assert.False(cfg.SetBatteryChannel("0269", answer));
        Assert.False(File.Exists(_path));

        // A genuinely different answer - the driver switched and the mouse moved channel - is
        // written.
        Assert.True(cfg.SetBatteryChannel("0269",
            new BatteryChannelAnswer(BatteryChannelAnswer.NoCollection, BatteryChannelAnswer.Feature47, 0x47)));
        Assert.Contains("battery_channel_0269=none:feature47:0x47", File.ReadAllText(_path));
    }

    // config.ini is a text file a user may edit and an older build may have written. A value
    // this app cannot understand is ignored - Load must not throw, and must not lose the rest
    // of the file with it.
    [Theory]
    [InlineData("col02:input90")]            // missing report id
    [InlineData("col02:wmi:0x90")]           // channel this app never reads
    [InlineData("mouse:input90:0x90")]       // collection token that matches no interface
    [InlineData("col02:input90:zz")]         // report id that is not hex
    [InlineData("col02:input90:0x1234")]     // report id that is not a byte
    [InlineData("")]
    public void BatteryChannel_UnusableValue_IsIgnored(string value)
    {
        File.WriteAllText(_path, $"threshold=5\nbattery_channel_0269={value}\nstar_clicked=true\n");

        var cfg = Config.Load(_path);
        Assert.Null(cfg.GetBatteryChannel("0269"));
        Assert.Equal(5, cfg.GlobalThreshold);
        Assert.True(cfg.StarClicked);
    }
}
