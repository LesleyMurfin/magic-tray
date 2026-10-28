// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

// TestModeSwitch.BuildPlan is the whole Test Mode decision with the machine
// lifted out: three tri-state readings in, and either a plan the caller may
// act on (Block == None) or a plan that says why the action is impossible.
// RunAsync is deliberately untested - it starts an elevated bcdedit and shows
// a UAC prompt, and faking that would only test the fake - so everything that
// can make the tray change this PC's driver-signing policy wrongly has to be
// pinned here instead.
//
// Two failure modes shape this file, and they pull in opposite directions:
//
//   1. Flipping Test Mode when it must not be flipped. Secure Boot on means
//      Windows refuses the write, so offering the action is offering a lie;
//      and the charter (AGENTS.md:76) forbids any silent or automatic change.
//      Hence: Block must be set, and RunAsync refuses a blocked plan.
//   2. Refusing to act on a PC that is fine. The current test-signing state is
//      commonly UNREADABLE (SystemStartOptions is often absent and the bcdedit
//      fallback needs elevation), so treating "unknown" as a block would take
//      the feature away from exactly the users who need it.
//
// The prompt assertions are about user-visible PROMISES the charter requires -
// the command, the reboot, the BitLocker recovery key, the revert - not about
// exact phrasing, which is free to change.
public class TestModeSwitchTests
{
    static TestModePlan Enable(
        bool? secureBootOn = null, bool? testSigningOn = null, bool? bitLocker = null) =>
        TestModeSwitch.BuildPlan(TestModeAction.Enable, secureBootOn, testSigningOn, bitLocker);

    static TestModePlan Disable(
        bool? secureBootOn = null, bool? testSigningOn = null, bool? bitLocker = null) =>
        TestModeSwitch.BuildPlan(TestModeAction.Disable, secureBootOn, testSigningOn, bitLocker);

    [Fact]
    public void EnableOnAnOrdinaryPc_IsAllowedAndNamesTheExactCommand()
    {
        var plan = Enable(secureBootOn: false, testSigningOn: false, bitLocker: false);

        Assert.Equal(TestModeBlock.None, plan.Block);
        Assert.Equal(TestModeAction.Enable, plan.Action);
        Assert.Equal("bcdedit /set testsigning on", plan.Command);
    }

    [Fact]
    public void DisableOnAnOrdinaryPc_IsAllowedAndNamesTheExactCommand()
    {
        var plan = Disable(secureBootOn: false, testSigningOn: true, bitLocker: false);

        Assert.Equal(TestModeBlock.None, plan.Block);
        Assert.Equal("bcdedit /set testsigning off", plan.Command);
    }

    [Fact]
    public void SecureBootOn_BlocksEnable()
    {
        // Windows rejects the write with "The value is protected by Secure Boot
        // policy", so an Enable here could only ever fail. Secure Boot is
        // firmware and the tray must never attempt to change it.
        var plan = Enable(secureBootOn: true, testSigningOn: false);

        Assert.Equal(TestModeBlock.SecureBootOn, plan.Block);
        Assert.Contains("Secure Boot", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("UEFI", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("Nothing has been changed", plan.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SecureBootOn_DoesNotBlockDisable()
    {
        // Turning test signing OFF is always permitted and always safe, so the
        // revert the charter demands has to stay available even on a PC whose
        // Enable is blocked - including one left in Test Mode before Secure
        // Boot was switched back on.
        var plan = Disable(secureBootOn: true, testSigningOn: true);

        Assert.Equal(TestModeBlock.None, plan.Block);
        Assert.Equal("bcdedit /set testsigning off", plan.Command);
    }

    [Fact]
    public void SecureBootUnknown_DoesNotBlockEnable()
    {
        // No evidence is not a block anywhere else in this repo and is not one
        // here: at worst bcdedit refuses and says so.
        Assert.Equal(TestModeBlock.None, Enable(secureBootOn: null, testSigningOn: false).Block);
    }

    [Fact]
    public void AlreadyOn_BlocksEnable_AndAlreadyOff_BlocksDisable()
    {
        var enable = Enable(secureBootOn: false, testSigningOn: true);
        Assert.Equal(TestModeBlock.AlreadyInThatState, enable.Block);
        Assert.Contains("already on", enable.Prompt, StringComparison.Ordinal);

        var disable = Disable(secureBootOn: false, testSigningOn: false);
        Assert.Equal(TestModeBlock.AlreadyInThatState, disable.Block);
        Assert.Contains("already off", disable.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SecureBootOnAndAlreadyOn_ReportsSecureBootFirst()
    {
        // The reason that cannot be worked around wins: a user told only
        // "already on" would turn it off, reboot, and then find they cannot
        // turn it back on.
        Assert.Equal(TestModeBlock.SecureBootOn, Enable(secureBootOn: true, testSigningOn: true).Block);
    }

    [Fact]
    public void UnknownTestSigningState_IsAllowedBothWaysAndSaysItCouldNotBeRead()
    {
        // The common case on an unelevated tray. Blocking here would make the
        // feature unavailable on most PCs; bcdedit setting a flag that is
        // already set is harmless, so act and say what was not known.
        var enable = Enable(secureBootOn: false, testSigningOn: null);
        Assert.Equal(TestModeBlock.None, enable.Block);
        Assert.Contains("could not read", enable.Prompt, StringComparison.Ordinal);

        var disable = Disable(secureBootOn: false, testSigningOn: null);
        Assert.Equal(TestModeBlock.None, disable.Block);
        Assert.Contains("could not read", disable.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AnActionThisBuildCannotTranslate_IsUnknownStateAndHasNoCommand()
    {
        // C# enums are not closed, so a cast value can reach BuildPlan. It must
        // never turn into bcdedit arguments nobody chose.
        var plan = TestModeSwitch.BuildPlan((TestModeAction)42, false, false, false);

        Assert.Equal(TestModeBlock.UnknownState, plan.Block);
        Assert.Equal(string.Empty, plan.Command);
        Assert.DoesNotContain("bcdedit", plan.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void EnablePrompt_MakesEveryPromiseTheCharterRequires()
    {
        // AGENTS.md:76: the confirmation must name the command, the reboot, the
        // BitLocker recovery-key risk and the revert path. It must also be
        // honest that this is a security downgrade, and that HVCI is the user's
        // job because the tray will never touch it.
        var prompt = Enable(secureBootOn: false, testSigningOn: false, bitLocker: true).Prompt;

        Assert.Contains("bcdedit /set testsigning on", prompt, StringComparison.Ordinal);
        Assert.Contains("administrator", prompt, StringComparison.Ordinal);
        Assert.Contains("REBOOTED", prompt, StringComparison.Ordinal);
        Assert.Contains("Test Mode\" watermark", prompt, StringComparison.Ordinal);
        Assert.Contains("Microsoft has not checked", prompt, StringComparison.Ordinal);
        Assert.Contains("recovery key", prompt, StringComparison.Ordinal);
        Assert.Contains("BitLocker", prompt, StringComparison.Ordinal);
        Assert.Contains("bcdedit /set testsigning off", prompt, StringComparison.Ordinal);
        Assert.Contains("Memory integrity", prompt, StringComparison.Ordinal);
        Assert.Contains("Magic Tray will not change that", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void DisablePrompt_NamesTheCommandTheRebootAndHowToPutItBack()
    {
        var prompt = Disable(secureBootOn: false, testSigningOn: true, bitLocker: true).Prompt;

        Assert.Contains("bcdedit /set testsigning off", prompt, StringComparison.Ordinal);
        Assert.Contains("REBOOTED", prompt, StringComparison.Ordinal);
        Assert.Contains("BitLocker", prompt, StringComparison.Ordinal);
        // The revert of a revert: this one costs scroll, and the user has to be
        // told how to get it back.
        Assert.Contains("bcdedit /set testsigning on", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BitLockerUnknown_StillWarnsAboutTheRecoveryKey()
    {
        // Win32_EncryptableVolume is admin-only, so an unelevated tray reads
        // null - which is most of the time. A warning nobody needed costs a
        // sentence; a missing one can cost the user their data.
        foreach (var reading in new bool?[] { null, true })
        {
            var prompt = Enable(secureBootOn: false, testSigningOn: false, bitLocker: reading).Prompt;
            Assert.Contains("recovery key", prompt, StringComparison.Ordinal);
            Assert.Contains("48-digit", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BitLockerMeasuredOff_DropsTheRecoveryKeyWarning()
    {
        // Only a measured "not protected" removes it, so the warning can only
        // ever be dropped on evidence.
        var prompt = Enable(secureBootOn: false, testSigningOn: false, bitLocker: false).Prompt;

        Assert.DoesNotContain("BitLocker", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("recovery key", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPlan_EverPromisesToReboot()
    {
        // The tray offers the reboot, never performs it (AGENTS.md:76). No
        // prompt may say the PC will be restarted for the user.
        foreach (var plan in new[]
        {
            Enable(secureBootOn: false, testSigningOn: false, bitLocker: true),
            Enable(secureBootOn: true, testSigningOn: false, bitLocker: true),
            Enable(secureBootOn: false, testSigningOn: true, bitLocker: true),
            Enable(secureBootOn: false, testSigningOn: null, bitLocker: null),
            Disable(secureBootOn: false, testSigningOn: true, bitLocker: true),
            Disable(secureBootOn: false, testSigningOn: false, bitLocker: true),
            Disable(secureBootOn: true, testSigningOn: null, bitLocker: null),
        })
        {
            Assert.DoesNotContain("will restart", plan.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("will reboot", plan.Prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryPlanIsOneOfTheTwoCommandsOrNoCommandAtAll()
    {
        // The only strings that may ever reach an elevated bcdedit. A plan is
        // the sole route to RunAsync, so pinning the whole output space here
        // pins what the tray can run.
        foreach (var secureBoot in new bool?[] { null, true, false })
        foreach (var testSigning in new bool?[] { null, true, false })
        foreach (var bitLocker in new bool?[] { null, true, false })
        foreach (var action in new[] { TestModeAction.Enable, TestModeAction.Disable })
        {
            var plan = TestModeSwitch.BuildPlan(action, secureBoot, testSigning, bitLocker);
            Assert.True(
                plan.Command is "bcdedit /set testsigning on" or "bcdedit /set testsigning off",
                plan.Command);
            Assert.Equal(action, plan.Action);
            Assert.False(string.IsNullOrWhiteSpace(plan.Prompt));
        }
    }
}
