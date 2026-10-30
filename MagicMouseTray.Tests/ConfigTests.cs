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
        var learned = new BatteryChannelAnswer("col02", BatteryChannelAnswer.Input90, 0x90);
        cfg.SetBatteryChannel("0269", learned);

        Assert.True(cfg.ClearBatteryChannel("0269", learned));
        Assert.Null(cfg.GetBatteryChannel("0269"));
        Assert.Null(Config.Load(_path).GetBatteryChannel("0269"));

        // Nothing left to forget - the caller uses this to log one relearn, not one per poll.
        // Still false for the same reason as before the expected= guard landed: there is no
        // entry under this PID at all, so there is nothing to compare against.
        Assert.False(cfg.ClearBatteryChannel("0269", learned));
    }

    // A poll read that was abandoned mid-cycle keeps running and can spend its third strike
    // after the next cycle has already learned a NEW channel for the same mouse. Without the
    // expected= guard that late Forget deletes the fresh answer and the device relearns from
    // scratch every few minutes - and the file loses a channel nothing was wrong with.
    [Fact]
    public void BatteryChannel_ClearingWithAStaleAnswer_LeavesTheLearnedOneAlone()
    {
        var cfg = Config.Load(_path);
        var stale = new BatteryChannelAnswer("col02", BatteryChannelAnswer.Input90, 0x90);
        var learned = new BatteryChannelAnswer(BatteryChannelAnswer.NoCollection, BatteryChannelAnswer.Feature47, 0x47);
        cfg.SetBatteryChannel("0269", learned);

        Assert.False(cfg.ClearBatteryChannel("0269", stale));
        Assert.Equal(learned, cfg.GetBatteryChannel("0269"));
        Assert.Equal(learned, Config.Load(_path).GetBatteryChannel("0269"));

        // The caller that holds the answer actually in force still clears it.
        Assert.True(cfg.ClearBatteryChannel("0269", learned));
        Assert.Null(cfg.GetBatteryChannel("0269"));
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

    // Pins the `lock (_gate)` in Config.SetThreshold. Delete that lock and the real failure
    // is not a lost threshold: the unguarded insert lands inside Persist()'s `foreach (var kv
    // in _thresholds)`, MoveNext throws InvalidOperationException, the bare `catch { }` at the
    // end of Persist swallows it, and the WHOLE save is dropped. config.ini keeps its previous
    // contents, nothing logs it and nothing retries it - so the battery channel the poll
    // thread just learned is gone at the next restart and the mouse is probe-walked again.
    [Fact]
    public void SetThreshold_FromAnotherThread_CannotSwallowAConcurrentSave() =>
        AssertAConcurrentWriterCannotSwallowASave("0fa1", (cfg, key) => cfg.SetThreshold(key, 10));

    // Pins the `lock (_gate)` in Config.SetDeviceEnabled, which fails exactly the same way:
    // Persist() enumerates _deviceEnabled too, and since the learned battery channel landed
    // Persist() also runs on the poll thread. An unguarded toggle from the UI thread throws
    // out of that enumeration into the same bare catch and drops the entire save silently.
    [Fact]
    public void SetDeviceEnabled_FromAnotherThread_CannotSwallowAConcurrentSave() =>
        AssertAConcurrentWriterCannotSwallowASave("0fa2", (cfg, key) => cfg.SetDeviceEnabled(key, false));

    // Seeded entries in the dictionary the background thread mutates. A wide dictionary is a
    // wide enumeration window inside Persist(), which is where the unguarded write has to land.
    const int GateSeedKeys = 500;
    const int GateObservations = 25;
    const int GateRounds = 2;

    // Drives the documented CONSEQUENCE of the missing lock rather than the race itself: a
    // thread that persists must always be able to read its own save back off disk.
    //
    // The only timing-dependent event on the pass path is catching a half-written file, and
    // per observation that is handled by skipping, never by failing. Persist() writes the single
    // battery_channel line LAST, so a snapshot whose final line is a terminated
    // battery_channel_<pid>= line is necessarily a whole file; anything else is a torn read
    // and is not counted as an observation at all. A complete snapshot carrying the PREVIOUS
    // report id is a dropped save, and nothing on the pass path depends on interleaving, core
    // count or scheduling.
    //
    // In aggregate there is exactly one timing-dependent failure mode: the
    // Assert.True(complete > 0, ...) floor at the end of this method fails if EVERY observation
    // across both rounds tore. That floor is deliberate - without it a run that observed nothing
    // would report green - so a red here with the "observed nothing" message means the runner was
    // loaded enough to tear all 50 reads, not that a save was dropped. A dropped save fails the
    // SECOND assert instead, and names the observation it first happened on. For scale: on an
    // idle 8-core Linux host 25 to 43 of the 50 snapshots came back complete, with zero dropped.
    static void AssertAConcurrentWriterCannotSwallowASave(string pid, Action<Config, string> write)
    {
        var expectedKey = Config.BatteryChannelKeyPrefix + pid + "=";
        int complete = 0, dropped = 0, firstDrop = -1;

        for (int round = 0; round < GateRounds; round++)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mm-tray-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "config.ini");
            int stop = 0;
            Thread? hammer = null;
            try
            {
                // write() is Config.SetThreshold / SetDeviceEnabled, each of which calls
                // Logger.Log after releasing _gate, and the hammer thread below is
                // deliberately unthrottled - throttling it would narrow the enumeration
                // window these gates exist to hit. So aim the log at this round's own
                // directory, which is deleted with it, instead of at the developer's
                // installed application log.
                Logger.LogPathOverride = Path.Combine(dir, "debug.log");

                var cfg = Config.Load(path);
                for (int i = 0; i < GateSeedKeys; i++) write(cfg, $"seed{round}x{i}");

                hammer = new Thread(() =>
                {
                    for (int i = 0; Volatile.Read(ref stop) == 0; i++) write(cfg, $"hammer{round}x{i}");
                })
                { IsBackground = true };
                hammer.Start();

                for (int i = 0; i < GateObservations; i++)
                {
                    var answer = new BatteryChannelAnswer("col02", BatteryChannelAnswer.Input90, (byte)(0x10 + i));
                    Assert.True(cfg.SetBatteryChannel(pid, answer));

                    var text = ReadWhileAnotherThreadMayBeWriting(path);
                    if (text.Length == 0 || text[^1] != '\n') continue;
                    var lastLine = text.TrimEnd('\n', '\r');
                    lastLine = lastLine[(lastLine.LastIndexOf('\n') + 1)..].TrimEnd('\r');
                    if (!lastLine.StartsWith(expectedKey, StringComparison.Ordinal)) continue;

                    complete++;
                    if (lastLine != expectedKey + answer.Value)
                    {
                        dropped++;
                        if (firstDrop < 0) firstDrop = complete;
                    }
                }
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                hammer?.Join();
                // Restored after the join, never before it: the hammer thread is still
                // calling Logger.Log until it stops, and the directory holding that log is
                // deleted on the next line.
                Logger.LogPathOverride = null;
                try { Directory.Delete(dir, recursive: true); }
                catch { }
            }
        }

        Assert.True(complete > 0, "no complete snapshot was ever read back - this test observed nothing");
        Assert.True(dropped == 0,
            $"{dropped} of {complete} saves were silently dropped (first at complete observation {firstDrop}): " +
            "a write to another Config dictionary invalidated Persist()'s enumeration and the bare catch ate the save.");
    }

    // FileShare.ReadWrite so the reader never blocks the writer - the point is to sample the
    // file WHILE it is being rewritten, and a torn sample is discarded by the caller.
    static string ReadWhileAnotherThreadMayBeWriting(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
