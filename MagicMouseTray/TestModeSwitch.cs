// SPDX-License-Identifier: MIT
using System.IO;
using System.Management;
using System.Runtime.Versioning;

namespace MagicMouseTray;

// The ONE place in this repo that may change the machine's driver-signing
// policy, and it may only do it the way AGENTS.md:76 allows: explicitly asked
// for, never silent, never automatic, never on startup or a poll.
//
// The shape is deliberately split in two so the rules are testable without a
// PC: BuildPlan is pure - it takes three readings and returns either a plan
// whose Block is None, or a plan that says why the action is impossible - and
// RunAsync is the only code that starts a process, and it refuses any plan
// whose Block is not None. So "no code path can flip Test Mode without a
// confirmed, unblocked plan" is a property of two small functions, not of
// every caller's discipline.
//
// What this file will NEVER do, all of it charter (AGENTS.md:76):
//   - change HVCI / Memory integrity. There is no Device Guard write here and
//     there must never be one; the prompts say out loud that the tray will not
//     touch it and that the user has to.
//   - touch Secure Boot. Secure Boot lives in firmware. With Secure Boot on,
//     `bcdedit /set testsigning on` fails with "The value is protected by
//     Secure Boot policy", so BuildPlan reports the Enable as blocked rather
//     than running a command that cannot work.
//   - reboot the PC. Test signing only takes effect after a restart; the
//     prompt says so, and the user restarts when they choose to.
//   - show its own confirmation. The caller shows plan.Prompt - which is the
//     per-action confirmation the charter requires - and only calls RunAsync
//     if the user agreed.
//
// Disable is never blocked by Secure Boot: turning test signing OFF is always
// permitted and always safe, so the revert the charter demands is available on
// every PC, including one whose Enable is blocked.
internal enum TestModeAction { Enable, Disable }

// Why an action is impossible. None = allowed.
internal enum TestModeBlock
{
    None,

    // Secure Boot is ON and measured so. Enable only.
    SecureBootOn,

    // The PC is already in the state the action would put it in, so running
    // bcdedit would be a UAC prompt and a reboot for nothing.
    AlreadyInThatState,

    // Defensive: a TestModeAction this build cannot translate into a bcdedit
    // command, which would otherwise mean running bcdedit with arguments
    // nobody chose. Produces no command. An unreadable testSigningOn is NOT
    // this case - see BuildPlan.
    UnknownState,
}

internal sealed record TestModePlan(
    TestModeAction Action,
    string Command,          // "bcdedit /set testsigning on" | "... off"
    string Prompt,           // full confirmation body: command, reboot, BitLocker risk, revert path
    TestModeBlock Block);    // Block != None => caller must not offer the action

internal static class TestModeSwitch
{
    internal const string EnableCommand = "bcdedit /set testsigning on";
    internal const string DisableCommand = "bcdedit /set testsigning off";

    const string EnableArguments = "/set testsigning on";
    const string DisableArguments = "/set testsigning off";

    // bcdedit finishes in well under a second. A minute is generous and still
    // bounded, so a hung elevated process cannot leave the tray waiting.
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(1);

    // Pure. No IO. Every rule that decides whether this PC may be changed, and
    // every word the user is asked to agree to, is decided here from three
    // readings and nothing else.
    //
    // The readings are all tri-state, and null means "no evidence" - never a
    // fault, and never a block:
    //
    //   secureBootOn == true        Enable is impossible (Windows refuses the
    //                               write). Disable stays allowed.
    //   testSigningOn == action     AlreadyInThatState: nothing to do.
    //   testSigningOn == null       ALLOWED, both directions. The registry's
    //                               SystemStartOptions is routinely absent and
    //                               the bcdedit fallback needs elevation, so
    //                               null is the ordinary reading on an
    //                               unelevated tray. Refusing there would make
    //                               the feature unavailable on exactly the PCs
    //                               that need it; instead the prompt says the
    //                               state could not be read, and bcdedit itself
    //                               is idempotent.
    //   bitLockerProtected != false the BitLocker recovery-key warning is
    //                               shown. Unknown warns: a warning the user
    //                               did not need costs a sentence, a missing
    //                               one costs them their data.
    internal static TestModePlan BuildPlan(
        TestModeAction action, bool? secureBootOn, bool? testSigningOn, bool? bitLockerProtected)
    {
        if (action is not (TestModeAction.Enable or TestModeAction.Disable))
        {
            return new TestModePlan(
                action,
                string.Empty,
                "Magic Tray does not recognise this Test Mode request and will not run anything.",
                TestModeBlock.UnknownState);
        }

        bool enable = action == TestModeAction.Enable;
        var command = enable ? EnableCommand : DisableCommand;

        var block = TestModeBlock.None;
        if (enable && secureBootOn == true)
            block = TestModeBlock.SecureBootOn;
        else if (testSigningOn == enable)
            block = TestModeBlock.AlreadyInThatState;

        return new TestModePlan(action, command, Prompt(action, block, testSigningOn, bitLockerProtected), block);
    }

    // The confirmation body. The charter requires it to name the command, the
    // reboot and the BitLocker recovery-key risk, and to offer the revert, so
    // all of those are built here rather than left to a caller to remember.
    // A blocked plan still gets a body, because the user who clicked deserves
    // to be told why nothing will happen.
    static string Prompt(
        TestModeAction action, TestModeBlock block, bool? testSigningOn, bool? bitLockerProtected)
    {
        bool enable = action == TestModeAction.Enable;

        if (block == TestModeBlock.SecureBootOn)
        {
            return "Secure Boot is on, so Windows will not let Test Mode be turned on: "
                + EnableCommand + " fails with \"The value is protected by Secure Boot policy\".\n\n"
                + "Secure Boot is a firmware setting. It is changed in this PC's UEFI setup screen, "
                + "not from Windows, and Magic Tray will never change it for you. Turning it off "
                + "lowers this PC's boot security, and on a BitLocker-protected PC it can ask for "
                + "the 48-digit recovery key on the next boot.\n\n"
                + "Nothing has been changed.";
        }

        if (block == TestModeBlock.AlreadyInThatState)
        {
            return enable
                ? "Test Mode is already on. There is nothing to change, so Magic Tray will not run "
                    + EnableCommand + ".\n\nTo turn it back off later, run " + DisableCommand
                    + " and reboot."
                : "Test Mode is already off. There is nothing to change, so Magic Tray will not run "
                    + DisableCommand + ".";
        }

        // The unreadable-state sentence, on an action that is going ahead
        // anyway. bcdedit setting a flag that is already set is harmless, so
        // the honest thing is to act and say what was not known.
        var unknown = testSigningOn is null
            ? "Magic Tray could not read whether Test Mode is currently on, so it cannot tell you "
                + "whether this will change anything. Running it is safe either way.\n\n"
            : string.Empty;

        // Unknown counts as protected. Only a measured "not protected" removes
        // the warning.
        var bitLocker = bitLockerProtected != false
            ? (bitLockerProtected == true
                ? "This PC is protected by BitLocker. "
                : "Magic Tray could not tell whether this PC is protected by BitLocker, so assume "
                    + "it is. ")
                + "Changing boot configuration can make Windows ask for the 48-digit BitLocker "
                + "recovery key on the next start. Find that key first - it is in your Microsoft "
                + "account at aka.ms/myrecoverykey, or wherever you saved it - or you may not be "
                + "able to get back in.\n\n"
            : string.Empty;

        if (enable)
        {
            return unknown
                + "Magic Tray will run this command as administrator - Windows will ask you to "
                + "approve it:\n\n    " + EnableCommand + "\n\n"
                + "What this does:\n"
                + "- This PC must be REBOOTED before it takes effect. Magic Tray will not restart "
                + "it for you.\n"
                + "- After the reboot a \"Test Mode\" watermark appears in the corner of the "
                + "desktop.\n"
                + "- Windows will then load drivers that Microsoft has not checked, including any "
                + "self-signed driver. That is a real reduction in this PC's security, for as long "
                + "as Test Mode stays on.\n"
                + "- Memory integrity (Core isolation) must ALSO be off before such a driver will "
                + "load. Magic Tray will not change that - you turn it off yourself in Windows "
                + "Security -> Device security -> Core isolation.\n\n"
                + bitLocker
                + "To undo this at any time, run " + DisableCommand + " and reboot - the Test Mode "
                + "action in Magic Tray does exactly that.";
        }

        return unknown
            + "Magic Tray will run this command as administrator - Windows will ask you to approve "
            + "it:\n\n    " + DisableCommand + "\n\n"
            + "What this does:\n"
            + "- This PC must be REBOOTED before it takes effect. Magic Tray will not restart it "
            + "for you.\n"
            + "- After the reboot the \"Test Mode\" desktop watermark is gone and Windows is back "
            + "to loading only drivers it trusts. This puts the security Test Mode gave up back.\n"
            + "- Any self-signed driver will then stop loading. If this mouse was relying on one, "
            + "scroll stops working after the reboot; the pointer and the battery percent keep "
            + "working.\n"
            + "- Memory integrity (Core isolation) is separate and Magic Tray does not change it. "
            + "If you turned it off by hand, turn it back on in Windows Security -> Device "
            + "security -> Core isolation.\n\n"
            + bitLocker
            + "To undo this, run " + EnableCommand + " and reboot.";
    }

    // Elevated run, user already confirmed by the caller showing plan.Prompt.
    //
    // The arguments are rebuilt from plan.Action, never parsed out of
    // plan.Command: the only two command lines this method can ever produce are
    // the two constants above, whatever a caller put in the record. Plus the
    // Block gate, so a plan that BuildPlan refused cannot be run by a caller
    // that ignored it.
    [SupportedOSPlatform("windows")]
    internal static async Task<InstallOutcome> RunAsync(
        TestModePlan plan, CancellationToken ct = default)
    {
        if (plan.Block != TestModeBlock.None)
        {
            // Not Failed: nothing went wrong, the action is simply not
            // available on this PC, which is what Unavailable means
            // (DriverInstaller.cs:11-16).
            Logger.Log($"TESTMODE_REFUSED action={plan.Action} block={plan.Block}");
            return InstallOutcome.Unavailable;
        }

        var arguments = plan.Action switch
        {
            TestModeAction.Enable => EnableArguments,
            TestModeAction.Disable => DisableArguments,
            _ => null,
        };
        if (arguments is null)
        {
            Logger.Log($"TESTMODE_REFUSED action={plan.Action} block=unknown-action");
            return InstallOutcome.Unavailable;
        }

        Logger.Log($"TESTMODE_RUN action={plan.Action} command=bcdedit {arguments}");
        try
        {
            // RunElevated blocks until the elevated bcdedit exits, so it goes
            // on the thread pool and the UI thread stays free. The UAC prompt
            // itself is modal to the user, not to us.
            await Task.Run(
                () => DriverInstaller.RunElevated("bcdedit.exe", arguments, RunTimeout),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The tray is shutting down or the caller gave up waiting. bcdedit
            // may still have run - it is a single idempotent flag write, and
            // the next read reports the truth - so this is reported as
            // cancelled, not as a success.
            Logger.Log($"TESTMODE_RESULT action={plan.Action} outcome=Cancelled reason=token");
            return InstallOutcome.Cancelled;
        }
        catch (Exception ex) when (DriverInstaller.IsUacDeclined(ex))
        {
            // The user said no at the UAC prompt. Nothing ran, and the caller
            // must not show an error for a decision the user made.
            Logger.Log($"TESTMODE_RESULT action={plan.Action} outcome=Cancelled reason=uac-declined");
            return InstallOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            // Non-zero exit (Secure Boot refusal on a PC whose reading was
            // unknown lands here), timeout, or a start failure.
            Logger.Log($"TESTMODE_RESULT action={plan.Action} outcome=Failed err={ex.Message}");
            return InstallOutcome.Failed;
        }

        Logger.Log($"TESTMODE_RESULT action={plan.Action} outcome=Confirmed reboot=required");
        return InstallOutcome.Confirmed;
    }

    // Is the system drive BitLocker-protected? Only used to pick which
    // BitLocker sentence the prompt shows, and the warning is shown for null
    // as well as true - so a failure here can only ever make the confirmation
    // more cautious, never less.
    //
    // Win32_EncryptableVolume lives in root\CIMV2\Security\MicrosoftVolumeEncryption
    // and is readable only by an administrator, so an unelevated tray - the
    // normal case - gets an access-denied exception and returns null. That is
    // the whole reason null must still warn.
    //
    // ProtectionStatus: 0 unprotected, 1 protected, 2 unknown/locked.
    [SupportedOSPlatform("windows")]
    internal static bool? ReadBitLockerProtected()
    {
        try
        {
            var systemRoot = Path.GetPathRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrEmpty(systemRoot) || systemRoot.Length < 2)
                return null;

            // "C:\" -> "C:", which is how DriveLetter is stored.
            var driveLetter = systemRoot[..2];

            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
            var query = new ObjectQuery(
                "SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '"
                + driveLetter + "'");
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();
            foreach (var o in results)
            {
                using var volume = (ManagementObject)o;
                var status = volume["ProtectionStatus"];
                if (status is null)
                    continue;
                var value = Convert.ToUInt32(status, System.Globalization.CultureInfo.InvariantCulture);
                if (value == 0)
                    return false;
                if (value == 1)
                    return true;
                return null; // 2 = unknown, e.g. a locked volume.
            }

            // No row for the system drive at all: BitLocker is not available on
            // this edition / this volume is not encryptable. Nothing to warn
            // about, but nothing proven either - and the prompt warns on null,
            // which is the safe direction.
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"TESTMODE_BITLOCKER_FAILED err={ex.Message}");
            return null;
        }
    }
}
