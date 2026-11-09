// SPDX-License-Identifier: MIT
using System.Linq;
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

/// <summary>
/// A device the user stopped in Windows must keep its tray row, because that
/// row carries the only in-app way back: "Start this device in Windows".
/// </summary>
/// <remarks>
/// The tray builds its device rows from exactly two sources, and a stopped
/// Magic Keyboard or Magic Trackpad is in neither of them:
///   - the poll results (DeviceRegistry.Discover over live HID interfaces): an
///     administratively disabled devnode has no interfaces, so nothing is
///     enumerated and nothing is read;
///   - DriverHealthChecker.GetPerDeviceStatus, which parses BTHENUM keys with
///     skipNonScroll:true and therefore drops every keyboard and trackpad PID
///     on purpose - those devices must never feed a mouse driver status.
/// So the device vanished from the menu together with the item that starts it
/// again, and the user had to go to Device Manager to undo a click they made
/// in this app.
///
/// The missing third source is the one piece of evidence nobody had asked:
/// the BTHENUM devnode is still in the registry while the device is stopped
/// (DeviceSnapshot.BthenumLiveCount counts those instance keys), and the PID
/// is in this app's own catalog. That pair is enough to draw an honest row,
/// and it is the ONLY pair that is: no devnode means no device, and a PID the
/// catalog cannot name is not ours to name either.
/// </remarks>
public class OfflineDeviceRowTests
{
    // The shape of a device Windows has been told to stop: its BTHENUM key is
    // still there, nothing is live on it, and no poll has ever read it. Each
    // test overrides only the field its case is about. Everything that is not
    // evidence about presence is left at the no-news value, so no rule can
    // lean on something this decision must not depend on.
    static DeviceSnapshot Snap(
        string pid,
        int bthenumLiveCount = 1,
        int usbPhantomCount = 0,
        bool col01Present = false,
        bool col02Present = false,
        bool? pointerChildLive = null,
        int? lastBatteryPct = null)
        => new(
            pid,
            bthenumLiveCount,
            usbPhantomCount,
            col01Present,
            col02Present,
            BoundFilterName: null,
            FilterPackagePresent: false,
            FilterServiceRunning: false,
            ConfigEnabled: true,
            FilterCandidates: [],
            FilterInStack: null,
            PointerChildLive: pointerChildLive,
            MultitouchAdvancing: null,
            ScrollWatcherHealthy: null,
            LastBatteryPct: lastBatteryPct);

    // "PID&0239" / "PID_0239" -> "0239", the form every registry key and every
    // tray row uses.
    static string Tail(string pidPattern) => pidPattern[^4..].ToLowerInvariant();

    [Fact]
    public void OfflineRows_StoppedKeyboard_KeepsTheRowThatCanStartItAgain()
    {
        // Mouse live and polled, keyboard stopped in Windows.
        var rows = TrayMenu.OfflineRows(["0323"], [Snap("0239")]);

        var row = Assert.Single(rows);
        Assert.Equal("0239", row.Pid);
        Assert.Equal("Apple Wireless Keyboard (2011)", row.Name);
        Assert.Equal(DeviceKind.MagicKeyboard, row.Kind);
        Assert.Equal(TrayMenu.OfflineRowPct, row.Pct);

        // Neither existing source can produce this row, which is why the seam
        // exists: the health list skips keyboards by design.
        Assert.False(TrayMenu.ShouldShowHealthRow(["0323"], "0239", DriverStatus.Ok));
        Assert.False(TrayMenu.ShouldShowHealthRow(["0323"], "0239", DriverStatus.NotBound));
    }

    [Fact]
    public void OfflineRows_StoppedTrackpad_KeepsTheRowThatCanStartItAgain()
    {
        var row = Assert.Single(TrayMenu.OfflineRows([], [Snap("030e")]));

        Assert.Equal("030e", row.Pid);
        Assert.Equal("Magic Trackpad", row.Name);
        Assert.Equal(DeviceKind.MagicTrackpadV1, row.Kind);
        Assert.False(TrayMenu.ShouldShowHealthRow([], "030e", DriverStatus.Ok));
    }

    [Fact]
    public void OfflineRows_EveryDeviceTheHealthCheckerSkips_IsReachableAgain()
    {
        // Exactly the devices the driver health sweep refuses to parse: every
        // catalogued keyboard, and every trackpad. One of them stopped in
        // Windows is the whole defect, so each has to come back by itself.
        var pids = KeyboardBatteryDevice.KnownKeyboards
            .Select(k => Tail(k.PidPattern))
            .Concat(MouseBatteryDevice.KnownMice
                .Where(m => m.Kind is DeviceKind.MagicTrackpadV1
                    or DeviceKind.MagicTrackpadV2 or DeviceKind.MagicTrackpadV3)
                .Select(m => Tail(m.PidPattern)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(pids);

        foreach (var pid in pids)
        {
            var row = Assert.Single(TrayMenu.OfflineRows([], [Snap(pid)]));
            Assert.Equal(pid, row.Pid, ignoreCase: true);
            Assert.False(string.IsNullOrEmpty(row.Name), $"pid {pid} drew a row with no name");
            // The name is the catalog's, so the kind the row carries is the
            // kind that name resolves to - a row cannot describe one device
            // and behave as another.
            Assert.Equal(DeviceCapability.KindForName(row.Name), row.Kind);

            // The reason this row has to be built here and nowhere else.
            Assert.False(TrayMenu.ShouldShowHealthRow([], pid, DriverStatus.Ok));
        }
    }

    [Fact]
    public void OfflineRows_SkipsAPidTheMenuAlreadyShows()
    {
        // A device that polled is already a row; a second one would be the
        // same device twice, with two different stories about it.
        Assert.Empty(TrayMenu.OfflineRows(["0239"], [Snap("0239")]));

        // Registry keys are upper case and poll results are not. Casing is
        // never a second device.
        Assert.Empty(TrayMenu.OfflineRows(["023A"], [Snap("023a")]));
        Assert.Empty(TrayMenu.OfflineRows(["0239", "030E"], [Snap("0239"), Snap("030e")]));

        // The shown set is what the poll and the health rows between them
        // produced, so only the devices missing from it come back.
        var rows = TrayMenu.OfflineRows(["0239"], [Snap("0239"), Snap("030e")]);
        var row = Assert.Single(rows);
        Assert.Equal("030e", row.Pid);
    }

    [Fact]
    public void OfflineRows_WithoutADevnode_InventsNothing()
    {
        // No BTHENUM instance: Windows has never been paired with this device
        // on this PC, or the pairing was removed. There is nothing to start,
        // so there is nothing to say.
        Assert.Empty(TrayMenu.OfflineRows([], [Snap("0239", bthenumLiveCount: 0)]));

        // A charge-cable leftover under Enum\USB / Enum\HID is not a paired
        // device either, however many phantoms it left behind.
        Assert.Empty(TrayMenu.OfflineRows(
            [],
            [Snap("0239", bthenumLiveCount: 0, usbPhantomCount: 3, col01Present: true, col02Present: true)]));

        Assert.Empty(TrayMenu.OfflineRows([], []));
    }

    [Fact]
    public void OfflineRows_APidTheCatalogCannotName_InventsNothing()
    {
        // An Apple BTHENUM key for something this app does not ship support
        // for. A row would have to invent a name and a kind for it.
        Assert.Empty(TrayMenu.OfflineRows([], [Snap("c52b", bthenumLiveCount: 2)]));
        Assert.Empty(TrayMenu.OfflineRows([], [Snap("ffff", bthenumLiveCount: 1)]));
    }

    [Fact]
    public void OfflineRows_NeverRepeatTheLastPercentTheyRead()
    {
        // The device is stopped: whatever it last reported is history, and a
        // percentage on a row for a device nothing is reading is a claim about
        // the present that nothing measured.
        var row = Assert.Single(TrayMenu.OfflineRows([], [Snap("0239", lastBatteryPct: 97)]));

        Assert.Equal(-1, TrayMenu.OfflineRowPct);
        Assert.Equal(TrayMenu.OfflineRowPct, row.Pct);
        Assert.Equal("No reading", TrayMenu.BatteryText(row.Pct, enabledInApp: true));
    }

    [Fact]
    public void OfflineRows_NameEachDeviceOnceAndKeepTheSweepOrder()
    {
        var rows = TrayMenu.OfflineRows([], [Snap("030e"), Snap("0239"), Snap("0239"), Snap("030E")]);

        Assert.Equal(new[] { "030e", "0239" }, rows.Select(r => r.Pid).ToArray());
    }

    [Fact]
    public void OfflineRows_AreDecidedOnTheMenu_NotOnGuessedLiveness()
    {
        // A device with a live pointer child that the poll still failed to
        // read (a HID open can fail for its own reasons) is missing from the
        // menu just as completely as a stopped one. Absence from the shown set
        // is the signal; no other liveness evidence may veto the row, because
        // every veto here is another way to lose the Start item.
        var row = Assert.Single(TrayMenu.OfflineRows(
            [],
            [Snap("0239", bthenumLiveCount: 2, col01Present: true, col02Present: true, pointerChildLive: true)]));

        Assert.Equal("0239", row.Pid);
        Assert.Equal(TrayMenu.OfflineRowPct, row.Pct);
    }

    [Theory]
    [InlineData(1, "Apple Wireless Keyboard (2011)", true)]
    [InlineData(4, "Apple Wireless Keyboard (2011)", true)]
    [InlineData(0, "Apple Wireless Keyboard (2011)", false)]
    [InlineData(1, null, false)]
    [InlineData(1, "", false)]
    public void ShouldShowOfflineRow_NeedsACatalogNameAndADevnode(
        int bthenumLiveCount, string? catalogName, bool expected)
    {
        Assert.Equal(
            expected,
            TrayMenu.ShouldShowOfflineRow(
                [], "0239", catalogName, Snap("0239", bthenumLiveCount: bthenumLiveCount)));
    }

    [Fact]
    public void ShouldShowOfflineRow_WithoutEvidenceAboutThisDevice_IsFalse()
    {
        const string name = "Apple Wireless Keyboard (2011)";

        // No snapshot at all: the sweep has not run, or it failed for this
        // PID. Nothing was measured, so nothing is claimed.
        Assert.False(TrayMenu.ShouldShowOfflineRow([], "0239", name, null));

        // Another device's snapshot is not evidence about this one.
        Assert.False(TrayMenu.ShouldShowOfflineRow([], "030e", "Magic Trackpad", Snap("0239")));

        // Nothing to key a row on.
        Assert.False(TrayMenu.ShouldShowOfflineRow([], null, name, Snap("0239")));
        Assert.False(TrayMenu.ShouldShowOfflineRow([], "", name, Snap("")));

        // Already drawn by the poll or by a health row.
        Assert.False(TrayMenu.ShouldShowOfflineRow(["0239"], "0239", name, Snap("0239")));
        Assert.False(TrayMenu.ShouldShowOfflineRow(["0239", "0323"], "0239", name, Snap("0239")));
        Assert.False(TrayMenu.ShouldShowOfflineRow(["023A"], "023a", "Apple Wireless Keyboard (2011) ISO", Snap("023a")));
        Assert.True(TrayMenu.ShouldShowOfflineRow(["0323"], "0239", name, Snap("0239")));
    }

    [Fact]
    public void CatalogName_NamesEveryDeviceThisAppShipsSupportFor()
    {
        foreach (var k in KeyboardBatteryDevice.KnownKeyboards)
            Assert.Equal(k.DisplayName, TrayMenu.CatalogName(Tail(k.PidPattern)));

        foreach (var m in MouseBatteryDevice.KnownMice)
            Assert.Equal(m.DisplayName, TrayMenu.CatalogName(Tail(m.PidPattern)));

        // Registry keys spell the PID in upper case; poll results do not.
        Assert.Equal("Apple Wireless Keyboard (2011)", TrayMenu.CatalogName("0239"));
        Assert.Equal("Apple Wireless Keyboard (2011)", TrayMenu.CatalogName("0239".ToUpperInvariant()));
    }

    [Fact]
    public void CatalogName_NamesNothingItWasNotTold()
    {
        Assert.Null(TrayMenu.CatalogName("c52b"));
        Assert.Null(TrayMenu.CatalogName("ffff"));
        Assert.Null(TrayMenu.CatalogName(""));
        Assert.Null(TrayMenu.CatalogName(null));
    }
}
