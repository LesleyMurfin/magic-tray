// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

/// <summary>
/// Every tray command that can block states how long it can block for, and a
/// command that can block is never allowed to run on the UI thread.
/// </summary>
/// <remarks>
/// The tray used to run its elevated work straight out of the click handler:
/// ApplyWindowsDeviceState called DeviceEnable.Apply inline, and its poll
/// loop sits on the status sidecar for up to two minutes; RunDriverAction
/// called the v1/v2 stock restore and "Fix battery reads" inline, and
/// DriverInstaller.RunElevated waits on the elevated process for five
/// minutes; RunDriverActionAsync awaited the three 0323 driver offers with no
/// hop off the dispatcher at all, so a package download plus a fifteen-minute
/// WaitForExit ran on the message pump. For the whole of each of those
/// windows there is no menu, no icon repaint, no battery poll and no critical
/// alert - Windows marks the process Not Responding.
///
/// Two of the tray's own commands already got this right: both ModeFlip
/// clicks wrap their work in Task.Run and come back through the dispatcher to
/// report, and so do StartRepairApply and StartStaleFilterRemoval. This table
/// is that rule written down for every command instead of remembered at four
/// call sites out of eleven: the id, the ceiling the click can really block
/// for, and whether it has to leave the UI thread. The production handlers
/// are built from the entries, the same way the consent dialogs are built
/// from <see cref="TrayMenu.MutatingCommands"/>.
///
/// The ceilings are not opinions - each one is the wait the code actually
/// performs, so the entries are pinned against the production constants
/// rather than against numbers typed into a test. The two Boot Camp page
/// items are in the table precisely because they block on nothing: they open
/// a browser and return, so a zero ceiling is a real state and
/// <see cref="TrayMenu.MayRunOnUiThread"/> is not a predicate that answers
/// false to everything.
/// </remarks>
public class UiThreadBlockingWorkTests
{
    // The 0323 Magic Mouse: the device whose command set is complete (flip,
    // restore, both Windows device commands and all three driver radios).
    const string Pid = "0323";

    static TrayBlockingCommand Entry(string id) => TrayMenu.BlockingCommand(id);

    [Fact]
    public void BlockingCommands_EveryEntryThatCanBlockIsBarredFromTheUiThread()
    {
        var table = TrayMenu.BlockingCommands;
        Assert.NotEmpty(table);

        foreach (var command in table)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(command.Id),
                "a blocking-work entry with no id names no command");
            Assert.True(
                command.Ceiling >= TimeSpan.Zero,
                $"command {command.Id} names a negative ceiling");

            // The whole rule, as one equality per entry: work that can block
            // for any length of time may not run inline, and work that blocks
            // on nothing may.
            Assert.Equal(command.Ceiling == TimeSpan.Zero, TrayMenu.MayRunOnUiThread(command));

            // And the entry's own claim has to agree with it, so a row that
            // names a fifteen-minute wait cannot also say it stays on the
            // pump.
            Assert.Equal(command.Ceiling > TimeSpan.Zero, command.RunsOffUiThread);
        }

        Assert.Equal(
            table.Count,
            table.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());

        // Both kinds exist, so neither half of the rule above is vacuous.
        Assert.Contains(table, c => c.Ceiling > TimeSpan.Zero);
        Assert.Contains(table, c => c.Ceiling == TimeSpan.Zero);
    }

    [Fact]
    public void BlockingCommands_NameTheCeilingEachClickCanReallyBlockFor()
    {
        // Start/Stop in Windows: DeviceEnable polls the status sidecar to a
        // fixed ceiling, and that one constant is both the loop's wall and
        // the number this table publishes.
        Assert.Equal(TimeSpan.FromMinutes(2), DeviceEnable.ElevatedWaitCeiling);
        Assert.Equal(DeviceEnable.ElevatedWaitCeiling, Entry(TrayMenu.CommandStartInWindows).Ceiling);
        Assert.Equal(DeviceEnable.ElevatedWaitCeiling, Entry(TrayMenu.CommandStopInWindows).Ceiling);

        // "Fix battery reads" and the v1/v2 stock restore each wait on one
        // elevated script with RunElevated's ordinary ceiling.
        Assert.Equal(TimeSpan.FromMinutes(5), DriverInstaller.ScriptRunCeiling);
        Assert.Equal(DriverInstaller.ScriptRunCeiling, Entry(TrayMenu.CommandFixBatteryReads).Ceiling);
        Assert.Equal(DriverInstaller.ScriptRunCeiling, Entry(TrayMenu.CommandV1V2StockRestore).Ceiling);

        // The three 0323 radios pull the driver package first and then wait on
        // the installer, so the honest ceiling is the download plus the
        // elevated step - not the elevated step alone.
        Assert.Equal(TimeSpan.FromMinutes(5), DriverInstaller.DownloadCeiling);
        Assert.Equal(TimeSpan.FromMinutes(15), DriverInstaller.PackageInstallCeiling);
        var downloadThenInstall = DriverInstaller.DownloadCeiling + DriverInstaller.PackageInstallCeiling;
        Assert.True(
            Entry(TrayMenu.CommandV3KmdfInstall).Ceiling >= downloadThenInstall,
            "the KMDF radio downloads the package and then waits on Install-KMDF.cmd");
        Assert.True(
            Entry(TrayMenu.CommandV3PathAInstall).Ceiling >= downloadThenInstall,
            "the patched-Apple radio downloads the package and then waits on the install script");
        // Stock restore runs TWO elevated steps one after the other, the KMDF
        // uninstall and then the PATH-A uninstall, and waits the full
        // installer ceiling on each.
        Assert.True(
            Entry(TrayMenu.CommandV3StockRestore).Ceiling
                >= downloadThenInstall + DriverInstaller.PackageInstallCeiling,
            "the stock radio waits on two elevated uninstall steps, not one");

        // The battery flip: its own ready budget, the restore grace the
        // script gets after it, and the Mode B re-read ModeFlip performs
        // once AwaitTerminal has returned. Equality, not a lower bound - a
        // row that omits the re-read publishes 135 s for a wait that can run
        // 143 s, and a lower bound is exactly what let that through.
        Assert.Equal(8_000, ModeFlip.ModeBVerifyMs);
        Assert.Equal(
            TimeSpan.FromMilliseconds(
                ModeFlip.DefaultTimeoutMs + ModeFlip.RestoreGraceMs + ModeFlip.ModeBVerifyMs),
            Entry(TrayMenu.CommandReadBattery).Ceiling);
        Assert.Equal(
            TimeSpan.FromMilliseconds(ModeFlip.RestoreGraceMs + ModeFlip.ModeBVerifyMs),
            Entry(TrayMenu.CommandRestoreScrollMode).Ceiling);

        // The two repair flows: one elevated step and the sidecar poll
        // DeviceRepair waits out, published from the constant that waits it.
        Assert.Equal(TimeSpan.FromMinutes(2), DeviceRepair.SidecarWaitCeiling);
        Assert.Equal(
            DeviceRepair.SidecarWaitCeiling, Entry(TrayMenu.CommandRepairApply).Ceiling);
        Assert.Equal(
            DeviceRepair.SidecarWaitCeiling, Entry(TrayMenu.CommandStaleFilterRemoval).Ceiling);

        // The Boot Camp items open a documented page and wait on nothing: no
        // elevation, no pnputil, no WaitForExit.
        Assert.Equal(TimeSpan.Zero, Entry(TrayMenu.CommandV1V2BootCampPage).Ceiling);
        Assert.Equal(TimeSpan.Zero, Entry(TrayMenu.CommandTrackpadV1BootCampPage).Ceiling);
    }

    [Fact]
    public void BlockingCommands_CoverEveryElevatedCommandTheTrayCanStart()
    {
        var ids = TrayMenu.BlockingCommands.Select(c => c.Id).ToList();

        Assert.Contains(TrayMenu.CommandReadBattery, ids);
        Assert.Contains(TrayMenu.CommandRestoreScrollMode, ids);
        Assert.Contains(TrayMenu.CommandStartInWindows, ids);
        Assert.Contains(TrayMenu.CommandStopInWindows, ids);
        Assert.Contains(TrayMenu.CommandFixBatteryReads, ids);
        Assert.Contains(TrayMenu.CommandV1V2StockRestore, ids);
        Assert.Contains(TrayMenu.CommandV1V2BootCampPage, ids);
        Assert.Contains(TrayMenu.CommandTrackpadV1BootCampPage, ids);
        Assert.Contains(TrayMenu.CommandV3KmdfInstall, ids);
        Assert.Contains(TrayMenu.CommandV3PathAInstall, ids);
        Assert.Contains(TrayMenu.CommandV3StockRestore, ids);
        Assert.Contains(TrayMenu.CommandRepairApply, ids);
        Assert.Contains(TrayMenu.CommandStaleFilterRemoval, ids);
    }

    [Fact]
    public void BlockingCommands_HaveARowForEveryCommandIdTrayMenuDeclares()
    {
        // The list above is thirteen names typed twice, so a fourteenth
        // command added without a row fails nothing there. This reads the
        // ids off TrayMenu itself: a new Command* constant with no entry is
        // a click that throws InvalidOperationException out of its handler,
        // where App's DispatcherUnhandledException marks it handled and the
        // user sees a menu item that does nothing at all.
        var declared = typeof(TrayMenu)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral
                && f.FieldType == typeof(string)
                && f.Name.StartsWith("Command", StringComparison.Ordinal))
            .Select(f => (Name: f.Name, Id: (string)f.GetRawConstantValue()!))
            .ToList();

        Assert.Equal(TrayMenu.BlockingCommands.Count, declared.Count);
        foreach (var (name, id) in declared)
        {
            var entry = TrayMenu.BlockingCommand(id);
            Assert.Equal(id, entry.Id);
            Assert.False(
                string.IsNullOrWhiteSpace(name),
                "a command constant with no name names nothing");
        }
    }

    [Fact]
    public void BlockingCommands_ContainEveryCommandTheMenuAlreadyAsksConsentFor()
    {
        // A command worth a consent dialog is a command that changes the
        // device, and every one of those elevates and waits. So the consent
        // table is a subset of this one, and each of its commands has to be a
        // row with a real ceiling that leaves the pump.
        foreach (var mutating in TrayMenu.MutatingCommands(Pid, hasRecordedSetting: true))
        {
            var blocking = Entry(mutating.Id);
            Assert.Equal(mutating.Id, blocking.Id);
            Assert.True(
                blocking.Ceiling > TimeSpan.Zero,
                $"mutating command {mutating.Id} elevates, so it cannot have a zero ceiling");
            Assert.True(blocking.RunsOffUiThread);
            Assert.False(TrayMenu.MayRunOnUiThread(blocking));
        }
    }

    [Fact]
    public void MayRunOnUiThread_AllowsNothingThatNamesACeiling()
    {
        // Blocks on nothing: the only shape that may stay on the pump.
        Assert.True(TrayMenu.MayRunOnUiThread(
            new TrayBlockingCommand("opens-a-page", TimeSpan.Zero, RunsOffUiThread: false)));

        // One tick is still a wait.
        Assert.False(TrayMenu.MayRunOnUiThread(
            new TrayBlockingCommand("one-tick", TimeSpan.FromTicks(1), RunsOffUiThread: true)));

        // And the entry does not get to vote: a row that claims it runs inline
        // while naming a fifteen-minute installer is refused on the ceiling,
        // which is the claim that cannot be wrong by accident.
        Assert.False(TrayMenu.MayRunOnUiThread(
            new TrayBlockingCommand("claims-inline", TimeSpan.FromMinutes(15), RunsOffUiThread: false)));
        Assert.False(TrayMenu.MayRunOnUiThread(
            new TrayBlockingCommand("two-minutes", TimeSpan.FromMinutes(2), RunsOffUiThread: true)));
    }

    [Fact]
    public void BlockingCommand_AnIdTheTableDoesNotKnow_IsRefusedInsteadOfCalledFree()
    {
        // Fail closed. A lookup that answered with a default entry would hand
        // back a zero ceiling, and a zero ceiling is a licence to run the work
        // on the UI thread - the exact defect, reintroduced by a typo.
        var ex = Assert.Throws<InvalidOperationException>(
            () => TrayMenu.BlockingCommand("install-the-whole-driver"));
        Assert.Contains("install-the-whole-driver", ex.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentNullException>(() => TrayMenu.BlockingCommand(null!));
    }

    [Fact]
    public void TryStartCommand_RefusesASecondElevatedStartWhileTheFirstIsStillRunning()
    {
        // The window this closes exists only because the work left the pump:
        // the menu is live for the whole five to twenty minutes an install
        // runs, so a second click on a 0323 radio would enter
        // DownloadV3DefaultBranchAsync beside the first and both would use
        // the same zip path and the same extract directory.
        Assert.True(TrayMenu.TryStartCommand(TrayMenu.CommandV3KmdfInstall, out var free));
        try
        {
            Assert.Equal("", free);

            // A different command is refused too: they share one device and
            // one UAC prompt at a time, not just one download folder.
            Assert.False(TrayMenu.TryStartCommand(TrayMenu.CommandV3StockRestore, out var held));
            Assert.Equal(TrayMenu.CommandV3KmdfInstall, held);
            Assert.False(TrayMenu.TryStartCommand(TrayMenu.CommandStartInWindows, out held));
            Assert.Equal(TrayMenu.CommandV3KmdfInstall, held);

            // Including a second click on the very same radio, which is the
            // click a user actually makes when nothing looks like it happened.
            Assert.False(TrayMenu.TryStartCommand(TrayMenu.CommandV3KmdfInstall, out held));
            Assert.Equal(TrayMenu.CommandV3KmdfInstall, held);
        }
        finally
        {
            TrayMenu.FinishCommand();
        }

        // And the gate opens again, or one install would cost the tray every
        // elevated command it has until the user restarts it.
        Assert.True(TrayMenu.TryStartCommand(TrayMenu.CommandStartInWindows, out _));
        TrayMenu.FinishCommand();
        Assert.True(TrayMenu.TryStartCommand(TrayMenu.CommandRepairApply, out _));
        TrayMenu.FinishCommand();
    }

    [Fact]
    public void TryStartCommand_UnderConcurrentStarts_LetsExactlyOneThrough()
    {
        // Interlocked.CompareExchange, the same gate ModeFlip uses for its
        // own cycle: two clicks delivered close together are two threads
        // here, and "I checked and it was free" is not an answer.
        const int Racers = 8;
        // Dedicated threads, not the pool: a barrier all eight have to reach
        // needs eight threads that exist at once.
        using var ready = new Barrier(Racers);
        var won = 0;
        var holders = new string[Racers];
        var threads = new Thread[Racers];

        for (var i = 0; i < Racers; i++)
        {
            var slot = i;
            threads[slot] = new Thread(() =>
            {
                ready.SignalAndWait();
                if (TrayMenu.TryStartCommand(TrayMenu.CommandV3PathAInstall, out var held))
                    Interlocked.Increment(ref won);
                holders[slot] = held;
            });
            threads[slot].Start();
        }
        foreach (var thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a racing start never finished");

        Assert.Equal(1, won);
        TrayMenu.FinishCommand();

        // Every loser was told what it lost to, so the log line a refused
        // click writes names the command it is waiting on.
        Assert.Equal(
            Racers - 1,
            holders.Count(h => string.Equals(h, TrayMenu.CommandV3PathAInstall, StringComparison.Ordinal)));
    }

    [Fact]
    public void ElevatedCommandBusy_SaysWhatHappenedAndWhatToDo()
    {
        // A refusal the user cannot see is the dead menu item the frozen
        // pump already looked like.
        Assert.False(string.IsNullOrWhiteSpace(TrayMenu.ElevatedCommandBusy));
        Assert.Contains("still busy", TrayMenu.ElevatedCommandBusy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was changed", TrayMenu.ElevatedCommandBusy, StringComparison.Ordinal);
    }
}
