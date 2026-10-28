// SPDX-License-Identifier: MIT
using System.Reflection;
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

    // --- Legacy "logi" enable key, after the per-product Logitech PID split -----------------
    //
    // Before this change every Logitech interface reported Pid "logi", so a user who turned
    // EnableThirdParty on and then unchecked their Logitech row holds enabled_logi=false in
    // config.ini. Pid is now DeviceRegistry.ExtractPid(path) - one identity per product - and
    // IsDeviceEnabled reads an absent key as enabled, so that key now matches nothing:
    // AdaptivePoller.ShouldSkipPid answers false for the new per-product PID and the device the
    // user switched off is polled and shown again, with the stale line left in the file.
    // EnableThirdParty shipping off protects the user who never turned it on; it is exactly the
    // user who DID turn it on who holds the key, and a silently reversed preference is not an
    // acceptable upgrade.
    //
    // Decided semantics: enabled_logi=false was the user saying "do not poll my Logitech
    // hardware" - the only thing it COULD say, since one key covered the whole vendor - so it
    // keeps suppressing every Logitech product until the user says otherwise per product.
    //   legacy false, no per-product key  -> suppressed, for any Logitech product
    //   legacy true or absent             -> enabled; the legacy key can only ever suppress
    //   explicit enabled_<pid>            -> always wins, in both directions
    //   non-Logitech kind, or a caller that does not know the kind -> never affected
    //   on save the legacy line is RETAINED, never rewritten into per-product lines: which
    //   Logitech products this user owns is not knowable from the config file, so expanding it
    //   is guesswork and dropping it re-enables the rest of them at the next Persist.
    //
    // Required production change, minimal and source-compatible with every current call site:
    //
    //     internal bool IsDeviceEnabled(string pid, DeviceKind? kind = null)
    //
    // ordered: explicit per-product key, then the legacy "logi" key when kind is
    // DeviceKind.LogitechMouse, then the existing default of enabled. The call sites that know
    // the kind (AdaptivePoller.ShouldSkipPid, which already has it in scope at both call sites,
    // and the tray row builder) pass it; every other call site stays as it is.
    //
    // That method does not exist yet, which is what makes the cases below red. They reach it by
    // reflection ONLY so the rest of the suite still builds against today's Config; the day it
    // lands, this helper's body becomes `cfg.IsDeviceEnabled(pid, kind)` and the reflection goes.
    static bool Enabled(Config cfg, string pid, DeviceKind? kind)
    {
        var m = typeof(Config).GetMethod(
            nameof(Config.IsDeviceEnabled),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(string), typeof(DeviceKind?) },
            modifiers: null);
        Assert.True(m is not null,
            "Config.IsDeviceEnabled(string pid, DeviceKind? kind = null) does not exist: a legacy "
            + "enabled_logi=false must keep suppressing Logitech products after the per-product "
            + "PID split, or a device the user switched off comes back on upgrade.");
        Assert.True(m!.GetParameters()[1].HasDefaultValue,
            "kind must be optional, so the call sites that only hold a PID keep compiling unchanged.");
        return (bool)m.Invoke(cfg, new object?[] { pid, kind })!;
    }

    // b034 and 4069 are two different Logitech products: under the old identity both were
    // "logi", under the new one they are two rows and two checkboxes.
    [Fact]
    public void LegacyLogiKeyFalse_StillSuppressesEveryLogitechProduct()
    {
        File.WriteAllText(_path, "enable_third_party=true\nenabled_logi=false\n");
        var cfg = Config.Load(_path);
        Assert.True(cfg.EnableThirdParty);

        Assert.False(Enabled(cfg, "b034", DeviceKind.LogitechMouse));
        Assert.False(Enabled(cfg, "4069", DeviceKind.LogitechMouse));
        // ExtractPid's documented fallback for an interface with no pid token is "0000", and
        // that row is a Logitech row like any other.
        Assert.False(Enabled(cfg, "0000", DeviceKind.LogitechMouse));

        // A caller that does not know the vendor keeps today's answer: the legacy key is a
        // statement about Logitech hardware, so it needs the kind. The poller is the gate that
        // matters and it has the kind in scope.
        Assert.True(Enabled(cfg, "b034", null));
        Assert.True(cfg.IsDeviceEnabled("b034"));

        // The vendor-wide key is not an explicit line for this product. HasDeviceEnabledEntry
        // means "the user toggled THIS pid here", which the repair planner reads as "this PC
        // owns the device"; a key written for a different identity cannot claim that.
        Assert.False(cfg.HasDeviceEnabledEntry("b034"));
    }

    [Theory]
    [InlineData("enable_third_party=true\nenabled_logi=true\n")]  // explicitly left on
    [InlineData("enable_third_party=true\n")]                     // never toggled at all
    public void LegacyLogiKeyTrueOrAbsent_LeavesLogitechProductsEnabled(string ini)
    {
        File.WriteAllText(_path, ini);
        var cfg = Config.Load(_path);

        Assert.True(Enabled(cfg, "b034", DeviceKind.LogitechMouse));
        Assert.True(Enabled(cfg, "4069", DeviceKind.LogitechMouse));
    }

    [Fact]
    public void ExplicitPerProductKey_WinsOverTheLegacyLogiKey()
    {
        // The user has since re-enabled one product under its own identity.
        File.WriteAllText(_path, "enable_third_party=true\nenabled_logi=false\nenabled_b034=true\n");
        var cfg = Config.Load(_path);
        Assert.True(Enabled(cfg, "b034", DeviceKind.LogitechMouse));
        Assert.False(Enabled(cfg, "4069", DeviceKind.LogitechMouse));

        // And the other direction: a per-product false is not undone by a legacy true.
        File.WriteAllText(_path, "enable_third_party=true\nenabled_logi=true\nenabled_b034=false\n");
        var later = Config.Load(_path);
        Assert.False(Enabled(later, "b034", DeviceKind.LogitechMouse));
        Assert.True(Enabled(later, "4069", DeviceKind.LogitechMouse));
    }

    [Theory]
    [InlineData(DeviceKind.MagicMouseV3)]
    [InlineData(DeviceKind.MagicKeyboard)]
    [InlineData(DeviceKind.MagicTrackpadV2)]
    public void LegacyLogiKey_NeverReachesANonLogitechDevice(DeviceKind kind)
    {
        File.WriteAllText(_path, "enabled_logi=false\n");
        var cfg = Config.Load(_path);

        Assert.True(Enabled(cfg, "030d", kind));
        Assert.True(Enabled(cfg, "0323", kind));
        // Including the unparseable-path fallback, which an Apple interface can also land on.
        Assert.True(Enabled(cfg, "0000", kind));
    }

    [Fact]
    public void Persist_RetainsTheLegacyLogiKey_WhenAPerProductRowIsToggled()
    {
        File.WriteAllText(_path, "enable_third_party=true\nenabled_logi=false\n");
        var cfg = Config.Load(_path);
        cfg.SetDeviceEnabled("b034", true);

        // Rewriting the file must not drop the line: it is still the only record that the
        // user's other Logitech products are switched off.
        var ini = File.ReadAllText(_path);
        Assert.Contains("enabled_logi=false", ini);
        Assert.Contains("enabled_b034=true", ini);

        var reloaded = Config.Load(_path);
        Assert.True(Enabled(reloaded, "b034", DeviceKind.LogitechMouse));
        Assert.False(Enabled(reloaded, "4069", DeviceKind.LogitechMouse));
    }
}
