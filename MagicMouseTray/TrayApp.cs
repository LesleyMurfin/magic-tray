// SPDX-License-Identifier: MIT
using Microsoft.Win32;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MagicMouseTray;

// Copy helpers for the tray menu. Kept free of WinForms so tests can cover
// per-device badge/action rules without spinning a NotifyIcon.
internal static class TrayMenu
{
    internal const string ProductName = "Magic Tray";
    internal const string ReleasesUrl = "https://github.com/LesleyMurfin/magic-tray/releases";
    internal const string RepoUrl = "https://github.com/LesleyMurfin/magic-tray";
    internal const string IssuesUrl = "https://github.com/LesleyMurfin/magic-tray/issues";
    internal const string AlertsDocUrl = "https://github.com/LesleyMurfin/magic-tray/blob/main/docs/ALERTS.md";
    // The tray-side switch: whether Magic Tray polls and shows this device.
    // Pure config (enabled_<pid> in config.ini), no elevation and no PnP
    // change - which is why it no longer says "on this PC". That phrasing was
    // read as the Windows device state, and the single checkbox really did
    // drive both: a failed pnputil left the tray's own state unwritten.
    internal const string ShowInTray = "Show in Magic Tray";

    // The Windows device state, kept visibly apart from the line above: these
    // two are the only items that ask for elevation, and they name Windows as
    // the thing they change.
    internal const string WindowsDeviceMenuLabel = "Windows device";
    internal const string StartInWindows = "Start this device in Windows";
    internal const string StopInWindows = "Stop this device in Windows";

    // The consent for each of those two, said before anything is elevated,
    // and the answer when nothing needs elevating at all. Every one of them
    // names Windows, never "this PC", so none of them can be read as the tray
    // setting above.
    internal const string StartInWindowsPrompt =
        "Start this device in Windows?\n\nWindows will use it again. This needs administrator approval.";
    internal const string StopInWindowsPrompt =
        "Stop this device in Windows?\n\nIt will not move the cursor here until you start it again. It stays paired. This needs administrator approval.";
    internal const string AlreadyStartedInWindows =
        "Windows is already using this device, so there is nothing to change.\n\nNothing was run and no administrator approval was needed.";
    internal const string HelpMenuLabel = "Help/Documentation";
    internal const string HowAlertsWorkLabel = "How alerts work";
    internal const string RepositoryLabel = "Repository";
    internal const string ReportBugLabel = "Report a bug";
    internal const string RequestFeatureLabel = "Request a feature";
    // Root-level, not inside Help: a star ask nobody finds is a star ask nobody acts on.
    // GitHub has no URL that stars a repo, and Magic Tray holds no GitHub
    // credentials, so the click opens the repo and the user presses Star there.
    // The flip records that they took the trip, not that GitHub recorded a star.
    internal const string StarOnGitHubLabel = "★ Star on GitHub";
    internal const string StarThanksLabel = "★ Thanks for the support!";
    internal const string ReportBugConfirm =
        "Magic Tray will collect version, driver badges, battery readings, and the last log lines (Bluetooth MAC redacted), copy them, and open a GitHub issue draft. You submit it while logged in.\n\nContinue?";
    internal const string RequestFeatureConfirm =
        "Magic Tray will open a GitHub feature-request draft with the app version. The text is also on the clipboard. You submit it while logged in.\n\nContinue?";

    // Every string this app puts in a ToolStripItem's Text passes through
    // MenuText. A menu item does not wrap: WinForms lays one item out as a
    // single line as wide as its text needs, so a paragraph handed to .Text
    // draws a row across the whole display. This session's screenshots caught
    // exactly that - DriverAdvisor's sourced advice paragraphs (the keyboard
    // one is ~190 characters) rendered as unbroken 1568px-wide rows on all
    // three device rows, overlapping the rest of the UI.
    //
    // 80 characters is the cap: wider than every legitimate row this menu
    // draws today (the longest observed capability line is 77) and about half
    // the reference display, so nothing readable is cut yet nothing can spill
    // across the screen again. Prose longer than that belongs in a dialog -
    // ShowDriverAdvice and ShowConfigFact are where the full text lives.
    //
    // Runs of plain spaces are left alone: the device rows and the threshold
    // labels use a double space as a column separator. Only line breaks, tabs
    // and control characters collapse, because those are what turn one item
    // into a ragged multi-line row.
    //
    // AccessibleName is deliberately NOT clamped: a screen reader is not laid
    // out in pixels, and shortening what it announces would hide text rather
    // than fit it.
    internal const int MenuTextMaxChars = 80;
    const string MenuTextEllipsis = "...";

    internal static string MenuText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var flat = Flatten(text).Trim();
        if (flat.Length <= MenuTextMaxChars)
            return flat;

        // Cut on a word boundary when there is one reasonably close to the
        // cap, so the result reads as a shortened phrase, not a severed word.
        var keep = MenuTextMaxChars - MenuTextEllipsis.Length;
        var space = flat.LastIndexOf(' ', keep - 1);
        if (space > MenuTextMaxChars / 2)
            keep = space;
        return string.Concat(flat.AsSpan(0, keep).TrimEnd(), MenuTextEllipsis);
    }

    // Each run of line breaks / tabs / control characters becomes one space.
    // Returns the input itself, with no allocation, when it contains none -
    // which is every static label in this menu.
    static string Flatten(string text)
    {
        var firstBreak = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsBreak(text[i]))
            {
                firstBreak = i;
                break;
            }
        }
        if (firstBreak < 0)
            return text;

        var sb = new System.Text.StringBuilder(text.Length);
        sb.Append(text, 0, firstBreak);
        var pending = false;
        for (var i = firstBreak; i < text.Length; i++)
        {
            var c = text[i];
            if (IsBreak(c))
            {
                pending = true;
                continue;
            }
            if (pending)
            {
                pending = false;
                if (sb.Length > 0)
                    sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    static bool IsBreak(char c) => c != ' ' && (char.IsWhiteSpace(c) || char.IsControl(c));

    // What the advice row opens: the everything-we-know form, and the only
    // home for the long DriverAdviceView.OptionRows sentences now that the
    // menu draws OptionRowsShort. A dialog wraps, so the paragraph is the
    // point of this text rather than its problem - the reverse of MenuText
    // above. Composed here, free of WinForms, so what the user reads is
    // testable off device.
    //
    // stock/sdp are what the two registry readers found for a device this app
    // installs nothing for: the driver Windows actually has on it, and - for a
    // Magic Keyboard - whether this repo's SDP patch is what its battery
    // percent comes from. They are handed to AdviceFull rather than appended
    // here, because the advice is ONE sentence about the present state and two
    // sources for it would drift apart. Both default to null, which is exactly
    // what a mouse passes: a mouse's driver story is its DriverStatus, and
    // every existing caller reads the same as it always did.
    internal static string AdviceDialogText(
        DeviceKind kind, string? pid, DriverStatus? status,
        StockDriverInfo? stock = null, SdpPatchState? sdp = null)
    {
        var text = DriverAdviceView.AdviceFull(kind, pid, status, stock, sdp);
        if (!DriverAdviceView.ShowOptions(kind, pid))
            return text;

        var sb = new System.Text.StringBuilder(text);
        sb.Append("\n\n").Append(DriverAdviceView.OptionsHeader());
        foreach (var row in DriverAdviceView.OptionRows(kind, pid))
            sb.Append("\n\n").Append(row);
        return sb.ToString();
    }
    internal static string StarLabel(bool starClicked) =>
        starClicked ? StarThanksLabel : StarOnGitHubLabel;

    // Global picker: percent floor, then time alerts. No invented hours.
    internal static string GlobalThresholdLabel(int pct) => $"{pct}%  then time alerts";

    // Per-device: (~Nd)/(~Nh) only when GetHoursToEmpty > 0. Unknown → "10%".
    internal static string DeviceThresholdLabel(int pct, double hoursToEmpty)
    {
        if (hoursToEmpty > 0)
            return $"{pct}%  ({FormatHoursToEmpty(hoursToEmpty)})";
        return $"{pct}%";
    }

    internal static string FormatHoursToEmpty(double hours)
    {
        if (hours >= 24)
        {
            var days = Math.Max(1, (int)Math.Round(hours / 24.0));
            return $"~{days}d";
        }

        var h = Math.Max(1, (int)Math.Round(hours));
        return $"~{h}h";
    }

    internal static string? FindLocalAlertsDoc(string? startDir = null)
    {
        string? dir = startDir ?? AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var p = Path.Combine(dir, "docs", "ALERTS.md");
            if (File.Exists(p))
                return p;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    internal static bool PidEq(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static bool IsV3(DeviceKind kind, string? pid) =>
        kind == DeviceKind.MagicMouseV3 || PidEq(pid, "0323");

    internal static bool IsV1V2Mouse(DeviceKind kind) =>
        kind is DeviceKind.MagicMouseV1 or DeviceKind.MagicMouseV2;

    // The devices whose driver story is Windows' own: a keyboard, a trackpad,
    // anything that is not one of this app's mice. They get no Driver submenu
    // and no DriverStatus worth showing - DriverHealthChecker deliberately
    // skips them (DriverHealthChecker.cs:406, 494-498), and Classify would
    // call a Magic Keyboard an "unknown Apple mouse" if it did not - so the
    // driver they are actually on has to be READ instead
    // (StockDriverReader.ForPid).
    //
    // Exactly the set BuildDeviceRow's else branch draws, named once so the
    // reader's targets and the row that shows them cannot drift apart.
    internal static bool ShowsStockDriverStory(DeviceKind kind, string? pid) =>
        !IsV3(kind, pid) && !IsV1V2Mouse(kind);

    // The collapsed home for the per-driver predictions, on EVERY device row
    // now, not just the ones with no Driver submenu. Listing them inline under
    // the radios is what inflated each device row in this session's
    // screenshots, and a prediction rendered at row level beside the observed
    // capability rows is the one way those two blocks can be read as
    // contradicting each other. The label carries the predicted mood ("would
    // give") so the collapsed line cannot be read as a reading either.
    internal const string DriverChoicesLabel = "What each driver would give";

    // enabledInApp: false means the user switched this device off in Magic
    // Tray, so it is not polled. A device that is not being read has no
    // reading, and rendering that absence as "No reading" blamed the device
    // for a choice the user made - the v1 in this session's screenshots read
    // "Battery: No reading" while sitting two rows under "Mouse is switched
    // off in this app", on a mouse measured at 97% when it was on.
    internal static string BatteryText(int pct, bool? enabledInApp) =>
        DeviceCapability.BatteryLabel(pct, enabledInApp);

    // v1/v2 Driver radios replaced the orange Fix scroll item. Always false.
    internal static bool ShowFixScroll(DeviceKind kind, DriverStatus? status)
    {
        _ = kind;
        _ = status;
        return false;
    }

    // The keyboard SDP patch offer. -2 is KeyboardBatteryDevice's "present but
    // blocked" sentinel - no battery Feature cap on COL02, which is exactly
    // what an unpatched SDP record produces - and it is the ONLY value that
    // opens this offer.
    //
    // That single term is also why no SdpPatchState is needed here: a keyboard
    // whose patch reads Applied and whose percent reads 0..100 cannot be -2,
    // so a working device can never be offered a fix for a problem it does not
    // have. Adding "and the patch is not applied" would be an unreachable
    // clause pretending to be a guard.
    internal static bool ShowFixKeyboard(DeviceKind kind, int pct) =>
        kind == DeviceKind.MagicKeyboard && pct == -2;

    internal const string TrackpadV1BootCampLabel = "Boot Camp";

    // Magic Trackpad v1 (030E) only. Not KMDF radios, not 0265/0324.
    internal static bool ShowTrackpadV1BootCamp(DeviceKind kind, string? pid) =>
        kind == DeviceKind.MagicTrackpadV1 && DriverInstaller.IsTrackpadV1BootCampPid(pid);

    internal static bool ShowBatteryReads(bool v3Connected, DriverStatus? v3Status) =>
        v3Connected && v3Status == DriverStatus.PatchedKmdf;

    // The battery flip and its restore: only while v3 is bound to the patched
    // Apple driver, the one driver where reading the percent costs the user
    // their scroll wheel for the 10-20 seconds the hardware test measured
    // (ModeFlip.cs:10-21; the same figure ModeFlipView states in the offer).
    internal static bool ShowPathAModeSwitch(DeviceKind kind, string? pid, DriverStatus? status) =>
        IsV3(kind, pid) && status == DriverStatus.PathAPatched;

    // Exact radio copy. No PATH-A / applewirelessmouse / installer names.
    internal const string V3RadioKmdf = "KMDF";
    internal const string V3RadioPatchedApple = "Patched Apple driver";
    internal const string V3RadioStockWindows = "Stock Windows";

    internal static readonly string[] V3DriverRadioLabels =
    [
        V3RadioKmdf,
        V3RadioPatchedApple,
        V3RadioStockWindows,
    ];

    // Exactly one radio from Classify; NotBound checks none.
    internal static string? V3CheckedDriverRadio(DriverStatus? status) => status switch
    {
        DriverStatus.PatchedKmdf => V3RadioKmdf,
        DriverStatus.PathAPatched => V3RadioPatchedApple,
        DriverStatus.StockKmdf => V3RadioStockWindows,
        _ => null,
    };

    // Exact v1/v2 radio copy. No PATH-A / applewirelessmouse / KMDF / tealtadpole.
    internal const string V1V2RadioBootCamp = "Boot Camp";
    internal const string V1V2RadioStockWindows = "Stock Windows";

    internal static readonly string[] V1V2DriverRadioLabels =
    [
        V1V2RadioBootCamp,
        V1V2RadioStockWindows,
    ];

    // Exactly one radio from Classify; NotBound checks none.
    internal static string? V1V2CheckedDriverRadio(DriverStatus? status) => status switch
    {
        DriverStatus.Ok => V1V2RadioBootCamp,
        DriverStatus.NotInstalled => V1V2RadioStockWindows,
        _ => null,
    };

    // Checked radio is disabled. Stock clickable on Ok (Boot Camp bound) and NotBound.
    internal static bool V1V2BootCampRadioEnabled(DriverStatus? status) =>
        status != DriverStatus.Ok;

    internal static bool V1V2StockRadioEnabled(DriverStatus? status) =>
        status != DriverStatus.NotInstalled;

    internal static string? V3Badge(DriverStatus? status) => status switch
    {
        DriverStatus.PatchedKmdf => "KMDF",
        DriverStatus.PathAPatched => "Patched Apple",
        DriverStatus.StockKmdf => "Stock",
        DriverStatus.Error => "Error",
        _ => "Not bound",
    };

    internal static string? V1V2Badge(DriverStatus? status) => status switch
    {
        DriverStatus.Ok => "Boot Camp",
        DriverStatus.NotInstalled => "Stock",
        DriverStatus.Error => "Error",
        _ => "Not bound",
    };

    // A ConfigFact names a page or a script that already ships in a package;
    // the tray opens documentation and never launches anything, so only an
    // http(s) target earns an OK-opens-it dialog (SystemConfigChecker.cs:16-18).
    internal static bool IsHelpUrl(string? target) =>
        !string.IsNullOrEmpty(target)
        && (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    // Orange is the device-fault channel: the repair row, the per-device finding
    // row and the unknown-mouse warning. A configuration line may borrow it only
    // for a Blocking fact - an Advisory in orange would compete with a measured
    // device fault for the user's eye while saying much less.
    internal static bool ConfigSectionIsFault(IReadOnlyList<ConfigFact> facts)
    {
        for (var i = 0; i < facts.Count; i++)
        {
            if (facts[i].Severity == ConfigSeverity.Blocking)
                return true;
        }
        return false;
    }

    // SystemConfigChecker is asked once per DEVICE, so the same question can
    // come back several times per refresh and the answers have to be folded
    // into one section without inventing or losing a row.
    //
    // Machine-wide facts (Test Mode, Memory integrity, the driver package's
    // multitouch watcher) are properties of the PC: two devices asking the
    // same question are one question, so they collapse on the fact id and the
    // MORE SEVERE reading wins - a fact that blocks the mouse actually in use
    // is never pushed down by a milder reading taken for another device.
    //
    // The driver package fact is NOT machine-wide. Which package a device
    // needs is decided per device (SystemConfigChecker.PackagePathFor: KMDF or
    // the patched Apple filter for a 0323, the Boot Camp filter for a v1/v2),
    // so two devices can be in genuinely different package states. Collapsing
    // those onto one id reported one device's package state as the other's,
    // which is a silent misreport of a device nobody asked about. They are
    // de-duplicated by the whole fact instead: two devices that really are in
    // the same package state say it once, and two different states each keep
    // their own honest row.
    //
    // First-seen order is preserved throughout, because ConfigFactView.Ordered
    // sorts by rank on top of the checker's own reading order.
    internal static bool IsMachineWideFact(string id) =>
        !string.Equals(id, SystemConfigChecker.DriverPackageFactId, StringComparison.Ordinal);

    internal static IReadOnlyList<ConfigFact> MergeConfigFacts(
        IEnumerable<IReadOnlyList<ConfigFact>> perDevice)
    {
        var merged = new List<ConfigFact>(4);
        foreach (var facts in perDevice)
        {
            for (var i = 0; i < facts.Count; i++)
            {
                var fact = facts[i];
                if (IsMachineWideFact(fact.Id))
                {
                    var at = merged.FindIndex(
                        x => string.Equals(x.Id, fact.Id, StringComparison.Ordinal));
                    if (at < 0)
                        merged.Add(fact);
                    else if (ConfigFactView.Rank(fact.Severity)
                        < ConfigFactView.Rank(merged[at].Severity))
                        merged[at] = fact;
                    continue;
                }

                // ConfigFact is a record, so this is value equality over the
                // whole reading: identical claims say it once, a different
                // claim is a second device's own state and gets its own row.
                if (!merged.Contains(fact))
                    merged.Add(fact);
            }
        }

        if (merged.Count == 0)
            return Array.Empty<ConfigFact>();
        return merged;
    }

    // First Driver submenu item: live bound service, or (none).
    internal static string BoundLabel(string? boundDriverName) =>
        string.IsNullOrEmpty(boundDriverName) ? "Bound: (none)" : $"Bound: {boundDriverName}";

    internal static bool IconAttention(string pid, DriverStatus status)
    {
        if (PidEq(pid, "0323"))
            return status is DriverStatus.UnknownAppleMouse or DriverStatus.Error;
        // NotInstalled is valid Stock for v1/v2 — not attention.
        return status is DriverStatus.NotBound
            or DriverStatus.UnknownAppleMouse or DriverStatus.Error;
    }

    // Health-only tray row: known mouse PID not already in poll results, or UnknownAppleMouse.
    // 030D Ok and NotInstalled must appear — Stock radios are reachable on NotInstalled.
    internal static bool ShouldShowHealthRow(IEnumerable<string> shownPids, string healthPid, DriverStatus status)
    {
        if (string.IsNullOrEmpty(healthPid))
            return false;
        foreach (var shown in shownPids)
        {
            if (PidEq(shown, healthPid))
                return false;
        }

        if (status == DriverStatus.UnknownAppleMouse)
            return true;

        if (!Array.Exists(DriverHealthChecker.KnownMousePids, p => PidEq(p, healthPid)))
            return false;

        return status is DriverStatus.Ok
            or DriverStatus.NotBound
            or DriverStatus.NotInstalled
            or DriverStatus.PathAPatched
            or DriverStatus.PatchedKmdf
            or DriverStatus.StockKmdf;
    }


    internal static string RowLabel(
        string name, int pct, string? badge, string extras, bool? enabledInApp)
    {
        var s = $"{name}    {BatteryText(pct, enabledInApp)}";
        if (!string.IsNullOrEmpty(extras)) s += $"  {extras}";
        if (!string.IsNullOrEmpty(badge)) s += $"    {badge}";
        return s;
    }

    // ---- tray tooltip ----------------------------------------------------

    // One device segment as the tooltip states it, plus the reading behind it.
    // Pct carries the sentinels DeviceCapability.BatteryLabel renders as words
    // (negative = not a measured percentage), because which segment may be
    // dropped is decided on the reading, never on the rendered text.
    internal readonly record struct TooltipEntry(string Text, int Pct);

    // Measured on this machine against .NET 8 WinForms: NotifyIcon.Text takes
    // 127 characters and throws ArgumentOutOfRangeException ("Text length must
    // be less than 128 characters long") at 128. That is the modern
    // NOTIFYICONDATAW szTip[128] capacity, not the 64 (63 + NUL) of the legacy
    // struct the old 63 clamp here was written for.
    internal const int TooltipMaxLength = 127;

    // Last resort only, and it says so on screen: a hard clip is how a tooltip
    // ends up looking like a rendering bug, so the three dots are there to
    // show that the text was cut rather than that the tray ran out of things
    // to say.
    internal static string ClipTooltip(string text, int max = TooltipMaxLength)
    {
        if (max <= 0)
            return string.Empty;
        if (text.Length <= max)
            return text;
        return max <= 3 ? text.Substring(0, max) : text.Substring(0, max - 3) + "...";
    }

    // The whole tooltip, composed so that it degrades honestly.
    //
    // The old line took the first 127 (then 63) characters of the joined list,
    // which on a three-device PC cut the last device in half and left the
    // string ending in a dangling " | ": the user could not see that device's
    // battery at all, and what they could see looked broken. So:
    //
    //   - whole segments are dropped, never half of one;
    //   - a drop is always visible as a trailing "+N more", so a shorter list
    //     reads as a shortened list and not as a truncated one;
    //   - what survives is chosen by the reading, not by enumeration order: a
    //     measured percentage outranks a sentinel (a device we could not read
    //     is not a low battery), and among measured ones the LOWEST is kept
    //     first, so the battery the user has to act on is the last thing to go;
    //   - the kept segments stay in enumeration order, so the tooltip does not
    //     reshuffle itself every time a percentage moves;
    //   - the poll-interval suffix goes before any device does: it is the
    //     least urgent thing in the string, so a PC that cannot show
    //     everything loses the interval first and only then starts dropping
    //     devices.
    internal static string ComposeTooltip(
        IReadOnlyList<TooltipEntry> entries, string suffix, int max = TooltipMaxLength)
    {
        if (max <= 0)
            return string.Empty;
        if (entries.Count == 0)
            return ClipTooltip(suffix.Trim(), max);

        var keepOrder = DropOrder(entries);

        var full = Render(entries, keepOrder, entries.Count, suffix);
        if (full.Length <= max)
            return full;

        // The interval is gone from here down.
        var composed = FirstThatFits(entries, keepOrder, max);
        if (composed is not null)
            return composed;

        // Not even the single most urgent device fits. Clipping that one
        // segment is the only option left, and it is marked as clipped.
        return ClipTooltip(entries[keepOrder[0]].Text, max);
    }

    // Indices of entries in keep priority: measured readings first, lowest
    // percentage first, sentinels last. Stable within a group so the tie-break
    // is enumeration order.
    static List<int> DropOrder(IReadOnlyList<TooltipEntry> entries)
    {
        var order = new List<int>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
            order.Add(i);
        order.Sort((a, b) =>
        {
            var sentinelA = entries[a].Pct < 0 ? 1 : 0;
            var sentinelB = entries[b].Pct < 0 ? 1 : 0;
            if (sentinelA != sentinelB)
                return sentinelA - sentinelB;
            if (sentinelA == 0 && entries[a].Pct != entries[b].Pct)
                return entries[a].Pct - entries[b].Pct;
            return a - b;
        });
        return order;
    }

    // Widest list that fits: all of them, then one fewer, and so on.
    static string? FirstThatFits(IReadOnlyList<TooltipEntry> entries, List<int> keepOrder, int max)
    {
        for (var keep = entries.Count; keep >= 1; keep--)
        {
            var text = Render(entries, keepOrder, keep, string.Empty);
            if (text.Length <= max)
                return text;
        }
        return null;
    }

    static string Render(
        IReadOnlyList<TooltipEntry> entries, List<int> keepOrder, int keep, string suffix)
    {
        var kept = new bool[entries.Count];
        for (var i = 0; i < keep; i++)
            kept[keepOrder[i]] = true;

        var parts = new List<string>(keep + 1);
        for (var i = 0; i < entries.Count; i++)
        {
            if (kept[i])
                parts.Add(entries[i].Text);
        }

        var dropped = entries.Count - keep;
        if (dropped > 0)
            parts.Add($"+{dropped} more");

        return string.Join(" | ", parts) + suffix;
    }
}

// One device's answer to "which driver is Windows actually using, and where
// does the battery percent come from" for the devices this app installs
// nothing for. Both halves are tri-state by being nullable: null is "not
// read", never "broken".
//
// Sdp is only ever read for a Magic Keyboard - a trackpad reads its percent
// from HID Input report 0x90 with nothing installed, so the SDP patch has
// nothing to do with it (MouseBatteryDevice.KnownMice).
internal readonly record struct StockFacts(StockDriverInfo? Driver, SdpPatchState? Sdp);

// System tray icon, per-device menu, battery alerts. Driver work is always
// user-initiated via DriverInstaller — never a silent rebind.
internal sealed class TrayApp : IDisposable
{
    readonly NotifyIcon _tray;
    readonly Config _config;
    readonly AdaptivePoller _poller;
    readonly ToolStripMenuItem _startupItem;
    readonly ToolStripMenuItem _batteryReadsItem;
    readonly ToolStripMenuItem _globalThresholdMenu;

    readonly Dictionary<string, (int Pct, DeviceKind Kind, string Pid)> _deviceBatteries = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ToolStripMenuItem> _deviceMenuItems = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> _firedEvents = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> _lastGoodPct = new(StringComparer.OrdinalIgnoreCase);
    // Last battery sentinel the poller reported, keyed by PID the same way the
    // findings code keys (TrayMenu.PidEq = OrdinalIgnoreCase). This is the value
    // RepairPlanner needs for its battery rule; the tray already has it, so the
    // planner is handed this map instead of anyone reading the device twice.
    readonly Dictionary<string, int> _lastBatteryByPid = new(StringComparer.OrdinalIgnoreCase);

    ToolStripMenuItem? _deviceSection;
    IReadOnlyList<DeviceDriverHealth> _health = Array.Empty<DeviceDriverHealth>();

    CriticalAlert? _criticalAlert;
    string? _criticalDevice;
    bool _criticalStayOnDisconnect;
    Icon? _currentIcon;
    static bool _lightTaskbar;
    ToolStripMenuItem? _updateItem;
    ToolStripMenuItem? _repairItem;
    ToolStripMenuItem? _configItem;
    // What SystemConfigChecker last read about THIS PC. Started by the same
    // RefreshFindings funnel as _findings, but read on the thread pool
    // (BeginConfigFactsRead) because Check can spawn a bcdedit read, so this
    // field is written only by PublishConfigFacts back on the UI thread. A
    // read that failed publishes nothing and leaves the previous facts
    // standing, exactly as a failed snapshot read leaves the previous
    // findings standing.
    IReadOnlyList<ConfigFact> _configFacts = Array.Empty<ConfigFact>();
    // The driver Windows actually has on the devices this app installs nothing
    // for - keyboards and trackpads - plus, for a keyboard, whether this
    // repo's SDP registry patch is in its Bluetooth record. Both are REGISTRY
    // reads, so they ride the same off-UI-thread path as the configuration
    // facts above (BeginConfigFactsRead / PublishConfigFacts) and are written
    // only on the UI thread by the publish. No new timer and no new cadence:
    // this pair is read exactly when the configuration facts are.
    //
    // A missing entry is not a fault. Mice never get one (they have their own
    // DriverStatus and their own Driver submenu), and a device whose registry
    // could not be read keeps the wording it had.
    IReadOnlyDictionary<string, StockFacts> _stockFacts = EmptyStockFacts;
    static readonly IReadOnlyDictionary<string, StockFacts> EmptyStockFacts =
        new Dictionary<string, StockFacts>(StringComparer.OrdinalIgnoreCase);
    // 1 while a configuration read is on the thread pool. Interlocked because
    // it is set on the UI thread and cleared on the pool thread.
    int _configReadInFlight;
    // Only CONFIRMED findings live here: RefreshFindings routes the planner's raw
    // output through _findingGate first, so the menu row, the per-device sub-row,
    // the toasts and the guided dialogs all inherit that confirmation for free.
    IReadOnlyList<RepairFinding> _findings = Array.Empty<RepairFinding>();
    // Raw findings the gate is still holding. Never shown as a fault and never
    // toasted, but an explicit user-initiated check reports them rather than
    // answering "No problems found" while a real fault is mid-confirmation.
    IReadOnlyList<RepairFinding> _pendingFindings = Array.Empty<RepairFinding>();
    // The snapshots the current _findings were planned from, so the device rows
    // can state per-capability health without a second read.
    IReadOnlyList<DeviceSnapshot> _snapshots = Array.Empty<DeviceSnapshot>();
    readonly HashSet<string> _toastedFindings = new(StringComparer.OrdinalIgnoreCase);
    // One gate for the process: a fault has to persist across refreshes before it
    // is believed, so a driver install's stack churn cannot toast or offer a
    // repair that would fight the install (FindingGate.cs).
    readonly FindingGate _findingGate = new();

    // --- Startup / resume health settle (BeginHealthSettle) ----------------
    // One re-armed-by-hand timer, never a recurring one: the adaptive poller
    // stays the only periodic reader in the process, and this sequence stops
    // itself after SettleAttempts.
    readonly object _settleLock = new();
    System.Threading.Timer? _settleTimer;
    int _settleAttempt;
    string _settleTrigger = "startup";
    // Written under _settleLock in Dispose, but also read on the UI thread
    // after a tick has released the lock, so the read has to see it.
    volatile bool _settleStopped;

    // Why a settle sequence exists at all: every refresh the tray does at
    // startup lands inside the same second (ctor RefreshFindings plus the
    // poller's first tick), and the poll interval after that has been observed
    // live at POLL_SCHEDULED next_in=1.00:00:00. FindingGate needs
    // MinObservations (2) separate observations AND FindingGate.HoldWindow
    // (15 s) of elapsed time between the first and the latest one, so a fault
    // genuinely standing at boot could not confirm until the user happened to
    // open the menu. The same one-second clustering also makes
    // MultitouchAdvancing useless: it is a delta over the driver's
    // Diag\AclTranslateCount, and two samples taken in the same second read as
    // "no evidence".
    //
    // Lead - the first attempt waits this long so the Bluetooth link and the
    // driver package's multitouch watcher have started moving before anything
    // is judged. The watcher's measured boot race was 32 s (boot 14:29:44,
    // subscription 14:30:16), so the first sample is deliberately NOT treated
    // as the verdict, only as observation one.
    static readonly TimeSpan SettleLead = TimeSpan.FromSeconds(8);

    // Step - read from FindingGate.HoldWindow, not hardcoded, plus 2 s of
    // margin because Confirm's elapsed test is >= HoldWindow against a clock
    // sampled inside the refresh. Consecutive attempts are therefore always
    // more than one hold window apart, which is exactly the pair FindingGate
    // needs to confirm, and is far enough apart for the AclTranslateCount
    // delta to mean something.
    static readonly TimeSpan SettleStep = FindingGate.HoldWindow + TimeSpan.FromSeconds(2);

    // Three attempts: at +8 s, +25 s and +42 s after the trigger. Any two
    // consecutive ones are SettleStep (17 s) apart, so the sequence contains
    // two independent confirming pairs - (1,2) for a fault already standing at
    // +8 s, and (2,3) for one that only becomes visible after the watcher has
    // had its 32 s. The whole span (42 s) is well past HoldWindow and then the
    // tray goes quiet again.
    const int SettleAttempts = 3;

    const string V3DocUrl = "https://magictray.app/v3.html";
    const string DriversDocUrl = "https://magictray.app/drivers.html";

    // Written by the mouse driver package's multitouch watcher. Magic Tray only
    // ever names this path in guidance: parsing it belongs to DeviceDiagReader,
    // and re-implementing the watcher is an explicit non-goal
    // (docs/ENABLE-DISABLE.md:93-97).
    const string MultitouchWatcherLogPath = @"C:\ProgramData\MagicMouseDriver\auto-f1-watcher.log";

    internal TrayApp(Config config)
    {
        _config = config;
        _startupItem = null!;
        _batteryReadsItem = null!;
        _globalThresholdMenu = null!;

        _health = ReadHealth();
        var menu = BuildMenu(out _startupItem, out _batteryReadsItem, out _globalThresholdMenu);
        RefreshFindings();

        RefreshTheme();
        _currentIcon = MakeIcon(-1, false, Marker.Mouse, AnyDriverAttention());
        _tray = new NotifyIcon
        {
            Icon = _currentIcon,
            ContextMenuStrip = menu,
            Visible = true,
            Text = $"{TrayMenu.ProductName} — starting..."
        };

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemVisualChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemVisualChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _poller = new AdaptivePoller(_config);
        _poller.BatteryChanged += OnBatteryChanged;
        _poller.Start();

        // Boot is exactly when this device family breaks - the mouse loses its
        // multitouch stream unless the driver package's watcher re-sends its
        // enable report, and that watcher has its own boot race - so the tray
        // re-checks a few times on its own instead of waiting for the user to
        // open the menu.
        BeginHealthSettle("startup");

        // A reading that never confirmed its restore may have parked this mouse
        // in the shape where its wheel does not work, so the offer is made once
        // per process - here, and never from the menu-open or settle paths that
        // would re-ask it. OfferStaleModeARestore queues the modal instead of
        // showing it, so the tray icon appears whether or not anyone is at the
        // keyboard to answer.
        if (ModeFlip.StaleModeAOnStartup())
            OfferStaleModeARestore();

        if (_config.UpdateCheck)
            _ = CheckForUpdateBackgroundAsync();
    }

    async Task CheckForUpdateBackgroundAsync()
    {
        var tag = await UpdateChecker.CheckForUpdateAsync();
        if (tag != null)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_updateItem != null)
                {
                    _updateItem.Text = $"Update available — {tag}";
                    _updateItem.Visible = true;
                }
            });
        }
    }

    /// <summary>
    /// Builds the tray context menu, including the Help section entries that open the
    /// pre-filled bug and feature drafts. Rebuilt state is refreshed on Opening.
    /// </summary>
    ContextMenuStrip BuildMenu(
        out ToolStripMenuItem startupItem,
        out ToolStripMenuItem batteryReadsItem,
        out ToolStripMenuItem globalThresholdMenu)
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            _health = ReadHealth();
            RefreshFindings();
            UpdateDeviceMenuItems();
            RefreshGlobalThresholdChecks();
            UpdateBatteryReadsVisibility();
        };

        _repairItem = new ToolStripMenuItem(TrayMenu.MenuText(
            RepairPlanner.MenuLabel(HeadlineFindings())));
        if (HeadlineFindings().Count > 0)
            _repairItem.ForeColor = Color.OrangeRed;
        _repairItem.Click += (_, _) => ShowRepairFlow();
        menu.Items.Add(_repairItem);

        // Directly BELOW the fault row, never above it: a device fault is a
        // measured problem with the hardware in front of the user, while a
        // configuration fact at most explains one, so the fault keeps the first
        // line of the menu (ConfigFactView.cs:16-33). Hidden until a check has
        // actually produced a fact - an empty section claiming "checked" would
        // be claiming a verification that never ran.
        _configItem = new ToolStripMenuItem(TrayMenu.MenuText(ConfigFactView.SectionPrefix)) { Visible = false };
        menu.Items.Add(_configItem);
        RefreshConfigSection();
        menu.Items.Add(new ToolStripSeparator());

        _deviceSection = new ToolStripMenuItem(TrayMenu.MenuText("Devices")) { Enabled = false };
        menu.Items.Add(_deviceSection);
        menu.Items.Add(new ToolStripSeparator());

        var bluetoothMenu = new ToolStripMenuItem(TrayMenu.MenuText(BluetoothSettings.MenuLabel));
        foreach (var label in BluetoothSettings.MenuItemLabels)
        {
            var btItem = new ToolStripMenuItem(TrayMenu.MenuText(label));
            if (label == BluetoothSettings.RenameADevice)
                btItem.Click += (_, _) => BluetoothSettings.OpenRenamePage();
            else
                btItem.Click += (_, _) => BluetoothSettings.OpenDevicesPage();
            bluetoothMenu.DropDownItems.Add(btItem);
        }
        menu.Items.Add(bluetoothMenu);

        globalThresholdMenu = new ToolStripMenuItem(TrayMenu.MenuText("Low battery threshold"));
        foreach (var t in Config.ThresholdChoices)
        {
            var tItem = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.GlobalThresholdLabel(t)))
            {
                Checked = t == _config.GlobalThreshold,
                Tag = t,
            };
            tItem.Click += (_, _) =>
            {
                _config.SetGlobalThreshold(t);
                RefreshGlobalThresholdChecks();
                UpdateTrayIcon();
            };
            globalThresholdMenu.DropDownItems.Add(tItem);
        }
        menu.Items.Add(globalThresholdMenu);

        startupItem = new ToolStripMenuItem(TrayMenu.MenuText("Start with Windows"))
        {
            Checked = _config.StartWithWindows
        };
        startupItem.Click += (_, _) =>
        {
            _config.SetStartWithWindows(!_config.StartWithWindows);
            _startupItem.Checked = _config.StartWithWindows;
        };
        menu.Items.Add(startupItem);

        var thirdPartyItem = new ToolStripMenuItem(TrayMenu.MenuText(
            _config.EnableThirdParty ? "Show Logitech devices [On]" : "Show Logitech devices [Off]"))
        {
            Checked = _config.EnableThirdParty
        };
        thirdPartyItem.Click += (_, _) =>
        {
            _config.SetEnableThirdParty(!_config.EnableThirdParty);
            thirdPartyItem.Text = _config.EnableThirdParty ? "Show Logitech devices [On]" : "Show Logitech devices [Off]";
            thirdPartyItem.Checked = _config.EnableThirdParty;
        };
        menu.Items.Add(thirdPartyItem);

        batteryReadsItem = new ToolStripMenuItem(TrayMenu.MenuText("Battery reads"))
        {
            Checked = true,
            Enabled = false,
            Visible = false
        };
        menu.Items.Add(batteryReadsItem);

        menu.Items.Add(new ToolStripSeparator());

        var refresh = new ToolStripMenuItem(TrayMenu.MenuText("Refresh Now"));
        refresh.Click += (_, _) => _poller.RefreshNow();
        menu.Items.Add(refresh);

        var diagnostics = new ToolStripMenuItem(TrayMenu.MenuText("Diagnostics"));

        var checkProblems = new ToolStripMenuItem(TrayMenu.MenuText("Check for problems now"));
        // One extra observation for the gate, then ShowRepairFlow answers - and it
        // reports a fault still being confirmed rather than claiming health.
        checkProblems.Click += (_, _) =>
        {
            RefreshFindings();
            ShowRepairFlow();
        };
        diagnostics.DropDownItems.Add(checkProblems);

        var testToast = new ToolStripMenuItem(TrayMenu.MenuText("Test notification"));
        testToast.Click += (_, _) =>
        {
            var live = _deviceBatteries.FirstOrDefault(kv => kv.Value.Pct >= 0);
            string name;
            DeviceKind kind;
            int pct;
            if (!string.IsNullOrEmpty(live.Key))
            {
                name = live.Key;
                kind = live.Value.Kind;
                pct = live.Value.Pct;
            }
            else
            {
                name = TrayMenu.ProductName;
                kind = DeviceKind.MagicMouseV3;
                pct = 10;
            }
            var preview = BatteryAlertPolicy.PreviewToast(kind, name, pct);
            ToastNotifier.Show(preview.Title, preview.Body);
        };
        diagnostics.DropDownItems.Add(testToast);

        var openLogs = new ToolStripMenuItem(TrayMenu.MenuText("Open logs"));
        openLogs.Click += (_, _) => OpenLogsInEditor();
        diagnostics.DropDownItems.Add(openLogs);

        var openDiagFolder = new ToolStripMenuItem(TrayMenu.MenuText("Open diagnostics folder"));
        openDiagFolder.Click += (_, _) => OpenDiagnosticsFolder();
        diagnostics.DropDownItems.Add(openDiagFolder);

        AddDiagnosticScript(diagnostics, DiagnosticScripts.CaptureStateLabel,
            DiagnosticScripts.Find(DiagnosticScripts.CaptureState));
        AddDiagnosticScript(diagnostics, DiagnosticScripts.DiagnoseDriverLabel,
            DiagnosticScripts.Find(DiagnosticScripts.DiagnoseDriver));
        AddDiagnosticScript(diagnostics, DiagnosticScripts.DiagnoseAndRecoverLabel,
            DiagnosticScripts.Find(DiagnosticScripts.DiagnoseAndRecover));
        var stack = DiagnosticScripts.FindStackDump();
        if (stack is not null)
            AddDiagnosticScript(diagnostics, stack.Value.Label, stack.Value.Path);

        menu.Items.Add(diagnostics);

        var help = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.HelpMenuLabel));
        var howAlerts = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.HowAlertsWorkLabel));
        howAlerts.Click += (_, _) => OpenHelpUrl(TrayMenu.AlertsDocUrl, TrayMenu.FindLocalAlertsDoc());
        help.DropDownItems.Add(howAlerts);
        var repoItem = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.RepositoryLabel));
        repoItem.Click += (_, _) => OpenHelpUrl(TrayMenu.RepoUrl);
        help.DropDownItems.Add(repoItem);
        var bugItem = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.ReportBugLabel));
        bugItem.Click += (_, _) => OpenGitHubDraft(feature: false);
        help.DropDownItems.Add(bugItem);
        var featItem = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.RequestFeatureLabel));
        featItem.Click += (_, _) => OpenGitHubDraft(feature: true);
        help.DropDownItems.Add(featItem);
        menu.Items.Add(help);

        var starItem = new ToolStripMenuItem(TrayMenu.StarLabel(_config.StarClicked));
        starItem.Click += (_, _) =>
        {
            // Only flip once the browser actually launched — a failed handoff
            // must leave the ask in place.
            if (!OpenHelpUrl(TrayMenu.RepoUrl)) return;
            _config.SetStarClicked(true);
            starItem.Text = TrayMenu.StarLabel(true);
        };
        menu.Items.Add(starItem);

        menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem(TrayMenu.MenuText("Quit"));
        quit.Click += (_, _) =>
        {
            Dispose();
            System.Windows.Application.Current.Shutdown();
        };
        menu.Items.Add(quit);

        menu.Items.Add(new ToolStripSeparator());

        var asmVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var semver = asmVer != null ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}" : "1.0.0";
        menu.Items.Add(new ToolStripMenuItem(TrayMenu.MenuText($"{TrayMenu.ProductName} {semver}")) { Enabled = false });

        _updateItem = new ToolStripMenuItem(TrayMenu.MenuText("Update available")) { Visible = false };
        _updateItem.Click += (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TrayMenu.ReleasesUrl)
            {
                UseShellExecute = true
            });
        };
        menu.Items.Add(_updateItem);

        return menu;
    }

    void RefreshGlobalThresholdChecks()
    {
        foreach (ToolStripMenuItem tItem in _globalThresholdMenu.DropDownItems)
        {
            if (tItem.Tag is int t)
                tItem.Checked = t == _config.GlobalThreshold;
        }
    }

    void UpdateBatteryReadsVisibility()
    {
        var v3Connected = _deviceBatteries.Values.Any(v => TrayMenu.IsV3(v.Kind, v.Pid));
        DriverStatus? v3Status = null;
        foreach (var h in _health)
        {
            if (TrayMenu.PidEq(h.Pid, "0323"))
            {
                v3Status = h.Status;
                break;
            }
        }
        _batteryReadsItem.Visible = TrayMenu.ShowBatteryReads(v3Connected, v3Status);
        _batteryReadsItem.Checked = _batteryReadsItem.Visible;
        _batteryReadsItem.Enabled = false;
    }

    void OpenLogsInEditor()
    {
        var logPath = Logger.LogPath;
        if (!System.IO.File.Exists(logPath))
        {
            System.IO.Directory.CreateDirectory(Logger.LogDir);
            System.IO.File.WriteAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Log file created on demand from tray.\r\n");
        }

        var npp = FindNotepadPlusPlus();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = npp ?? "notepad.exe",
                Arguments = $"\"{logPath}\"",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            Logger.Log($"OPEN_LOGS editor={(npp ?? "notepad.exe")} path={logPath}");
        }
        catch (Exception ex)
        {
            Logger.Log($"OPEN_LOGS_FAIL err={ex.Message}");
        }
    }

    /// <summary>
    /// Opens the folder holding debug.log so a reporter can attach it by hand;
    /// failures are logged, never surfaced as a dialog.
    /// </summary>
    void OpenDiagnosticsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Logger.LogDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Logger.LogDir}\"",
                UseShellExecute = true
            });
            Logger.Log($"OPEN_DIAG_FOLDER path={Logger.LogDir}");
        }
        catch (Exception ex)
        {
            Logger.Log($"OPEN_DIAG_FOLDER_FAIL err={ex.Message}");
        }
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser, then
    /// <paramref name="localFallback"/> if that throws. Returns true only when a
    /// browser (or the local fallback) actually launched, so callers can run their
    /// own fallback instead of assuming success.
    /// </summary>
    bool OpenHelpUrl(string url, string? localFallback = null)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"OPEN_HELP_FAIL url={url} err={ex.Message}");
            if (string.IsNullOrEmpty(localFallback))
                return false;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(localFallback)
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex2)
            {
                Logger.Log($"OPEN_HELP_LOCAL_FAIL path={localFallback} err={ex2.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Confirms with the user, builds the redacted bug or feature Markdown, copies it
    /// to the clipboard, and opens the pre-filled GitHub draft. On launch failure it
    /// logs GITHUB_DRAFT_FAIL and opens the plain issues page instead.
    /// </summary>
    /// <param name="feature">true for a feature request, false for a bug report.</param>
    void OpenGitHubDraft(bool feature)
    {
        var caption = feature ? TrayMenu.RequestFeatureLabel : TrayMenu.ReportBugLabel;
        var confirm = feature ? TrayMenu.RequestFeatureConfirm : TrayMenu.ReportBugConfirm;
        if (MessageBox.Show(confirm, caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
            != DialogResult.OK)
            return;

        try
        {
            var version = BugReport.AppVersion();
            var os = BugReport.OsDescription();
            string md;
            string title;
            string url;
            if (feature)
            {
                md = BugReport.FormatFeatureMarkdown(version, os);
                title = BugReport.FeatureTitle(version);
                url = BugReport.IssueUrl(title, md, "enhancement");
            }
            else
            {
                var rows = BugReport.Collect(_health, _deviceBatteries);
                var log = BugReport.ReadLogTail(Logger.LogPath, BugReport.LogTailLines);
                md = BugReport.FormatMarkdown(version, os, _config.Driver0323, rows, log);
                title = BugReport.IssueTitle(rows, version);
                url = BugReport.IssueUrl(title, md, "bug");
            }
            Clipboard.SetText(md);
            if (!OpenHelpUrl(url))
            {
                Logger.Log($"GITHUB_DRAFT_FAIL feature={feature} err=draft_launch_failed");
                OpenHelpUrl(TrayMenu.IssuesUrl);
                return;
            }
            Logger.Log(feature ? "FEATURE_DRAFT opened" : "BUG_REPORT opened");
        }
        catch (Exception ex)
        {
            Logger.Log($"GITHUB_DRAFT_FAIL feature={feature} err={ex.Message}");
            OpenHelpUrl(TrayMenu.IssuesUrl);
        }
    }

    static void AddDiagnosticScript(ToolStripMenuItem diagnostics, string label, string? path)
    {
        if (path is null) return;
        var item = new ToolStripMenuItem(TrayMenu.MenuText(label));
        item.Click += (_, _) => RunDiagnosticScript(path);
        diagnostics.DropDownItems.Add(item);
    }

    static void RunDiagnosticScript(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(DiagnosticScripts.StartInfo(path));
            Logger.Log($"DIAG_SCRIPT path={path}");
        }
        catch (Exception ex)
        {
            Logger.Log($"DIAG_SCRIPT_FAIL path={path} err={ex.Message}");
        }
    }

    static string? FindNotepadPlusPlus()
    {
        string[] candidates =
        {
            @"C:\Program Files\Notepad++\notepad++.exe",
            @"C:\Program Files (x86)\Notepad++\notepad++.exe",
        };
        foreach (var c in candidates)
            if (System.IO.File.Exists(c)) return c;
        return null;
    }

    void OnBatteryChanged(int pct, string name, DeviceKind kind, string pid)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // The sentinel map has to be current BEFORE the snapshot is planned,
            // or the planner judges this device on the previous tick's reading.
            // An empty name is the all-gone signal: the devices are gone, so the
            // readings are stale rather than blocked, and a stale -2/-3 left in
            // the map would keep accusing a device that is not even here.
            if (string.IsNullOrEmpty(name))
                _lastBatteryByPid.Clear();
            else if (!string.IsNullOrEmpty(pid))
                _lastBatteryByPid[pid] = pct;

            _health = ReadHealth();
            RefreshFindings();
            NotifyNewFindings();

            if (string.IsNullOrEmpty(name))
            {
                // All-gone: Evaluate every known name with pct=-1 (AA death / v3 CloseModal)
                // before wiping the tray. Mixed drop is handled by AdaptivePoller's named -1.
                var dropped = new Dictionary<string, (DeviceKind Kind, string Pid)>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _deviceBatteries)
                    dropped[kv.Key] = (kv.Value.Kind, kv.Value.Pid);
                foreach (var kv in _lastGoodPct)
                {
                    if (dropped.ContainsKey(kv.Key)) continue;
                    var inferred = DeviceCapability.KindForName(kv.Key) ?? DeviceKind.MagicMouseV1;
                    dropped[kv.Key] = (inferred, string.Empty);
                }
                foreach (var kv in dropped)
                    ApplyBatteryAlert(-1, kv.Key, kv.Value.Kind, kv.Value.Pid);
                _deviceBatteries.Clear();
                if (_criticalAlert != null && !_criticalStayOnDisconnect)
                {
                    _criticalAlert.Close();
                    Logger.Log("CRITICAL_ALERT_CLOSED reason=no_devices");
                }
            }
            else
            {
                name = new string(name.Where(c => !char.IsControl(c)).ToArray());
                _deviceBatteries[name] = (pct, kind, pid);
                ApplyBatteryAlert(pct, name, kind, pid);
            }

            UpdateTrayIcon();
        });
    }

    void ApplyBatteryAlert(int pct, string name, DeviceKind kind, string pid)
    {
        if (!_config.IsDeviceEnabled(pid))
            return;

        var threshold = _config.GetThreshold(pid);
        int lastGood = _lastGoodPct.TryGetValue(name, out var lg) ? lg : int.MinValue;
        if (pct >= 0)
            _lastGoodPct[name] = pct;

        if (!_firedEvents.TryGetValue(name, out var fired))
            _firedEvents[name] = fired = new HashSet<string>(StringComparer.Ordinal);

        var hours = pct >= 0
            ? DrainRateTracker.GetHoursToEmpty(name, pct)
            : -1;
        var rateKnown = hours >= 0;
        BatteryAlertPolicy.RearmFired(fired, kind, pct, hours, rateKnown, threshold);

        if (pct > 1 && _criticalDevice == name && _criticalAlert != null)
        {
            _criticalAlert.Close();
            Logger.Log("CRITICAL_ALERT_CLOSED reason=rearm");
        }

        var decision = BatteryAlertPolicy.Evaluate(
            kind, name, pct, threshold, hours, rateKnown,
            DateTime.Now, lastGood, fired);

        if (decision.EventId != null &&
            decision.Action is BatteryAlertAction.Toast or BatteryAlertAction.Modal)
            fired.Add(decision.EventId);

        if (decision.Action == BatteryAlertAction.Toast &&
            decision.Title != null && decision.Body != null)
        {
            ToastNotifier.Show(decision.Title, decision.Body);
        }
        else if (decision.Action == BatteryAlertAction.Modal &&
                 decision.Body != null && _criticalAlert == null)
        {
            _criticalAlert = new CriticalAlert(decision.Body);
            _criticalAlert.FormClosed += (_, _) =>
            {
                _criticalAlert = null;
                _criticalDevice = null;
                _criticalStayOnDisconnect = false;
            };
            _criticalDevice = name;
            _criticalStayOnDisconnect = BatteryAlertPolicy.IsAaPowered(kind);
            _criticalAlert.Show();
            Logger.Log($"CRITICAL_ALERT_SHOWN device={name} pct={pct}");
        }

        if (BatteryAlertPolicy.ShouldCloseModal(decision.CloseModal, _criticalDevice, name)
            && _criticalAlert != null)
        {
            _criticalAlert.Close();
            Logger.Log("CRITICAL_ALERT_CLOSED reason=disconnect");
        }
    }


    void OnSystemVisualChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.Invoke(() => { RefreshTheme(); UpdateTrayIcon(); });

    bool AnyDriverAttention()
    {
        foreach (var h in _health)
        {
            if (!_config.IsDeviceEnabled(h.Pid)) continue;
            if (TrayMenu.IconAttention(h.Pid, h.Status))
                return true;
        }
        return false;
    }

    DeviceDriverHealth? FindHealth(string pid)
    {
        foreach (var h in _health)
        {
            if (TrayMenu.PidEq(h.Pid, pid))
                return h;
        }
        return null;
    }

    void UpdateTrayIcon()
    {
        int lowestPct = -1;
        string lowestName = string.Empty;
        foreach (var kv in _deviceBatteries)
        {
            if (!_config.IsDeviceEnabled(kv.Value.Pid)) continue;
            if (kv.Value.Pct < 0) continue;
            if (lowestPct < 0 || kv.Value.Pct < lowestPct) { lowestPct = kv.Value.Pct; lowestName = kv.Key; }
        }

        bool anyLow = false;
        foreach (var kv in _deviceBatteries)
        {
            if (!_config.IsDeviceEnabled(kv.Value.Pid)) continue;
            if (kv.Value.Pct >= 0 && kv.Value.Pct <= _config.GetThreshold(kv.Value.Pid))
                anyLow = true;
        }

        var newIcon = MakeIcon(lowestPct, anyLow, MarkerFor(lowestName), AnyDriverAttention());
        var oldIcon = _currentIcon;
        _tray.Icon = newIcon;
        _currentIcon = newIcon;
        oldIcon?.Dispose();

        string tip;
        if (_deviceBatteries.Count == 0)
        {
            tip = TrayMenu.ClipTooltip($"{TrayMenu.ProductName} — no devices detected");
        }
        else
        {
            var entries = new List<TrayMenu.TooltipEntry>(_deviceBatteries.Count);
            foreach (var kv in _deviceBatteries)
            {
                var pct = kv.Value.Pct;
                entries.Add(new TrayMenu.TooltipEntry(
                    $"{kv.Key}: {TrayMenu.BatteryText(pct, EnabledInApp(kv.Value.Pid))}", pct));
            }

            // Composition owns the length: it drops whole devices, keeps the
            // lowest battery, and says "+N more" when it had to. Nothing is
            // cut mid-entry here any more, and the result is already inside
            // NotifyIcon.Text's measured 127-character limit.
            tip = TrayMenu.ComposeTooltip(entries, $" · {FormatInterval(_poller.LastInterval)}");
        }

        _tray.Text = tip;
        Logger.Log($"TRAY_UPDATE devices={_deviceBatteries.Count} lowest={lowestPct} tooltip=\"{_tray.Text}\"");
        UpdateDeviceMenuItems();
        UpdateBatteryReadsVisibility();
    }

    void UpdateDeviceMenuItems()
    {
        if (_deviceSection is null) return;
        var menu = _tray.ContextMenuStrip;
        if (menu is null) return;

        foreach (var item in _deviceMenuItems.Values)
            menu.Items.Remove(item);
        _deviceMenuItems.Clear();

        var shownPids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int insertAt = menu.Items.IndexOf(_deviceSection) + 1;

        if (_deviceBatteries.Count == 0)
        {
            _deviceSection.Text = "No devices detected";
            _deviceSection.Enabled = true;
            _deviceSection.DropDownItems.Clear();
            _deviceSection.DropDownItems.Add(new ToolStripMenuItem(TrayMenu.MenuText(
                "Pair a Magic Mouse or keyboard over Bluetooth, then Refresh Now")) { Enabled = false });
        }
        else
        {
            _deviceSection.Enabled = false;
            _deviceSection.DropDownItems.Clear();
            _deviceSection.Text = _deviceBatteries.Count == 1
                ? "1 device"
                : $"{_deviceBatteries.Count} devices";
        }

        foreach (var kv in _deviceBatteries.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            shownPids.Add(kv.Value.Pid);
            var item = BuildDeviceRow(kv.Key, kv.Value.Pct, kv.Value.Kind, kv.Value.Pid);
            _deviceMenuItems[kv.Key] = item;
            menu.Items.Insert(insertAt++, item);
        }

        foreach (var h in _health)
        {
            if (!TrayMenu.ShouldShowHealthRow(shownPids, h.Pid, h.Status)) continue;

            if (h.Status == DriverStatus.UnknownAppleMouse)
            {
                var key = $"unknown:{h.Pid}";
                if (_deviceMenuItems.ContainsKey(key)) continue;
                var item = BuildUnknownRow(h);
                _deviceMenuItems[key] = item;
                menu.Items.Insert(insertAt++, item);
                shownPids.Add(h.Pid);
                continue;
            }

            if (!MouseBatteryDevice.TryKnownMouse(h.Pid, out var name, out var kind))
                continue;
            var healthKey = $"health:{h.Pid}";
            if (_deviceMenuItems.ContainsKey(healthKey)) continue;
            shownPids.Add(h.Pid);
            var row = BuildDeviceRow(name, -1, kind, h.Pid);
            _deviceMenuItems[healthKey] = row;
            menu.Items.Insert(insertAt++, row);
        }

        if (_deviceBatteries.Count == 0 && _deviceMenuItems.Count > 0)
        {
            _deviceSection.Text = _deviceMenuItems.Count == 1
                ? "1 device"
                : $"{_deviceMenuItems.Count} devices";
            _deviceSection.Enabled = false;
            _deviceSection.DropDownItems.Clear();
        }
    }

    ToolStripMenuItem BuildDeviceRow(string name, int pct, DeviceKind kind, string pid)
    {
        var health = FindHealth(pid);
        var status = health?.Status;
        // What the last background read found for this device: the driver
        // Windows actually has on it, and where its battery percent comes
        // from. Empty for a mouse, which has DriverStatus instead, and empty
        // until the first read has published - both of which read as "not
        // read", never as a fault.
        var stockFacts = FindStockFacts(pid);


        string? badge = null;
        if (TrayMenu.IsV3(kind, pid))
            badge = TrayMenu.V3Badge(status);
        else if (TrayMenu.IsV1V2Mouse(kind))
            badge = TrayMenu.V1V2Badge(status);
        else if (status == DriverStatus.UnknownAppleMouse)
            badge = "Unknown model";

        var extras = new List<string>();
        var rate = DrainRateTracker.GetDrainRatePctPerHour(name);
        if (rate > 0.001) extras.Add($"{rate:F1}%/h");
        if (pct >= 0)
        {
            var hoursLeft = DrainRateTracker.GetHoursToEmpty(name, pct);
            if (hoursLeft > 24) extras.Add($"~{(hoursLeft / 24.0):F1}d");
            else if (hoursLeft > 0)
                extras.Add($"~{Math.Max(1, (int)Math.Round(hoursLeft))}h");
        }

        var enabledInApp = EnabledInApp(pid);
        var item = new ToolStripMenuItem(TrayMenu.MenuText(
            TrayMenu.RowLabel(name, pct, badge, string.Join("  ", extras), enabledInApp)));
        item.AccessibleName = string.IsNullOrEmpty(badge)
            ? $"{name}, {TrayMenu.BatteryText(pct, enabledInApp)}"
            : $"{name}, {TrayMenu.BatteryText(pct, enabledInApp)}, {badge}";

        // Per-capability status first: what works, what does not, and what is
        // simply not known. These are labels, not actions.
        var facts = FactsFor(pid, pct, kind, stockFacts.Sdp);
        foreach (var line in DeviceCapability.Rows(facts))
            item.DropDownItems.Add(new ToolStripMenuItem(TrayMenu.MenuText(line)) { Enabled = false });

        // No automatic multitouch fault exists: Diag counter movement proves the
        // stream is flowing, but counters standing still cannot tell an idle
        // mouse from a broken one, and Diag\LastAclReceived was measured
        // oscillating 23/9 on a mouse whose wheel works. So the symptom is the
        // user's to assert, and this item is how they assert it.
        if (DeviceCapability.OfferScrollHelp(facts))
        {
            var helpPid = pid;
            var scrollHelp = new ToolStripMenuItem(
                TrayMenu.MenuText(DeviceCapability.ScrollHelpItemLabel));
            scrollHelp.Click += (_, _) => ShowScrollGuidance(helpPid);
            item.DropDownItems.Add(scrollHelp);
        }

        item.DropDownItems.Add(new ToolStripSeparator());

        var rowFinding = FindFinding(pid);
        if (rowFinding != null)
        {
            var scoped = rowFinding;
            var fixItem = new ToolStripMenuItem(TrayMenu.MenuText(scoped.Title)) { ForeColor = Color.OrangeRed };
            fixItem.Click += (_, _) => ShowRepairFlow(scoped);
            item.DropDownItems.Add(fixItem);
        }

        if (status == DriverStatus.UnknownAppleMouse)
        {
            var warn = new ToolStripMenuItem(TrayMenu.MenuText("Unknown Apple mouse — check for an app update"))
            {
                ForeColor = Color.OrangeRed
            };
            warn.Click += (_, _) => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(TrayMenu.ReleasesUrl) { UseShellExecute = true });
            item.DropDownItems.Add(warn);
        }


        if (TrayMenu.ShowFixKeyboard(kind, pct))
        {
            var fix = new ToolStripMenuItem(TrayMenu.MenuText("Fix battery reads")) { ForeColor = Color.OrangeRed };
            fix.Click += (_, _) => RunDriverAction(DriverInstaller.OfferKeyboardSdpPatch);
            item.DropDownItems.Add(fix);
        }

        if (TrayMenu.ShowTrackpadV1BootCamp(kind, pid))
        {
            var offerPid = pid;
            var bootCamp = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.TrackpadV1BootCampLabel));
            bootCamp.Click += (_, _) =>
                RunDriverAction(() => DriverInstaller.OfferTrackpadV1BootCamp(offerPid));
            item.DropDownItems.Add(bootCamp);
        }

        if (TrayMenu.IsV3(kind, pid))
        {
            var driverMenu = new ToolStripMenuItem(TrayMenu.MenuText($"Driver: {TrayMenu.V3Badge(status)}"));

            driverMenu.DropDownItems.Add(new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.BoundLabel(health?.BoundDriverName))) { Enabled = false });
            // One advice row, always present: DriverAdvisor covers every
            // DriverStatus, so there is no state left where the submenu says
            // nothing about what to be on. Short here, full on click.
            driverMenu.DropDownItems.Add(BuildAdviceItem(kind, pid, status, stockFacts));

            var kmdf = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.V3RadioKmdf))
            {
                Checked = status == DriverStatus.PatchedKmdf
            };
            if (status != DriverStatus.PatchedKmdf)
                kmdf.Click += (_, _) => _ = RunDriverActionAsync(
                    () => OfferThenRemember(
                        Config.Driver0323Kmdf, () => DriverInstaller.OfferV3KmdfInstallAsync()));
            else
                kmdf.Enabled = false;
            driverMenu.DropDownItems.Add(kmdf);

            var patchedApple = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.V3RadioPatchedApple))
            {
                Checked = status == DriverStatus.PathAPatched
            };
            // The Apple route can displace a working KMDF bind (driver repo
            // issue #27), so the offer carries a displacement warning. It is
            // handed THIS device's status - the same reading the radio above
            // renders its Checked state from - so the warning fires for a user
            // who is actually on KMDF and stays silent for one on Stock or the
            // patched Apple filter, who has nothing to lose. Omitting it would
            // warn everybody, which is the false-alarm class this menu has
            // been clearing out.
            var appleRouteStatus = status;
            if (status != DriverStatus.PathAPatched)
                patchedApple.Click += (_, _) => _ = RunDriverActionAsync(
                    () => OfferThenRemember(
                        Config.Driver0323PathA,
                        () => DriverInstaller.OfferV3PathAInstallAsync(appleRouteStatus)));
            else
                patchedApple.Enabled = false;
            driverMenu.DropDownItems.Add(patchedApple);

            var stock = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.V3RadioStockWindows))
            {
                Checked = status == DriverStatus.StockKmdf
            };
            if (status != DriverStatus.StockKmdf)
                stock.Click += (_, _) => _ = RunDriverActionAsync(
                    () => OfferThenRemember(
                        Config.Driver0323Stock, () => DriverInstaller.OfferV3StockRestoreAsync()));
            else
                stock.Enabled = false;
            driverMenu.DropDownItems.Add(stock);

            if (TrayMenu.ShowPathAModeSwitch(kind, pid, status))
            {
                // One action, not a pair of sticky radios. The mouse is only in
                // battery mode for the 10-20 seconds of a reading, so a checked
                // "Battery" radio described a state nobody can usefully be left
                // in - and the old pair persisted the driver choice before the
                // flip was even attempted, so a silent failure left the config
                // and the machine disagreeing. Neither happens here.
                var flipPid = pid;
                var flipName = name;
                var flipKind = kind;
                var flip = new ToolStripMenuItem(TrayMenu.MenuText(ModeFlipView.MenuItemLabel()));
                flip.Click += (_, _) =>
                {
                    // The one confirmation, stating the cost before consent.
                    // Cancel does nothing at all: nothing elevated, nothing
                    // written, and no driver choice persisted - a reading does
                    // not change which driver the user picked.
                    var answer = System.Windows.Forms.MessageBox.Show(
                        ModeFlipView.OfferText(flipPid), TrayMenu.ProductName,
                        System.Windows.Forms.MessageBoxButtons.OKCancel,
                        System.Windows.Forms.MessageBoxIcon.Information);
                    if (answer != System.Windows.Forms.DialogResult.OK)
                        return;
                    _ = RunDriverActionAsync(async () =>
                    {
                        // Off the UI thread: the cycle blocks on an elevated
                        // script, and ModeFlip's own budget is 45 s to reach
                        // battery mode plus the restore on top of that.
                        var result = await Task.Run(
                            () => ModeFlip.ReadBatteryViaFlip(ModeFlip.ReadCol02BatteryPercent));
                        ReportModeFlip(result, flipName, flipKind, flipPid);
                    });
                };
                driverMenu.DropDownItems.Add(flip);

                // The item every restore-failed sentence tells the user to hunt
                // for (ModeFlip.RestoreWarning, ModeFlipView.RestoreItemLabel),
                // so it is on this driver whether or not a flip has run yet.
                var restore = new ToolStripMenuItem(TrayMenu.MenuText(ModeFlipView.RestoreItemLabel));
                restore.Click += (_, _) => _ = RunDriverActionAsync(async () =>
                {
                    var result = await Task.Run(ModeFlip.RestoreModeB);
                    ReportModeFlip(result, null, flipKind, flipPid);
                });
                driverMenu.DropDownItems.Add(restore);
            }

            AddDriverChoices(driverMenu.DropDownItems, kind, pid, separator: true);

            item.DropDownItems.Add(driverMenu);
        }
        else if (TrayMenu.IsV1V2Mouse(kind))
        {
            var driverMenu = new ToolStripMenuItem(TrayMenu.MenuText($"Driver: {TrayMenu.V1V2Badge(status)}"));

            driverMenu.DropDownItems.Add(new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.BoundLabel(health?.BoundDriverName))) { Enabled = false });
            driverMenu.DropDownItems.Add(BuildAdviceItem(kind, pid, status, stockFacts));

            var bootCamp = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.V1V2RadioBootCamp))
            {
                Checked = status == DriverStatus.Ok
            };
            if (TrayMenu.V1V2BootCampRadioEnabled(status))
                bootCamp.Click += (_, _) => RunDriverAction(DriverInstaller.OfferV1V2ScrollFix);
            else
                bootCamp.Enabled = false;
            driverMenu.DropDownItems.Add(bootCamp);

            var stock = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.V1V2RadioStockWindows))
            {
                Checked = status == DriverStatus.NotInstalled
            };
            var stockPid = pid;
            if (TrayMenu.V1V2StockRadioEnabled(status))
                stock.Click += (_, _) => RunDriverAction(() => DriverInstaller.OfferV1V2StockRestore(stockPid));
            else
                stock.Enabled = false;
            driverMenu.DropDownItems.Add(stock);

            AddDriverChoices(driverMenu.DropDownItems, kind, pid, separator: true);

            item.DropDownItems.Add(driverMenu);
        }
        else
        {
            // Keyboards and trackpads have no Driver submenu, so the advice
            // row is the device row's own. It is a statement about the present
            // state, like the capability lines above it, so it sits with them.
            //
            // This is also the branch where the driver had to be READ rather
            // than classified: DriverHealthChecker skips these PIDs, so
            // without StockDriverReader the row said "The driver bound to this
            // device has not been read yet" over a Magic Keyboard whose whole
            // stack - hidbth.inf, kbdhid via keyboard.inf, hidserv.inf on
            // COL02/COL03 - is readable and every node Status OK (measured
            // 2026-09-16). The facts go in here so the sentence can say it.
            item.DropDownItems.Add(BuildAdviceItem(kind, pid, status, stockFacts));

            // The predictions do NOT sit with them.
            AddDriverChoices(item.DropDownItems, kind, pid, separator: false);
        }

        item.DropDownItems.Add(BuildShowInTrayItem(pid, name));
        item.DropDownItems.Add(BuildWindowsDeviceMenu(pid, name));
        item.DropDownItems.Add(new ToolStripSeparator());
        var thrMenu = new ToolStripMenuItem(TrayMenu.MenuText("Low battery alert"));
        var currentThr = _config.GetThreshold(pid);
        foreach (var t in Config.ThresholdChoices)
        {
            var hours = DrainRateTracker.GetHoursToEmpty(name, t);
            var tItem = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.DeviceThresholdLabel(t, hours)))
            {
                Checked = t == currentThr,
                Tag = t,
            };
            var pidStr = pid;
            tItem.Click += (_, _) =>
            {
                _config.SetThreshold(pidStr, t);
                UpdateTrayIcon();
            };
            thrMenu.DropDownItems.Add(tItem);
        }
        item.DropDownItems.Add(thrMenu);

        return item;
    }

    // Two different things used to live in one checkbox labelled "Enabled on
    // this PC", and the ordering inside it threw the user's choice away:
    //
    //     DeviceEnable.Apply(pid, want);        // elevated pnputil - THREW
    //     _config.SetDeviceEnabled(pid, want);  // never reached
    //
    // A user ticked the box for a Magic Mouse v1 (030d) that Windows had
    // already enabled. pnputil had nothing to do and exited 1, Apply threw,
    // and the tray's own state was never written: the mouse stayed switched
    // off in the app, stayed unpolled, and its row still read "Battery: No
    // reading" for a mouse measured at 97% via HID Feature report 0x47. The
    // dialog then blamed UAC, which the user never saw.
    //
    // So the two meanings are now two items:
    //   - this one is the TRAY state (enabled_<pid> in config.ini): whether
    //     Magic Tray polls and shows the device. Pure user space - no
    //     elevation, no PnP, nothing that can fail - so it is written
    //     immediately with no confirmation, and it is the ONLY source of
    //     Checked. The checkbox can no longer disagree with what was saved.
    //   - BuildWindowsDeviceMenu is the WINDOWS devnode state, which is the
    //     only part that is a real PnP change and the only part that asks for
    //     administrator approval.
    ToolStripMenuItem BuildShowInTrayItem(string pid, string? nameForModal)
    {
        var item = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.ShowInTray))
        {
            Checked = _config.IsDeviceEnabled(pid),
            CheckOnClick = true,
        };
        item.Click += (_, _) =>
        {
            var want = item.Checked;
            _config.SetDeviceEnabled(pid, want);
            Logger.Log($"DEVICE_SHOW_IN_TRAY pid={pid} val={(want ? "true" : "false")}");
            if (!want)
                CloseCriticalAlertFor(nameForModal, "hidden");
            _poller.RefreshNow();
            UpdateTrayIcon();
        };
        return item;
    }

    // The Windows devnode state: stop the device so a Mac can take the
    // Bluetooth link, or start it again. Two commands rather than one
    // checkbox, because a checkbox would have to claim a state this app can
    // only sometimes observe - and a tick that silently means "ask Windows to
    // change something" is what the report above was about.
    ToolStripMenuItem BuildWindowsDeviceMenu(string pid, string? nameForModal)
    {
        var menu = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.WindowsDeviceMenuLabel));

        var start = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.StartInWindows));
        start.Click += (_, _) => ApplyWindowsDeviceState(pid, enable: true, nameForModal);
        menu.DropDownItems.Add(start);

        var stop = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.StopInWindows));
        stop.Click += (_, _) => ApplyWindowsDeviceState(pid, enable: false, nameForModal);
        menu.DropDownItems.Add(stop);

        return menu;
    }

    // Positive evidence, from a read the tray has already done and that needs
    // no elevation: does Windows have a live pointer child for this device
    // right now (DeviceSnapshot.PointerChildLive)? true only when the COL01
    // child devnode resolves as present.
    //
    // null for "no evidence" - no snapshot, or no live BTHENUM instance to ask
    // about - and false is never read as "disabled", because a COL01 key that
    // does not resolve can equally be a broken stack. Only the true case is
    // acted on, which is the only case where skipping is provably right.
    bool? LiveInWindows(string pid)
    {
        var snapshot = FindSnapshot(pid);
        if (snapshot is null || snapshot.BthenumLiveCount == 0)
            return null;
        return snapshot.PointerChildLive;
    }

    // One elevated PnP change, with every outcome DeviceEnable can report
    // said in its own words. Nothing here rolls the tray state back: the tray
    // state is not what this changes.
    void ApplyWindowsDeviceState(string pid, bool enable, string? nameForModal)
    {
        // A device Windows is already driving needs no pnputil run and no UAC
        // prompt at all. This is the 030d case: the mouse was working the
        // whole time, so the honest answer is "nothing to change", not a
        // consent dialog followed by a failure.
        if (enable && LiveInWindows(pid) == true)
        {
            Logger.Log($"DEVICE_WINDOWS_STATE pid={pid} enable=true skipped=already-live");
            System.Windows.Forms.MessageBox.Show(
                TrayMenu.AlreadyStartedInWindows, TrayMenu.ProductName,
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
            return;
        }

        var confirm = System.Windows.Forms.MessageBox.Show(
            enable ? TrayMenu.StartInWindowsPrompt : TrayMenu.StopInWindowsPrompt,
            TrayMenu.ProductName,
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Warning);
        if (confirm != System.Windows.Forms.DialogResult.OK)
            return;

        DeviceEnableResult result;
        try
        {
            result = DeviceEnable.Apply(pid, enable);
        }
        catch (Exception ex)
        {
            // Apply is total for every device outcome, so what is left here is
            // a refusal raised before anything launched (an uncatalogued PID,
            // a guard in BuildScript). Its own words are the honest report.
            Logger.Log($"DEVICE_ENABLE_REFUSED pid={pid} err={ex.Message}");
            System.Windows.Forms.MessageBox.Show(
                ex.Message, TrayMenu.ProductName,
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
            return;
        }

        Logger.Log($"DEVICE_WINDOWS_STATE pid={pid} enable={(enable ? "true" : "false")} "
            + $"outcome={result.Outcome}");
        _poller.RefreshNow();

        switch (result.Outcome)
        {
            case DeviceEnableOutcome.Changed:
            case DeviceEnableOutcome.AlreadyInState:
                // The state the user asked for is the state the device is in.
                // A dialog here would be a nag about a success, and
                // AlreadyInState did not even run pnputil.
                break;

            case DeviceEnableOutcome.NoInstances:
                // Not a failure of this app or of elevation: Windows has no
                // instance to start. The one actionable answer is pairing.
                var pair = System.Windows.Forms.MessageBox.Show(
                    result.Detail
                    + "\n\nIf you removed it in Bluetooth settings, put it in pairing mode "
                    + "(Magic Mouse v1: flip the underside switch off, then on until the LED blinks) "
                    + "and add it again.\n\nOK opens Bluetooth settings.",
                    TrayMenu.ProductName,
                    System.Windows.Forms.MessageBoxButtons.OKCancel,
                    System.Windows.Forms.MessageBoxIcon.Warning);
                if (pair == System.Windows.Forms.DialogResult.OK)
                    BluetoothSettings.OpenDevicesPage();
                break;

            default:
                // UacDeclined and Failed are different facts and DeviceEnable
                // words them differently: only UacDeclined's Detail mentions
                // UAC. Nothing is added here, so a post-elevation failure can
                // never be reported as a cancelled prompt again.
                System.Windows.Forms.MessageBox.Show(
                    result.Detail, TrayMenu.ProductName,
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
                break;
        }

        if (result.Succeeded && !enable)
            CloseCriticalAlertFor(nameForModal, "stopped");
        UpdateTrayIcon();
    }

    // The critical-battery modal names one device. It is closed when that
    // device stops being something this tray can read: hidden from the tray,
    // or stopped in Windows.
    void CloseCriticalAlertFor(string? name, string reason)
    {
        if (string.IsNullOrEmpty(name)
            || _criticalAlert is null
            || !string.Equals(_criticalDevice, name, StringComparison.OrdinalIgnoreCase))
            return;

        _criticalAlert.Close();
        Logger.Log($"CRITICAL_ALERT_CLOSED reason={reason}");
    }


    ToolStripMenuItem BuildUnknownRow(DeviceDriverHealth h)
    {
        var pid = string.IsNullOrEmpty(h.Pid) ? "unknown" : h.Pid.ToUpperInvariant();
        var item = new ToolStripMenuItem(TrayMenu.MenuText($"Unknown Apple mouse (PID {pid})"))
        {
            ForeColor = Color.OrangeRed
        };
        item.AccessibleName = $"Unknown Apple mouse, PID {pid}";
        var warn = new ToolStripMenuItem(TrayMenu.MenuText("Check for an app update")) { ForeColor = Color.OrangeRed };
        warn.Click += (_, _) => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(TrayMenu.ReleasesUrl) { UseShellExecute = true });
        item.DropDownItems.Add(warn);
        item.DropDownItems.Add(BuildShowInTrayItem(h.Pid, null));
        item.DropDownItems.Add(BuildWindowsDeviceMenu(h.Pid, null));
        return item;
    }

    IReadOnlyList<DeviceDriverHealth> ReadHealth() =>
        DriverHealthChecker.GetPerDeviceStatus(_config.Driver0323);

    // Consent first, config second - the one ordering rule for the sticky
    // driver choice.
    //
    // _config.Driver0323 is what DriverHealthChecker.Classify falls back to
    // when the machine itself cannot answer which 0323 driver is in play, so
    // writing it BEFORE the offer meant a cancelled install (the offer shows
    // its own OK/Cancel) or a failed one left the config - and through it the
    // menu - claiming a driver that is not on this PC. Only
    // InstallOutcome.Confirmed, which is "the user accepted AND the elevated
    // step launched", may be remembered. Cancelled, Failed and Unavailable
    // leave the config exactly as it was, the same way the mode flip persists
    // nothing (BuildDeviceRow, flip handler).
    async Task OfferThenRemember(string driver, Func<Task<InstallOutcome>> offer)
    {
        var outcome = await offer();
        if (outcome == InstallOutcome.Confirmed)
        {
            _config.SetDriver0323(driver);
            Logger.Log($"DRIVER_CHOICE_SAVED driver={driver}");
            return;
        }

        Logger.Log($"DRIVER_CHOICE_NOT_SAVED driver={driver} outcome={outcome}");
    }

    // Offer* report their own outcome and explain their own failures
    // (DriverInstaller shows the message box itself), so nothing is toasted
    // here for Cancelled/Failed/Unavailable - that would be a second dialog
    // for one answer. The catch stays as the backstop for the guard refusals
    // Plan*/Execute* still throw.
    void RunDriverAction(Func<InstallOutcome> offer)
    {
        try
        {
            var outcome = offer();
            Logger.Log($"DRIVER_ACTION outcome={outcome}");
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_ACTION_FAIL err={ex.Message}");
            ToastNotifier.ShowError(TrayMenu.ProductName, ex.Message);
        }
        _health = ReadHealth();
        _poller.RefreshNow();
        RefreshFindings();
        UpdateDeviceMenuItems();
    }

    async Task RunDriverActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Logger.Log($"DRIVER_ACTION_FAIL err={ex.Message}");
            ToastNotifier.ShowError(TrayMenu.ProductName, ex.Message);
        }
        _health = ReadHealth();
        _poller.RefreshNow();
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            RefreshFindings();
            UpdateDeviceMenuItems();
        });
    }

    // The advice row: the short form in the menu, the full paragraph on click.
    //
    // DriverAdvisor.AdviceLine is a sourced paragraph - it was written for a
    // dialog - and a paragraph in a ToolStripItem's Text draws one unbroken
    // row across the whole display (this session's screenshots caught it on
    // all three device rows). So the menu shows AdviceShort and the prose is
    // one click away, the same shape the configuration facts use
    // (RefreshConfigSection / ShowConfigFact).
    //
    // AccessibleName carries the full text unclamped: a screen reader reads a
    // sentence out, it does not lay it out in pixels.
    // stock carries what the two registry readers found for a device this app
    // installs nothing for. Default for a mouse (both halves null), which is
    // what makes every mouse row read exactly as it always did: a mouse's
    // driver story is its DriverStatus and its bound filter name.
    ToolStripMenuItem BuildAdviceItem(
        DeviceKind kind, string pid, DriverStatus? status, StockFacts stock)
    {
        var item = new ToolStripMenuItem(TrayMenu.MenuText(
            DriverAdviceView.AdviceShort(kind, pid, status, stock.Driver, stock.Sdp)))
        {
            AccessibleName = DriverAdviceView.AdviceFull(kind, pid, status, stock.Driver, stock.Sdp),
        };
        item.Click += (_, _) => ShowDriverAdvice(kind, pid, status, stock);
        return item;
    }

    // The whole driver story for this device, in the one place wide enough for
    // it. TrayMenu.AdviceDialogText composes it; this method only shows it.
    void ShowDriverAdvice(DeviceKind kind, string pid, DriverStatus? status, StockFacts stock) =>
        System.Windows.Forms.MessageBox.Show(
            TrayMenu.AdviceDialogText(kind, pid, status, stock.Driver, stock.Sdp),
            TrayMenu.ProductName,
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Information);

    // The predicted "what each driver would give" lines, collapsed behind one
    // item on every device row.
    //
    // Never a flat run of lines beside the observed capability rows: those
    // describe this PC right now, and a prediction standing next to them is
    // the one rendering accident DriverAdviceView exists to prevent
    // (DriverAdviceView.cs:11-36). Inline was also what inflated every device
    // row in the screenshots. OptionsHeader stays as the first row INSIDE the
    // submenu: the collapsed label says what the block is, the header says
    // outright that it is a prediction and not a reading.
    static void AddDriverChoices(
        ToolStripItemCollection into, DeviceKind kind, string pid, bool separator)
    {
        if (!DriverAdviceView.ShowOptions(kind, pid))
            return;

        if (separator)
            into.Add(new ToolStripSeparator());

        var choices = new ToolStripMenuItem(TrayMenu.MenuText(TrayMenu.DriverChoicesLabel));
        choices.DropDownItems.Add(
            new ToolStripMenuItem(TrayMenu.MenuText(DriverAdviceView.OptionsHeader())) { Enabled = false });
        foreach (var row in DriverAdviceView.OptionRowsShort(kind, pid))
            choices.DropDownItems.Add(new ToolStripMenuItem(TrayMenu.MenuText(row)) { Enabled = false });
        into.Add(choices);
    }

    /// <summary>
    /// Turns one <see cref="ModeFlipResult"/> into the log line, the battery
    /// display and the dialog. The single reporting path, so no outcome can be
    /// announced twice or swallowed.
    /// </summary>
    /// <remarks>
    /// RunDriverActionAsync only toasts thrown exceptions, and the flip never
    /// throws - it reports refusals and failures as outcomes. A discarded result
    /// is therefore a failure the user would never hear about, which is exactly
    /// what the old queue protocol did.
    /// </remarks>
    void ReportModeFlip(ModeFlipResult result, string? name, DeviceKind kind, string pid)
    {
        Logger.Log($"MODE_FLIP_UI outcome={result.Outcome} percent={result.Percent} "
            + $"restored={result.RestoredToModeB} detail={result.Detail}");

        // Null once the WPF host has shut down: a normal exit race, and the
        // verbatim outcome is already in the log above either way.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        dispatcher.Invoke(() =>
        {
            // A real percent enters by the same door a poll read uses, so the
            // tooltip, the device row and this dialog cannot state different
            // numbers for one reading.
            if (result.Outcome == ModeFlipOutcome.Ok
                && result.Percent >= 0
                && !string.IsNullOrEmpty(name))
                OnBatteryChanged(result.Percent, name, kind, pid);

            // Information only for a verified restore. Everything else leads
            // with a warning because everything else leaves something unproven,
            // and ModeFlipView's text says which.
            var verified = result.Outcome == ModeFlipOutcome.Ok && result.RestoredToModeB;
            System.Windows.Forms.MessageBox.Show(
                ModeFlipView.ResultText(result), TrayMenu.ProductName,
                System.Windows.Forms.MessageBoxButtons.OK,
                verified
                    ? System.Windows.Forms.MessageBoxIcon.Information
                    : System.Windows.Forms.MessageBoxIcon.Warning);
        });
    }

    // Queued, never shown inline: this is called from the constructor, and a
    // modal there would hold the tray icon off the screen until somebody
    // answered it. InvokeAsync from the dispatcher's own thread posts the work
    // and returns, so the ctor finishes and the icon appears first.
    void OfferStaleModeARestore()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        _ = dispatcher.InvokeAsync(() =>
        {
            var answer = System.Windows.Forms.MessageBox.Show(
                ModeFlipView.StartupRestoreOffer(), TrayMenu.ProductName,
                System.Windows.Forms.MessageBoxButtons.OKCancel,
                System.Windows.Forms.MessageBoxIcon.Warning);
            if (answer != System.Windows.Forms.DialogResult.OK)
                return;
            _ = RunDriverActionAsync(async () =>
            {
                // Off the UI thread for the same reason the flip is: the
                // restore waits on an elevated script and then re-reads the
                // end state before it will claim anything.
                var result = await Task.Run(ModeFlip.RestoreModeB);
                ReportModeFlip(result, null, DeviceKind.MagicMouseV3, ModeFlip.V3Pid);
            });
        });
    }

    // A resume rebuilds the Bluetooth link from scratch, and the mouse can come
    // back without its multitouch enable - the same class of failure as the
    // boot race - so the settle sequence runs again. StatusChange is ignored on
    // purpose: it fires on every AC/battery transition, which on a laptop would
    // turn this into a near-constant re-check.
    void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume)
            return;
        BeginHealthSettle("resume");
    }

    /// <summary>
    /// Starts (or restarts) the finite startup/resume re-check sequence:
    /// SettleAttempts refreshes at SettleLead, then SettleStep apart. Safe to
    /// call from any thread.
    /// </summary>
    void BeginHealthSettle(string trigger)
    {
        lock (_settleLock)
        {
            if (_settleStopped)
                return;
            // Coalesce rather than queue: a resume landing mid-startup-settle,
            // or two resumes in quick succession, reset the one timer instead
            // of stacking a second sequence on top of the first. The device
            // state the later trigger cares about is the only one worth
            // sampling, and the gate gets its spaced pair either way.
            _settleTrigger = trigger;
            _settleAttempt = 0;
            _settleTimer ??= new System.Threading.Timer(
                OnHealthSettleTick, null,
                System.Threading.Timeout.InfiniteTimeSpan,
                System.Threading.Timeout.InfiniteTimeSpan);
            _settleTimer.Change(SettleLead, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    // One attempt. The timer is re-armed by hand only while attempts remain, so
    // the sequence ends on its own with no recurring tick left behind.
    void OnHealthSettleTick(object? state)
    {
        int attempt;
        string trigger;
        lock (_settleLock)
        {
            if (_settleStopped || _settleTimer is null)
                return;
            attempt = ++_settleAttempt;
            trigger = _settleTrigger;
            if (attempt < SettleAttempts)
                _settleTimer.Change(SettleStep, System.Threading.Timeout.InfiniteTimeSpan);
        }

        // One line per attempt, so the next reboot is a verifiable regression
        // check from debug.log alone: three HEALTH_SETTLE lines at roughly
        // +8 s, +25 s and +42 s, spread either side of the startup
        // REPAIR_FINDINGS burst instead of inside its single second.
        Logger.Log($"HEALTH_SETTLE trigger={trigger} attempt={attempt}/{SettleAttempts}");

        // RefreshFindings repaints WinForms menu state, and this callback is on
        // a thread pool thread, so it is marshalled exactly as OnBatteryChanged
        // does. Current is null once the WPF host has shut down - that is a
        // normal exit race here, not an error.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        try
        {
            dispatcher.Invoke(() =>
            {
                // Dispose can land between releasing the lock and this
                // callback reaching the UI thread, and by then the menu and the
                // tray icon are gone.
                if (_settleStopped)
                    return;
                RefreshFindings();
                // Without this, a fault the settle sequence just confirmed
                // would sit in the menu row unseen until the user opened the
                // menu: NotifyNewFindings used to be reachable only from the
                // poller tick, and that poller has been observed scheduling a
                // full day out. _toastedFindings is keyed {Pid}:{Problem} and
                // is never cleared, so the extra attempts here cannot re-toast
                // a pair the poller (or an earlier attempt) already announced.
                NotifyNewFindings();
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"HEALTH_SETTLE_FAIL trigger={trigger} err={ex.Message}");
        }
    }

    /// <summary>
    /// Recomputes the repair findings from a fresh device snapshot, confirms them
    /// through the persistence gate and repaints the top-of-menu entry. Never
    /// throws: a snapshot failure must not break the menu.
    /// </summary>
    void RefreshFindings()
    {
        IReadOnlyList<RepairFinding> raw;
        IReadOnlyList<DeviceSnapshot> snapshots;
        try
        {
            // The reader cannot read a battery itself - the percent arrives as a
            // pushed HID input report the poller owns - so the tray hands it the
            // sentinels it already has.
            snapshots = DeviceSnapshotReader.Read(_config, _lastBatteryByPid);
            raw = RepairPlanner.Plan(snapshots);
        }
        catch (Exception ex)
        {
            // A read that threw learned NOTHING. Feeding the gate an empty list
            // here would make the failure itself an observation of health: it
            // would forget a confirmed fault's credit and blank the device rows'
            // capability facts, i.e. answer "no problems" from no evidence. The
            // last known state stands until a read succeeds, and this line is
            // how that is audited.
            Logger.Log($"REPAIR_FINDINGS_FAIL err={ex.Message}");
            return;
        }

        // Nothing reaches _findings - and so nothing reaches the menu row, the
        // device sub-rows, the toasts or the guided dialogs - until the gate has
        // seen the same (Pid, Problem) pair at least FindingGate.MinObservations
        // times spanning FindingGate.HoldWindow. A driver install walks the stack
        // through several of the planner's fault states for a few seconds each;
        // this is what stops those from toasting and offering an elevated
        // restart-device that would fight the install (FindingGate.cs).
        var gated = _findingGate.Confirm(raw, DateTime.UtcNow);
        _findings = gated.Confirmed;
        _pendingFindings = gated.Pending;
        _snapshots = snapshots;

        // Started here, exactly where the synchronous read stood, so the
        // refresh cadence is unchanged - but the reading itself runs on the
        // thread pool and publishes back. A read that fails leaves the
        // previous configuration facts standing rather than answering "all
        // checks passed" from no evidence, the same rule the early return
        // above follows for a failed snapshot read.
        BeginConfigFactsRead();

        try
        {
            if (_repairItem != null)
            {
                _repairItem.Text = RepairPlanner.MenuLabel(HeadlineFindings());
                _repairItem.ForeColor = HeadlineFindings().Count > 0
                    ? Color.OrangeRed
                    : SystemColors.ControlText;
            }

            RefreshConfigSection();
        }
        catch (Exception ex)
        {
            Logger.Log($"REPAIR_MENU_FAIL err={ex.Message}");
        }

        // raw vs confirmed vs pending, plus WHICH pairs are being held, so a
        // suppressed transient is visible in debug.log instead of silently
        // swallowed. This replaces the old "count=" field: count was the number
        // of findings the menu shows, which is exactly confirmed= now.
        Logger.Log($"REPAIR_FINDINGS raw={raw.Count} confirmed={_findings.Count} "
            + $"pending={_pendingFindings.Count}{HeldSuffix(_pendingFindings)}");
    }

    /// <summary>
    /// Starts the per-device <see cref="SystemConfigChecker"/> read on the
    /// thread pool and marshals the finished list back for rendering.
    /// </summary>
    /// <remarks>
    /// Check is the one reader on this refresh that can start a process: it
    /// may reach a single <c>bcdedit /enum {current}</c> READ when the
    /// registry cannot answer the signing question, and its own header says
    /// so and says it belongs on a background read and never on a UI thread
    /// (SystemConfigChecker.cs:21-23, 145-151). The device targets are
    /// UI-owned state, so they are sampled on the calling thread and only the
    /// finished facts cross back.
    /// </remarks>
    void BeginConfigFactsRead()
    {
        // One read in flight at a time. Refreshes can arrive faster than a
        // bcdedit read returns (poller tick, settle attempt, driver action),
        // and queueing them would pile identical readings onto the thread
        // pool; the read already running answers the same question. This is a
        // guard against overlap, not a cadence: nothing is delayed or timed.
        if (System.Threading.Interlocked.CompareExchange(ref _configReadInFlight, 1, 0) != 0)
            return;

        // Sampled here, on the thread that owns the state, never inside the
        // task. A throw here would otherwise strand the flag and stop every
        // later read.
        List<(DeviceKind Kind, string Pid, DriverStatus? Status)> targets;
        try
        {
            targets = ConfigFactTargets();
        }
        catch (Exception ex)
        {
            System.Threading.Interlocked.Exchange(ref _configReadInFlight, 0);
            Logger.Log($"CONFIG_FACTS_FAIL err={ex.Message}");
            return;
        }

        _ = Task.Run(() =>
        {
            IReadOnlyList<ConfigFact>? facts = null;
            IReadOnlyDictionary<string, StockFacts>? stock = null;
            try
            {
                try
                {
                    facts = ReadConfigFacts(targets);
                }
                catch (Exception ex)
                {
                    // Learned nothing: keep what the menu already shows.
                    Logger.Log($"CONFIG_FACTS_FAIL err={ex.Message}");
                }

                // A second registry read for the same targets, on the same
                // trip: the driver Windows actually has on the non-mouse
                // devices, and the SDP patch state behind a keyboard's
                // percent. Its own catch, because one reader failing must not
                // cost the menu the other reader's evidence. Read-only and
                // unelevated - but an HKLM walk belongs off the UI thread just
                // as the bcdedit read above does.
                try
                {
                    stock = ReadStockFacts(targets);
                }
                catch (Exception ex)
                {
                    Logger.Log($"STOCK_FACTS_FAIL err={ex.Message}");
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _configReadInFlight, 0);
            }

            if (facts is not null || stock is not null)
                PublishConfigFacts(facts, stock);
        });
    }

    // The (kind, pid, status) triples Check needs, sampled on the thread that
    // owns them: _deviceBatteries and _health are written on the UI thread, so
    // the background read is handed a snapshot instead of reading them live.
    List<(DeviceKind Kind, string Pid, DriverStatus? Status)> ConfigFactTargets()
    {
        var targets = new List<(DeviceKind, string, DriverStatus?)>(4);
        foreach (var (kind, pid) in ConfigDeviceTargets())
            targets.Add((kind, pid, FindHealth(pid)?.Status));
        return targets;
    }

    /// <summary>
    /// What SystemConfigChecker says about this PC for the devices the menu is
    /// showing rows for. Null means the read learned nothing at all and the
    /// caller must keep the facts it already has.
    /// </summary>
    /// <remarks>
    /// Check takes a per-device triple and the folding rule is
    /// TrayMenu.MergeConfigFacts: machine-wide questions (Test Mode, Memory
    /// integrity, the multitouch watcher) collapse onto one row with the more
    /// severe reading winning, while the per-device driver package fact keeps
    /// a row per distinct package state so one device's state is never
    /// reported as another's.
    /// </remarks>
    static IReadOnlyList<ConfigFact>? ReadConfigFacts(
        IReadOnlyList<(DeviceKind Kind, string Pid, DriverStatus? Status)> targets)
    {
        var perDevice = new List<IReadOnlyList<ConfigFact>>(targets.Count);
        var failed = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            try
            {
                perDevice.Add(SystemConfigChecker.Check(target.Kind, target.Pid, target.Status));
            }
            catch (Exception ex)
            {
                // One unreadable device must not blank the others.
                Logger.Log($"CONFIG_FACTS_FAIL pid={target.Pid} err={ex.Message}");
                failed++;
            }
        }

        // Every device unreadable is the same as a snapshot read that threw:
        // no evidence at all, so the last known facts stand.
        if (failed > 0 && perDevice.Count == 0)
            return null;

        return TrayMenu.MergeConfigFacts(perDevice);
    }

    /// <summary>
    /// Which driver Windows actually has on each non-mouse device, and - for a
    /// Magic Keyboard only - whether this repo's SDP patch is in its Bluetooth
    /// record. Null means nothing at all was readable, so the caller keeps
    /// what it has.
    /// </summary>
    /// <remarks>
    /// Only the devices with no Driver submenu are read
    /// (TrayMenu.ShowsStockDriverStory): a Magic Mouse has DriverStatus and a
    /// bound filter name of its own, and reading its BTHENUM nodes here would
    /// be a second, competing answer to a question already answered.
    ///
    /// Measured on the reference PC, 2026-09-16: keyboard 0239 is entirely
    /// Microsoft's stack - hidbth.inf on the BTHENUM parent, kbdhid via
    /// keyboard.inf on COL01, hidserv.inf on COL02/COL03, every node Status
    /// OK, Lower/UpperFilters empty - and its percent only exists because the
    /// SDP patch put a battery Feature cap on COL02. Neither fact is a
    /// DriverStatus, which is why neither goes near the mouse-shaped enum.
    ///
    /// Both readers are contracted never to throw, and this still catches per
    /// device: one unreadable device must not cost the others their reading.
    /// </remarks>
    static IReadOnlyDictionary<string, StockFacts>? ReadStockFacts(
        IReadOnlyList<(DeviceKind Kind, string Pid, DriverStatus? Status)> targets)
    {
        Dictionary<string, StockFacts>? read = null;
        var failed = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (!TrayMenu.ShowsStockDriverStory(target.Kind, target.Pid))
                continue;
            try
            {
                // The SDP patch is a keyboard-only story: a trackpad reads its
                // percent from a HID Input report with nothing installed, so
                // asking about its Bluetooth SDP record would answer a
                // question nothing about it depends on.
                var sdp = target.Kind == DeviceKind.MagicKeyboard
                    ? SdpPatchReader.ForPid(target.Pid)
                    : (SdpPatchState?)null;
                read ??= new Dictionary<string, StockFacts>(StringComparer.OrdinalIgnoreCase);
                read[target.Pid] = new StockFacts(StockDriverReader.ForPid(target.Pid), sdp);
            }
            catch (Exception ex)
            {
                Logger.Log($"STOCK_FACTS_FAIL pid={target.Pid} err={ex.Message}");
                failed++;
            }
        }

        // No non-mouse device on this PC is not a failed read: it is a PC with
        // nothing to say here, and publishing the empty map is what clears a
        // stale entry for a device that has since gone away.
        if (failed > 0 && read is null)
            return null;
        return read ?? EmptyStockFacts;
    }

    // Back to the UI thread, marshalled exactly as OnBatteryChanged and the
    // settle tick are: this runs on a thread pool thread and
    // RefreshConfigSection repaints WinForms menu state. Current is null once
    // the WPF host has shut down, and _settleStopped is the same
    // already-disposed guard the settle tick uses after crossing threads -
    // both are normal exit races, not errors.
    //
    // Both readings cross together because they are read together: the
    // configuration section and the device rows are repainted from the one
    // trip the refresh already pays for.
    void PublishConfigFacts(
        IReadOnlyList<ConfigFact>? facts, IReadOnlyDictionary<string, StockFacts>? stock)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        try
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                if (_settleStopped)
                    return;
                // Null from either reader is no evidence, so that half keeps
                // the value it had rather than being blanked by a failed read.
                if (facts is not null)
                    _configFacts = facts;
                var stockChanged = stock is not null && !SameStockFacts(_stockFacts, stock);
                if (stock is not null)
                    _stockFacts = stock;
                try
                {
                    RefreshConfigSection();
                    // The device rows carry the driver-in-use line and the
                    // battery's source, so a changed reading has to reach
                    // them. Rebuilt only when the reading actually moved:
                    // UpdateTrayIcon already rebuilds these rows on every
                    // poller tick, and repainting them for an identical read
                    // would be work for no change on screen.
                    if (stockChanged)
                        UpdateDeviceMenuItems();
                }
                catch (Exception ex)
                {
                    Logger.Log($"CONFIG_FACTS_MENU_FAIL err={ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"CONFIG_FACTS_PUBLISH_FAIL err={ex.Message}");
        }
    }

    // Whether two readings say the same thing. StockDriverInfo is a record
    // whose Stack is a string[], so record equality compares that array by
    // REFERENCE: every read allocates a fresh one and would always compare
    // unequal, which would rebuild every device row on every refresh forever.
    // Element-wise is the only comparison that answers the question asked.
    static bool SameStockFacts(
        IReadOnlyDictionary<string, StockFacts> a, IReadOnlyDictionary<string, StockFacts> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var (pid, left) in a)
        {
            if (!b.TryGetValue(pid, out var right))
                return false;
            if (left.Sdp != right.Sdp || !SameStockDriver(left.Driver, right.Driver))
                return false;
        }
        return true;
    }

    static bool SameStockDriver(StockDriverInfo? a, StockDriverInfo? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a.Service != b.Service || a.InfPath != b.InfPath || a.Provider != b.Provider
            || a.Version != b.Version || a.AllNodesOk != b.AllNodesOk
            || a.Stack.Length != b.Stack.Length)
            return false;
        for (var i = 0; i < a.Stack.Length; i++)
        {
            if (!string.Equals(a.Stack[i], b.Stack[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    // Every (kind, pid) the tray has a device row or a driver status for. Health
    // is walked as well as the battery map because the v3 driver status is known
    // before the poller has ever reported a percent for it, which is the same
    // pairing UpdateBatteryReadsVisibility relies on.
    IEnumerable<(DeviceKind Kind, string Pid)> ConfigDeviceTargets()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in _deviceBatteries.Values)
        {
            if (!string.IsNullOrEmpty(v.Pid) && seen.Add(v.Pid))
                yield return (v.Kind, v.Pid);
        }
        foreach (var h in _health)
        {
            if (string.IsNullOrEmpty(h.Pid) || !seen.Add(h.Pid))
                continue;
            // An unknown PID gets no kind, so it gets no configuration claim
            // either - inventing a kind here would invent the facts with it.
            if (MouseBatteryDevice.TryKnownMouse(h.Pid, out _, out var kind))
                yield return (kind, h.Pid);
        }
    }

    // The collapsed section plus one clickable row per fact. Rebuilt wholesale
    // on every refresh, the same way the device rows are, so there is no stale
    // row to invalidate.
    void RefreshConfigSection()
    {
        if (_configItem is null)
            return;

        var label = ConfigFactView.SectionLabel(_configFacts);
        _configItem.DropDownItems.Clear();
        if (label is null)
        {
            // No fact at all means nothing about this PC was checked. A row
            // saying "checked" would claim a verification that never ran.
            _configItem.Visible = false;
            return;
        }

        _configItem.Text = label;
        _configItem.ForeColor = TrayMenu.ConfigSectionIsFault(_configFacts)
            ? Color.OrangeRed
            : SystemColors.ControlText;
        foreach (var fact in ConfigFactView.Ordered(_configFacts))
        {
            var scoped = fact;
            var row = new ToolStripMenuItem(TrayMenu.MenuText(ConfigFactView.Row(scoped)));
            row.Click += (_, _) => ShowConfigFact(scoped);
            _configItem.DropDownItems.Add(row);
        }
        _configItem.Visible = true;
    }

    // The checker's own prose, then the one thing this tray is allowed to do
    // about it: open a page. A ConfigFact may name a script that ships in a
    // driver package, and running one from here is exactly what
    // SystemConfigChecker's read-only contract forbids, so only an http(s)
    // target gets the OK/Cancel pair that promises to open something.
    void ShowConfigFact(ConfigFact fact)
    {
        var openable = !string.IsNullOrEmpty(fact.ActionLabel)
            && TrayMenu.IsHelpUrl(fact.ActionUrlOrScript);
        var answer = System.Windows.Forms.MessageBox.Show(
            ConfigFactView.DialogText(fact), TrayMenu.ProductName,
            openable
                ? System.Windows.Forms.MessageBoxButtons.OKCancel
                : System.Windows.Forms.MessageBoxButtons.OK,
            fact.Severity == ConfigSeverity.Blocking
                ? System.Windows.Forms.MessageBoxIcon.Warning
                : System.Windows.Forms.MessageBoxIcon.Information);
        if (openable && answer == System.Windows.Forms.DialogResult.OK)
            OpenHelpUrl(fact.ActionUrlOrScript!);
    }

    // "held=0323:FilterNotInStack,0323:ConflictingFilters" for the pending pairs,
    // empty when nothing is held. Only allocates on the suppression path.
    static string HeldSuffix(IReadOnlyList<RepairFinding> pending)
    {
        if (pending.Count == 0)
            return string.Empty;
        var sb = new System.Text.StringBuilder(" held=");
        for (var i = 0; i < pending.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(pending[i].Pid).Append(':').Append(pending[i].Problem.ToString());
        }
        return sb.ToString();
    }

    RepairFinding? FindFinding(string pid)
    {
        // HeadlineFindings, not _findings: a measured scroll verdict has to
        // reach the device row it is about, not only the top-of-menu row.
        foreach (var f in HeadlineFindings())
        {
            if (TrayMenu.PidEq(f.Pid, pid))
                return f;
        }
        return null;
    }

    DeviceSnapshot? FindSnapshot(string pid)
    {
        foreach (var s in _snapshots)
        {
            if (TrayMenu.PidEq(s.Pid, pid))
                return s;
        }
        return null;
    }

    // What the last published background read said about this device. Default
    // - both halves null - for a mouse, for a device the readers could not
    // read, and before the first read has published. All three are "not
    // read", which every row renders as unknown and none as a fault.
    StockFacts FindStockFacts(string pid) =>
        _stockFacts.TryGetValue(pid, out var facts) ? facts : default;

    // Everything the capability lines need for one device row. A missing
    // snapshot leaves every stack fact null, which DeviceCapability renders as
    // unknown rather than as a fault.
    //
    // sdp is the caller's, not read here: the row has already sampled it
    // alongside the driver it names, and reading it twice could show a battery
    // line and a driver line disagreeing about the same registry value.
    DeviceCapability.CapabilityFacts FactsFor(
        string pid, int pct, DeviceKind kind, SdpPatchState? sdp)
    {
        var snapshot = FindSnapshot(pid);
        return new DeviceCapability.CapabilityFacts(
            Kind: kind,
            Pid: pid,
            LastPct: pct,
            BoundFilter: snapshot?.BoundFilterName,
            FilterPackagePresent: snapshot?.FilterPackagePresent,
            FilterServiceRunning: snapshot?.FilterServiceRunning,
            FilterInStack: snapshot?.FilterInStack,
            PointerChildLive: snapshot?.PointerChildLive,
            MultitouchAdvancing: snapshot?.MultitouchAdvancing,
            Problem: FindFinding(pid)?.Problem,
            EnabledInApp: EnabledInApp(pid),
            Sdp: sdp,
            // The passive observation, so the scroll line cannot claim
            // "working" off counter movement while a measured window on this
            // very device saw no notch reach Windows.
            Wheel: snapshot?.Wheel);
    }

    // Tri-state, deliberately: true on, false switched off in this app, null
    // never toggled for this PID. Same convention as
    // DeviceSnapshot.ConfigEnabled - "no entry" is not the same statement as
    // "the user turned it on", and only false licenses the capability rows to
    // explain a missing reading by this app's own choice.
    bool? EnabledInApp(string pid) =>
        _config.HasDeviceEnabledEntry(pid) ? _config.IsDeviceEnabled(pid) : null;

    /// <summary>
    /// One toast per (Pid, Problem) per process lifetime, so a standing problem never
    /// nags. UsbPhantomsOnly is informational and never toasts.
    ///
    /// _toastedFindings is never cleared, and that is what stops a guidance-only
    /// finding from toasting on a loop: it is not auto-fixed, so it is present on
    /// every poll tick, and only the first tick may toast it. A capability with no
    /// evidence produces no finding at all (RepairPlanner never faults on null),
    /// so unknown never toasts.
    /// </summary>
    void NotifyNewFindings()
    {
        foreach (var f in _findings)
        {
            if (f.Problem == RepairProblem.UsbPhantomsOnly)
                continue;
            if (!_toastedFindings.Add($"{f.Pid}:{f.Problem}"))
                continue;
            Logger.Log($"REPAIR_TOAST pid={f.Pid} problem={f.Problem}");
            try
            {
                ToastNotifier.Show(TrayMenu.ProductName,
                    $"{f.Title}\nOpen the Magic Tray menu to fix this.");
            }
            catch (Exception ex)
            {
                Logger.Log($"REPAIR_TOAST_FAIL pid={f.Pid} err={ex.Message}");
            }
        }
    }

    /// <summary>
    /// Guided repair over every current finding. Each finding is explained in plain
    /// language before anything is changed; Cancel stops the walk.
    ///
    /// This is the answer to a direct user question - the top-of-menu row and
    /// "Check for problems now" both land here - so it must never say "No problems
    /// found" while the gate is still holding one. It must not bypass the gate
    /// either: an unconfirmed fault is offered a repair that elevates and runs
    /// pnputil /restart-device, and during a driver install that fights the
    /// install. Asking does not make a 900 ms fault real. So the honest answer is
    /// the third one: name what is being checked and when it will be decided.
    /// </summary>
    void ShowRepairFlow()
    {
        if (HeadlineFindings().Count == 0)
        {
            if (_pendingFindings.Count > 0)
            {
                var held = _pendingFindings[0];
                var more = _pendingFindings.Count > 1
                    ? $" (and {_pendingFindings.Count - 1} more)"
                    : string.Empty;
                Logger.Log($"REPAIR_FLOW_PENDING pending={_pendingFindings.Count} "
                    + $"pid={held.Pid} problem={held.Problem}");
                ToastNotifier.Show(TrayMenu.ProductName,
                    $"Checking a possible problem: {held.Title}{more}.\n"
                    + "It has not been seen for long enough to be sure it is real - "
                    + "installing or restarting a driver makes a healthy mouse look faulty "
                    + "for a few seconds. Open this menu again in about "
                    + $"{(int)FindingGate.HoldWindow.TotalSeconds} seconds and it will be "
                    + "reported if it is still there.");
                return;
            }

            // ONE source of truth with the row above. This used to be the
            // literal "No problems found." - a second, independently written
            // string that could agree with the menu row only by coincidence,
            // and a cheerier claim than the row was entitled to make. The row
            // is RepairPlanner.MenuLabel(HeadlineFindings()) (:834, refreshed
            // :2331); the toast is now that same call on that same list.
            ToastNotifier.Show(TrayMenu.ProductName,
                RepairPlanner.MenuLabel(HeadlineFindings()));

            // ...and the label above is about the DRIVER and the CONNECTION. It
            // has never been able to see the scroll wheel, so where nothing
            // explains a dead wheel, the honest next move is to measure it with
            // the user's help rather than let "No problems found" stand as an
            // answer about scrolling.
            OfferScrollProbe();
            return;
        }

        // The same list the row and the toast are built from, so a measured
        // scroll verdict is walked here too instead of being announced by a
        // headline that leads nowhere.
        foreach (var finding in HeadlineFindings().ToArray())
        {
            if (!HandleFinding(finding))
                break;
        }
    }

    // Same guided flow, scoped to the one finding shown on a device row.
    void ShowRepairFlow(RepairFinding finding) => _ = HandleFinding(finding);

    // The headline's single source of truth, shared by the menu row (:834,
    // refreshed :2331) and by the toast that the same click produces (:2849):
    // the planner's gated findings PLUS the verdict of the user-initiated
    // scroll probe.
    //
    // The probe result belongs here and not in _findings because it is not a
    // gated observation - FindingGate confirms a fault by seeing it repeatedly
    // (FindingGate.cs:11-18), and a probe the user performed once is already
    // as confirmed as it will ever get. What it must NOT be is invisible: the
    // label reads driver and connection state only, so without this a measured
    // dead wheel would sit behind a row saying "No problems found".
    IReadOnlyList<RepairFinding> HeadlineFindings()
    {
        if (_scrollProbeFinding is null)
            return _findings;
        var combined = new List<RepairFinding>(_findings.Count + 1);
        combined.AddRange(_findings);
        combined.Add(_scrollProbeFinding);
        return combined;
    }

    // What the last prompted scroll probe measured, or null when none has ever
    // produced a verdict. Set only by a probe whose control leg carried notches
    // and whose discriminator leg carried none; cleared only by a later probe
    // that saw notches on the discriminator. An INCONCLUSIVE probe changes
    // nothing in either direction - that is the whole meaning of inconclusive.
    RepairFinding? _scrollProbeFinding;

    // One sink for the process, so the decoder proof is not thrown away between
    // probes: DecoderValidated can only be set by decoding a real click, it is
    // sticky per device path inside the sink, and a fresh sink per probe would
    // force the user to click every single time.
    IWheelSink? _wheelSink;
    // Whether any observation this process has taken proved the decoder. Drives
    // the click-first wording, nothing else.
    bool _scrollDecoderValidated;

    // Offered only where the driver state leaves a dead wheel UNEXPLAINED: a
    // live v3 whose filter is bound, running and actually in the device stack.
    // Every other shape is one of the automatic rules above, and those name a
    // cause and offer a real fix - asking the user to perform a 20-second
    // gesture test to rediscover a stopped service would be a waste of their
    // time.
    DeviceSnapshot? ScrollProbeCandidate()
    {
        foreach (var s in _snapshots)
        {
            if (!DriverHealthChecker.IsV3Pid(s.Pid))
                continue;
            if (s.BthenumLiveCount <= 0 || string.IsNullOrEmpty(s.BoundFilterName))
                continue;
            if (!s.FilterServiceRunning || s.FilterInStack != true)
                continue;
            return s;
        }
        return null;
    }

    void OfferScrollProbe()
    {
        // A verdict already measured is reported instead of re-measured.
        if (_scrollProbeFinding is not null)
        {
            _ = HandleFinding(_scrollProbeFinding);
            return;
        }

        var snapshot = ScrollProbeCandidate();
        if (snapshot is null)
            return;

        var intro =
            "No problem was found with this mouse's driver or its connection - and that is not "
            + "an answer about the scroll wheel.\n\n"
            + "This PC cannot tell a mouse whose scrolling is broken from a mouse nobody has "
            + "scrolled on: both look exactly the same from here. A hand resting on the top "
            + "surface sends the same stream of touch reports as a hand scrolling, and a "
            + "working mouse spends most of its time sending no scroll at all. So the only "
            + "honest way to check is to measure two short gestures while you make them.\n\n"
            + "It takes about twenty seconds, in two steps of ten. Nothing is installed, "
            + "nothing is changed on this PC or on the mouse, no administrator prompt appears, "
            + "and you can stop at any step.\n\n"
            + "OK starts the test. Cancel changes nothing.";
        if (System.Windows.Forms.MessageBox.Show(
                intro, TrayMenu.ProductName,
                System.Windows.Forms.MessageBoxButtons.OKCancel,
                System.Windows.Forms.MessageBoxIcon.Information)
            != System.Windows.Forms.DialogResult.OK)
        {
            Logger.Log($"SCROLL_PROBE_DECLINED pid={snapshot.Pid}");
            return;
        }

        StartScrollProbe(snapshot);
    }

    // Measurement runs off the dispatcher - two ten-second windows plus dialogs
    // would otherwise freeze the tray - and every dialog is marshalled back,
    // with the same dispatcher-is-gone early-out the repair apply path uses
    // (:3050-3055). A probe that resolves after the UI has gone logs and drops.
    void StartScrollProbe(DeviceSnapshot snapshot)
    {
        var service = RepairPlanner.FilterServiceFor(snapshot);
        _wheelSink ??= new RawInputWheelSink(
            new RawInputMouseSource(),
            () => DeviceDiagReader.MultitouchAdvancing(service));
        var sink = _wheelSink;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunScrollProbeAsync(snapshot, sink).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Log($"SCROLL_PROBE_FAIL pid={snapshot.Pid} err={ex.Message}");
            }
        });
    }

    async Task RunScrollProbeAsync(DeviceSnapshot snapshot, IWheelSink sink)
    {
        // Leg 1, the positive control, FIRST: it is the leg most likely to
        // succeed, so a user who abandons the flow has still given us the
        // control rather than the half that cannot be interpreted alone.
        if (!AskOnUi(RepairPlanner.ScrollProbeControlPrompt(_scrollDecoderValidated)))
            return;
        var control = await sink.ObservePromptedAsync(
            RepairPlanner.ScrollProbeLeg, CancellationToken.None).ConfigureAwait(false);
        _scrollDecoderValidated |= control.DecoderValidated;

        // Exactly one retry, and it REPLACES the control observation rather
        // than adding to it: accumulating windows is the passive design that
        // cannot tell a healthy mouse nobody scrolled on from a broken one.
        if (control.WheelEvents == 0 && control.HWheelEvents == 0)
        {
            if (!AskOnUi(RepairPlanner.ScrollProbeControlRetryPrompt))
                return;
            control = await sink.ObservePromptedAsync(
                RepairPlanner.ScrollProbeLeg, CancellationToken.None).ConfigureAwait(false);
            _scrollDecoderValidated |= control.DecoderValidated;
        }

        if (!AskOnUi(RepairPlanner.ScrollProbeDiscriminatorPrompt))
            return;
        var discriminator = await sink.ObservePromptedAsync(
            RepairPlanner.ScrollProbeLeg, CancellationToken.None).ConfigureAwait(false);
        _scrollDecoderValidated |= discriminator.DecoderValidated;

        var verdict = RepairPlanner.JudgeScrollProbe(control, discriminator);
        Logger.Log($"SCROLL_PROBE pid={snapshot.Pid} verdict={verdict} "
            + $"control_wheel={control.WheelEvents}+{control.HWheelEvents} "
            + $"control_void={control.Void} "
            + $"probe_wheel={discriminator.WheelEvents}+{discriminator.HWheelEvents} "
            + $"probe_records={discriminator.MouseRecords} probe_void={discriminator.Void} "
            + $"decoder={discriminator.DecoderValidated} "
            + $"target={discriminator.TargetDevicePath ?? "none"}");

        var finding = RepairPlanner.PlanScrollProbe(snapshot, control, discriminator);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Logger.Log($"SCROLL_PROBE_RESULT pid={snapshot.Pid} verdict={verdict} ui=gone");
            return;
        }
        dispatcher.Invoke(() => OnScrollProbeFinished(verdict, finding));
    }

    void OnScrollProbeFinished(
        RepairPlanner.ScrollProbeVerdict verdict, RepairFinding? finding)
    {
        switch (verdict)
        {
            case RepairPlanner.ScrollProbeVerdict.NotchesMissing when finding is not null:
                // Recorded BEFORE it is shown, so the menu row and the icon
                // stop saying "No problems found" from this moment on.
                _scrollProbeFinding = finding;
                UpdateDeviceMenuItems();
                _ = HandleFinding(finding);
                break;
            case RepairPlanner.ScrollProbeVerdict.NotchesDelivered:
                // Notches got through, so a standing verdict from an earlier
                // probe is no longer supported by the evidence and is dropped.
                // The wording still refuses to call scrolling healthy.
                _scrollProbeFinding = null;
                UpdateDeviceMenuItems();
                TellOnUi(RepairPlanner.ScrollProbeNoFaultFound);
                break;
            default:
                TellOnUi(RepairPlanner.ScrollProbeInconclusive);
                break;
        }
    }

    // OKCancel on the UI thread from a background step. false for Cancel AND
    // for a dispatcher that has gone away, which ends the probe without a
    // verdict - the same "no evidence, no claim" default the planner uses.
    bool AskOnUi(string body)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Logger.Log("SCROLL_PROBE_PROMPT ui=gone");
            return false;
        }
        return dispatcher.Invoke(() => System.Windows.Forms.MessageBox.Show(
                body + "\n\nOK starts the ten seconds. Cancel stops the test.",
                TrayMenu.ProductName,
                System.Windows.Forms.MessageBoxButtons.OKCancel,
                System.Windows.Forms.MessageBoxIcon.Information)
            == System.Windows.Forms.DialogResult.OK);
    }

    void TellOnUi(string body)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Logger.Log("SCROLL_PROBE_REPORT ui=gone");
            return;
        }
        dispatcher.Invoke(() => System.Windows.Forms.MessageBox.Show(
            body, TrayMenu.ProductName,
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Information));
    }

    // Returns true when the finding was handled and the walk may continue.
    bool HandleFinding(RepairFinding finding)
    {
        // Two rival vendor filters need the stale and the kept name in the dialog, so this
        // finding reads the live stack first and states both before anything is changed.
        if (finding.AutoFixable && finding.Action == RepairAction.RemoveStaleFilter)
            return HandleConflictingFilters(finding);

        // Guidance only. Nothing here is elevated, nothing is written to the mouse, and
        // the multitouch enable Feature report is never sent: the mouse driver package
        // owns that, and a second implementation in Magic Tray is an explicit non-goal
        // (docs/ENABLE-DISABLE.md:93-97). PlanOne does not raise this action today - the
        // symptom is user-asserted from the device row - but a finding carrying it must
        // still reach real help instead of falling through the switch below.
        if (finding.Action == RepairAction.RecommendMultitouchWatcher)
        {
            ShowScrollGuidance(finding.Pid);
            AfterFindingHandled();
            return true;
        }

        var body = $"{finding.Title}\n\n{finding.Detail}\n\n{ConsequenceText(finding)}";
        var answer = System.Windows.Forms.MessageBox.Show(
            body, TrayMenu.ProductName,
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Warning);
        if (answer != System.Windows.Forms.DialogResult.OK)
        {
            Logger.Log($"REPAIR_FLOW_CANCEL pid={finding.Pid} problem={finding.Problem}");
            return false;
        }

        Logger.Log($"REPAIR_FLOW_OK pid={finding.Pid} problem={finding.Problem} action={finding.Action}");

        if (finding.AutoFixable && finding.Action == RepairAction.RestartBtHidParent)
        {
            // Elevated restart plus the sidecar poll can take minutes: never on the UI thread.
            StartRepairApply(finding.Pid, finding.Problem);
            return false;
        }

        switch (finding.Action)
        {
            case RepairAction.EnableInApp when finding.AutoFixable:
                EnableDeviceInApp(finding.Pid);
                break;
            case RepairAction.PairInWindows:
                BluetoothSettings.OpenDevicesPage();
                break;
            case RepairAction.InstallDriver:
                OfferDriverForFinding(finding);
                break;
        }

        AfterFindingHandled();
        return true;
    }

    // What OK actually does, appended to every guided dialog.
    static string ConsequenceText(RepairFinding finding)
    {
        // FilterNotInStack is the one restart finding where the scroll driver service is
        // already RUNNING: the fault is that Windows rebuilt this mouse's device stack
        // without the filter attached to it (docs/v3.html:149-157). Telling the user that OK
        // "starts the scroll driver service" would contradict the finding's own evidence and
        // leave them looking for a stopped service.
        if (finding.Problem == RepairProblem.FilterNotInStack)
        {
            return "OK rebuilds this mouse's Bluetooth connection so Windows loads the scroll "
                + "driver into it. Windows will ask you to approve an administrator prompt, and "
                + "the pointer may pause for a few seconds. The mouse stays paired, and nothing "
                + "is installed or removed. Cancel changes nothing.";
        }

        // PointerChildMissing restarts the same Bluetooth parent, but the missing thing is
        // the pointer devnode, not the scroll driver service: the sentence below about
        // starting that service would send the user looking for the wrong fault.
        if (finding.Problem == RepairProblem.PointerChildMissing)
        {
            return "OK rebuilds this mouse's Bluetooth connection so Windows creates its "
                + "pointer device again. Windows will ask you to approve an administrator "
                + "prompt, and the pointer may pause for a few seconds. The mouse stays "
                + "paired, and nothing is installed or removed. Cancel changes nothing.";
        }

        if (finding.Problem == RepairProblem.BatteryReadBlocked)
        {
            return "OK opens the battery-report step for this device. Nothing is installed "
                + "until you confirm there, and the device keeps working either way: only the "
                + "battery reading is affected. Cancel changes nothing.";
        }

        if (finding.Action == RepairAction.RecommendMultitouchWatcher)
            return ScrollGuidanceConsequence;

        if (finding.AutoFixable && finding.Action == RepairAction.RestartBtHidParent)
        {
            return "OK starts the scroll driver service and restarts this mouse on the Bluetooth "
                + "stack. Windows will ask you to approve an administrator prompt, and the pointer "
                + "may pause for a few seconds. The mouse stays paired. Cancel changes nothing.";
        }

        if (finding.AutoFixable && finding.Action == RepairAction.EnableInApp)
        {
            return "OK turns \"Show in Magic Tray\" back on for this device. "
                + "Cancel changes nothing.";
        }

        if (finding.Action == RepairAction.PairInWindows)
        {
            return PairingStepsText(finding.Pid)
                + "\n\nOK opens Windows Settings -> Bluetooth and devices so you can do step 2. "
                + "Cancel changes nothing.";
        }

        if (finding.Action == RepairAction.InstallDriver)
        {
            return "OK opens the driver step for this device. Nothing is installed until you "
                + "confirm there. Cancel changes nothing.";
        }

        return "OK continues. Cancel changes nothing.";
    }

    static string PairingStepsText(string pid)
    {
        var power = DriverHealthChecker.IsV3Pid(pid)
            ? "1. Turn the mouse off and then on again with the switch on the underside."
            : "1. Flip the switch on the underside of the mouse off, then on, and hold until the light blinks.";
        return power
            + "\n2. In Windows Settings -> Bluetooth and devices, choose Add device -> Bluetooth."
            + "\n3. Pick the mouse in the list and let Windows finish pairing."
            + "\n4. Back in the Magic Tray menu, turn \"Show in Magic Tray\" back on for this device if it is off.";
    }

    void StartRepairApply(string pid, RepairProblem problem)
    {
        // DeviceRepair.Apply's generated script polls "sc query <service>" to verify the
        // repair, so it has to be handed the service this mouse's stack actually binds, not
        // the catalog default. The default for PID 0323 is MagicMouseDriver, but the reference
        // PC binds MagicMouseDriver204Scroll in LowerFilters, so the default would verify a
        // service this mouse does not use. Both names pass
        // DeviceRepair.ValidateFilterServiceName (family prefix test), so this cannot throw.
        // Read the live snapshot the same way HandleConflictingFilters does.
        string service;
        try
        {
            service = RepairPlanner.FilterServiceFor(DeviceSnapshotReader.ReadPid(pid, _config));
        }
        catch (Exception ex)
        {
            // Snapshot unreadable: the catalog default is the only name left to verify with.
            Logger.Log($"REPAIR_APPLY_SERVICE_FAIL pid={pid} err={ex.Message}");
            service = RepairPlanner.FilterServiceFor(pid);
        }

        Logger.Log($"REPAIR_APPLY_START pid={pid} problem={problem} service={service}");
        _ = Task.Run(() =>
        {
            RepairOutcome outcome;
            try
            {
                outcome = DeviceRepair.Apply(pid, service);
            }
            catch (Exception ex)
            {
                Logger.Log($"REPAIR_APPLY_FAIL pid={pid} err={ex.Message}");
                outcome = RepairOutcome.Failed;
            }

            // For FilterNotInStack, Ok only means the script's sc query poll saw RUNNING, and
            // RUNNING was already true before the repair: that contradiction is the whole
            // finding. Attachment is the only honest proof, so re-read it here, still off the
            // UI thread (registry plus one CM property query).
            bool? inStack = null;
            if (outcome == RepairOutcome.Ok && problem == RepairProblem.FilterNotInStack)
            {
                try
                {
                    inStack = DeviceSnapshotReader.ReadPid(pid, _config).FilterInStack;
                }
                catch (Exception ex)
                {
                    Logger.Log($"REPAIR_VERIFY_FAIL pid={pid} err={ex.Message}");
                }
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                Logger.Log($"REPAIR_APPLY_RESULT pid={pid} outcome={outcome} ui=gone");
                return;
            }
            dispatcher.Invoke(() => OnRepairApplied(pid, outcome, problem, inStack));
        });
    }

    void OnRepairApplied(string pid, RepairOutcome outcome, RepairProblem problem, bool? inStack)
    {
        Logger.Log($"REPAIR_APPLY_RESULT pid={pid} outcome={outcome}");
        switch (outcome)
        {
            case RepairOutcome.Ok when problem == RepairProblem.FilterNotInStack:
                Logger.Log($"REPAIR_VERIFY pid={pid} problem=FilterNotInStack "
                    + $"in_stack={inStack?.ToString() ?? "unknown"}");
                if (inStack == true)
                {
                    ToastNotifier.Show(TrayMenu.ProductName,
                        "The scroll driver is attached to this mouse again. Try the wheel now.");
                }
                else if (inStack == false)
                {
                    ShowStackRebuildFailedHelp(pid);
                }
                else
                {
                    // No attachment evidence either way: report only what was actually done.
                    ToastNotifier.Show(TrayMenu.ProductName,
                        "This mouse was restarted on this PC. Try the scroll wheel to see "
                        + "whether the scroll driver came back.");
                }
                break;
            case RepairOutcome.Ok when problem == RepairProblem.PointerChildMissing:
                // The generic 0323 line below claims the scroll driver was restarted, which
                // is not what was wrong here: the pointer devnode was missing.
                ToastNotifier.Show(TrayMenu.ProductName,
                    "This mouse was restarted on this PC. Move it to check that the pointer "
                    + "is back.");
                break;
            case RepairOutcome.Ok:
                ToastNotifier.Show(TrayMenu.ProductName, DriverHealthChecker.IsV3Pid(pid)
                    ? "Scroll driver restarted. Try the wheel now."
                    : "Device restarted on this PC.");
                break;
            case RepairOutcome.FilterBlocked:
                ShowFilterBlockedHelp();
                break;
            case RepairOutcome.NoInstances:
                ShowPairingGuidance(pid);
                break;
            default:
                ToastNotifier.ShowError(TrayMenu.ProductName,
                    "The repair did not finish. Open Diagnostics -> Open logs to see why.");
                break;
        }

        AfterFindingHandled();
    }

    // Restart succeeded, the service is running, and the filter is still not in this mouse's
    // device stack: Windows rebuilt the stack from the cached HID layout that leaves the
    // filter out, and a restart only reloads that cache (docs/v3.html:150-156). Removing the
    // mouse and pairing it again is the remaining fix, so say that instead of claiming
    // success. ShowPairingGuidance opens with "not connected over Bluetooth", which is false
    // here: the mouse is live on the stack.
    void ShowStackRebuildFailedHelp(string pid)
    {
        var body =
            "The mouse restarted, but Windows loaded the same connection layout again, so the "
            + "scroll driver is still not attached to this mouse.\n\n"
            + "Windows has remembered a layout for this mouse that leaves the scroll driver "
            + "out, and restarting the mouse only loads that same layout again. The fix left is "
            + "to remove this mouse in Settings -> Bluetooth and devices (pick the mouse, then "
            + "Remove device) and pair it again:\n\n"
            + PairingStepsText(pid)
            + "\n\nOK opens Windows Settings -> Bluetooth and devices.";
        var answer = System.Windows.Forms.MessageBox.Show(
            body, TrayMenu.ProductName,
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Warning);
        if (answer == System.Windows.Forms.DialogResult.OK)
            BluetoothSettings.OpenDevicesPage();
    }

    // What OK does in the scroll-help dialog. Shared with ConsequenceText so a finding that
    // ever carries RecommendMultitouchWatcher promises exactly the same thing.
    const string ScrollGuidanceConsequence =
        "OK opens the mouse driver package page (" + DriverPackageCatalog.V3RepoUrl + "), where "
        + "that watcher lives. Magic Tray changes nothing on this PC, installs nothing, and "
        + "sends nothing to the mouse. Cancel just closes this.";

    // User-asserted "the wheel does nothing" on a mouse whose scroll driver is bound,
    // running and attached. No automatic check can reach this conclusion: movement in the
    // driver's Diag counters proves the multitouch stream IS flowing, but counters standing
    // still cannot tell an idle mouse from a broken one, and Diag\LastAclReceived was
    // measured oscillating 23 / 9 on a mouse whose wheel works. So the user asserts the
    // symptom and this dialog is the whole response.
    //
    // Measured cause on the reference PC: the mouse emits its multitouch stream only after
    // it receives the Apple multitouch enable Feature report, which it forgets on power
    // cycle and reconnect (docs/DESIGN-trackpad-tap.md:54). Only the mouse driver package
    // sends it. Magic Tray must not send it and must not re-implement the package's
    // watcher - that is an explicit non-goal (docs/ENABLE-DISABLE.md:93-97) - so this
    // offers the power cycle, names the watcher, and stops there. Nothing here is elevated.
    void ShowScrollGuidance(string pid)
    {
        var snapshot = FindSnapshot(pid);
        var watcherHealthy = snapshot?.ScrollWatcherHealthy;
        var watcher = watcherHealthy switch
        {
            true => "The mouse driver package's multitouch watcher is running on this PC, so "
                + "this should recover on its own after the next reconnect.\n\n",
            false => "The mouse driver package's multitouch watcher is not running on this PC. "
                + "Installing or re-enabling it is the permanent fix.\n\n",
            _ => "Magic Tray cannot tell whether the mouse driver package's multitouch watcher "
                + "is running on this PC.\n\n",
        };
        var watcherLog = File.Exists(MultitouchWatcherLogPath)
            ? $"The watcher records its own state in {MultitouchWatcherLogPath}.\n\n"
            : string.Empty;

        var body =
            "The scroll driver is installed, running and attached to this mouse, so the wheel "
            + "is not stopped by the driver.\n\n"
            + "This mouse sends its scroll and gesture data only after it has been asked to, "
            + "and it forgets after a power cycle or a reconnect. Try this first: it takes "
            + "seconds and changes nothing on this PC.\n\n"
            + "1. Turn the mouse off with the switch on the underside.\n"
            + "2. Wait two seconds, then turn it back on.\n"
            + "3. Move the pointer, then try the wheel.\n\n"
            + "The permanent fix belongs to the mouse driver package: it installs a watcher "
            + "that asks the mouse again after every reconnect. Magic Tray does not do this "
            + "itself.\n\n"
            + watcher
            + watcherLog
            + ScrollGuidanceConsequence;

        Logger.Log($"SCROLL_HELP_SHOWN pid={pid} watcher="
            + (watcherHealthy?.ToString() ?? "unknown"));
        var answer = System.Windows.Forms.MessageBox.Show(
            body, TrayMenu.ProductName,
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Information);
        if (answer == System.Windows.Forms.DialogResult.OK)
            OpenHelpUrl(DriverPackageCatalog.V3RepoUrl);
    }

    // RepairProblem.ConflictingFilters: an older install left a second family filter name
    // registered on the same stack. Only the stale name goes; the running one stays.
    bool HandleConflictingFilters(RepairFinding finding)
    {
        string keep;
        string[] remove;
        try
        {
            var snapshot = DeviceSnapshotReader.ReadPid(finding.Pid, _config);
            keep = RepairPlanner.FilterServiceFor(snapshot);
            remove = RepairPlanner.StaleFilters(snapshot);
        }
        catch (Exception ex)
        {
            Logger.Log($"REPAIR_STALE_READ_FAIL pid={finding.Pid} err={ex.Message}");
            ToastNotifier.ShowError(TrayMenu.ProductName,
                "Could not re-read this mouse before the repair. Open Diagnostics -> Open logs to see why.");
            return false;
        }

        // Never hand the repair an empty removal list: the stale name is already gone.
        if (remove.Length == 0)
        {
            Logger.Log($"REPAIR_STALE_NOTHING pid={finding.Pid} keep={keep}");
            RefreshFindings();
            ToastNotifier.Show(TrayMenu.ProductName,
                "Nothing needed fixing: this mouse already has one scroll driver registered.");
            return false;
        }

        var body = $"{finding.Title}\n\n{finding.Detail}\n\n"
            + StaleFilterConsequenceText(keep, remove);
        var answer = System.Windows.Forms.MessageBox.Show(
            body, TrayMenu.ProductName,
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Warning);
        if (answer != System.Windows.Forms.DialogResult.OK)
        {
            Logger.Log($"REPAIR_FLOW_CANCEL pid={finding.Pid} problem={finding.Problem}");
            return false;
        }

        Logger.Log($"REPAIR_FLOW_OK pid={finding.Pid} problem={finding.Problem} action={finding.Action}");
        StartStaleFilterRemoval(finding.Pid, keep, remove);
        return false;
    }

    // What OK does in the two-filters dialog, with both driver names spelled out.
    static string StaleFilterConsequenceText(string keep, string[] remove)
    {
        var leftover = string.Join(", ", remove);
        var noun = remove.Length == 1 ? "entry" : "entries";
        return $"OK removes the leftover scroll driver {noun} ({leftover}) from this mouse and "
            + "restarts the mouse connection on this PC, keeping the working scroll driver ("
            + keep + "). Windows will ask you to approve an administrator prompt, and the pointer "
            + "may pause for a few seconds. The mouse stays paired: nothing is unpaired, no driver "
            + "file is deleted, and the Bluetooth radio is left alone. Cancel changes nothing.";
    }

    void StartStaleFilterRemoval(string pid, string keep, string[] remove)
    {
        Logger.Log($"REPAIR_STALE_START pid={pid} keep={keep} remove={string.Join(",", remove)}");
        // Elevated registry edit plus restart-device and the sidecar poll can take minutes.
        _ = Task.Run(() =>
        {
            RepairOutcome outcome;
            try
            {
                outcome = DeviceRepair.ApplyRemoveStaleFilters(pid, keep, remove);
            }
            catch (Exception ex)
            {
                Logger.Log($"REPAIR_STALE_APPLY_FAIL pid={pid} err={ex.Message}");
                outcome = RepairOutcome.Failed;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                Logger.Log($"REPAIR_STALE_RESULT pid={pid} outcome={outcome} ui=gone");
                return;
            }
            dispatcher.Invoke(() => OnStaleFilterRemoved(pid, outcome));
        });
    }

    void OnStaleFilterRemoved(string pid, RepairOutcome outcome)
    {
        Logger.Log($"REPAIR_STALE_RESULT pid={pid} outcome={outcome}");
        switch (outcome)
        {
            case RepairOutcome.Ok:
                ToastNotifier.Show(TrayMenu.ProductName,
                    "Leftover scroll driver removed and the mouse reconnected. Try the scroll "
                    + "wheel now. The battery percent can take one more poll to reappear.");
                break;
            case RepairOutcome.FilterBlocked:
                ShowFilterBlockedHelp();
                break;
            case RepairOutcome.NoInstances:
                ShowPairingGuidance(pid);
                break;
            default:
                ToastNotifier.ShowError(TrayMenu.ProductName,
                    "The repair did not finish. Open Diagnostics -> Open logs to see why.");
                break;
        }

        AfterFindingHandled();
    }

    // WIN32_EXIT 31 / blocked signature: the user has to allow the self-signed driver.
    void ShowFilterBlockedHelp()
    {
        var body =
            "Windows refused to start the scroll driver.\n\n"
            + "The driver is self-signed, so Windows blocks it until this PC is told to allow it. "
            + "Three steps, in this order:\n\n"
            + "1. Turn Test Mode on: in an administrator Command Prompt run "
            + "bcdedit /set testsigning on\n"
            + "2. Turn Memory integrity off in Windows Security -> Device security -> Core isolation.\n"
            + "3. Reboot this PC, then open the Magic Tray menu and run the fix again.\n\n"
            + "OK opens the Magic Mouse v3 page with the full walkthrough.";
        var answer = System.Windows.Forms.MessageBox.Show(
            body, TrayMenu.ProductName,
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Warning);
        if (answer == System.Windows.Forms.DialogResult.OK)
            OpenHelpUrl(V3DocUrl);
    }

    // Nothing to enable or restart: the device is not on the Bluetooth stack at all.
    void ShowPairingGuidance(string pid)
    {
        var body =
            "This device is not connected over Bluetooth, so there is nothing for Windows to start.\n\n"
            + PairingStepsText(pid)
            + "\n\nOK opens Windows Settings -> Bluetooth and devices.";
        var answer = System.Windows.Forms.MessageBox.Show(
            body, TrayMenu.ProductName,
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Warning);
        if (answer == System.Windows.Forms.DialogResult.OK)
            BluetoothSettings.OpenDevicesPage();
    }

    void EnableDeviceInApp(string pid)
    {
        try
        {
            _config.SetDeviceEnabled(pid, true);
            Logger.Log($"REPAIR_ENABLE_IN_APP pid={pid}");
        }
        catch (Exception ex)
        {
            Logger.Log($"REPAIR_ENABLE_IN_APP_FAIL pid={pid} err={ex.Message}");
            ToastNotifier.ShowError(TrayMenu.ProductName,
                "Could not turn this device back on in Magic Tray. Open Diagnostics -> Open logs to see why.");
        }
    }

    // Route to the driver offer the device row already uses. No new installer paths.
    void OfferDriverForFinding(RepairFinding finding)
    {
        var pid = finding.Pid;
        if (DriverHealthChecker.IsV3Pid(pid))
        {
            // Same rule as the radio it routes to: the choice is remembered
            // only once the install is confirmed.
            _ = RunDriverActionAsync(
                () => OfferThenRemember(
                    Config.Driver0323Kmdf, () => DriverInstaller.OfferV3KmdfInstallAsync()));
            return;
        }

        if (DriverInstaller.IsTrackpadV1BootCampPid(pid))
        {
            RunDriverAction(() => DriverInstaller.OfferTrackpadV1BootCamp(pid));
            return;
        }

        if (Array.Exists(DriverHealthChecker.AppleFilterPids, p => TrayMenu.PidEq(p, pid)))
        {
            RunDriverAction(DriverInstaller.OfferV1V2ScrollFix);
            return;
        }

        if (DriverHealthChecker.IsKeyboardPid(pid))
        {
            RunDriverAction(DriverInstaller.OfferKeyboardSdpPatch);
            return;
        }

        System.Windows.Forms.MessageBox.Show(
            $"{finding.Title}\n\nMagic Tray has no built-in driver step for PID {pid.ToUpperInvariant()}. "
            + "The driver guide lists what this device needs.",
            TrayMenu.ProductName,
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Information);
        OpenHelpUrl(DriversDocUrl);
    }

    void AfterFindingHandled()
    {
        RefreshFindings();
        _poller.RefreshNow();
    }

    static string FormatInterval(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalMinutes}m";

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnSystemVisualChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemVisualChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        // A live SystemEvents handler or an armed timer would keep this whole
        // object - tray icon, menu, poller - alive for the process lifetime,
        // and _settleStopped makes a tick that is already in flight a no-op.
        lock (_settleLock)
        {
            _settleStopped = true;
            _settleTimer?.Dispose();
            _settleTimer = null;
        }
        _poller.BatteryChanged -= OnBatteryChanged;
        _poller.Dispose();
        _criticalAlert?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _currentIcon?.Dispose();
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    enum Marker { Mouse, Keyboard }

    static Marker MarkerFor(string name) =>
        name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) ? Marker.Keyboard : Marker.Mouse;

    static Icon MakeIcon(int pct, bool isLow, Marker marker, bool driverMissing = false)
    {
        int S = SystemInformation.SmallIconSize.Width;
        float k = S / 16f;
        int R(float v) => (int)Math.Round(v * k, MidpointRounding.AwayFromZero);

        using var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.None;

        bool alert = pct >= 0 && (isLow || pct < 10);
        Color outline = alert ? FillColor(pct, isLow) : ThemeOutlineColor();

        int bx = R(1), by = R(3), bw = R(11), bh = R(7);
        int t = Math.Max(1, R(1));
        using (var ob = new SolidBrush(outline))
        {
            g.FillRectangle(ob, bx, by, bw, t);
            g.FillRectangle(ob, bx, by + bh - t, bw, t);
            g.FillRectangle(ob, bx, by, t, bh);
            g.FillRectangle(ob, bx + bw - t, by, t, bh);
            g.FillRectangle(ob, bx + bw, by + R(2), R(1.5f), R(3));
        }

        if (pct >= 0)
        {
            int tx = bx + t, ty = by + t, tw = bw - 2 * t, th = bh - 2 * t;
            int fw = pct == 0 ? 0 : Math.Min(tw, Math.Max(1, (int)Math.Round(tw * pct / 100.0)));
            if (fw > 0)
                using (var fb = new SolidBrush(FillColor(pct, isLow)))
                    g.FillRectangle(fb, tx, ty, fw, th);

            g.SmoothingMode = S >= 20 ? SmoothingMode.AntiAlias : SmoothingMode.None;
            DrawMarker(g, marker, outline, R);
            g.SmoothingMode = SmoothingMode.None;
        }

        if (driverMissing)
            using (var dot = new SolidBrush(Color.FromArgb(255, 220, 30)))
                g.FillRectangle(dot, S - R(3), 0, R(3), R(3));

        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    static Color FillColor(int pct, bool isLow) => (pct, isLow) switch
    {
        (_, true) => Color.FromArgb(255, 64, 13),
        (> 50, _) => Color.FromArgb(52, 199, 89),
        (>= 20, _) => Color.FromArgb(255, 204, 0),
        (>= 10, _) => Color.FromArgb(255, 149, 0),
        _ => Color.FromArgb(255, 59, 48),
    };

    static void DrawMarker(Graphics g, Marker m, Color color, Func<float, int> R)
    {
        using var b = new SolidBrush(color);
        var rect = m == Marker.Mouse
            ? new Rectangle(R(6), R(10), R(4), R(6))
            : new Rectangle(R(5), R(11), R(6), R(4));
        using var path = Capsule(rect);
        g.FillPath(b, path);
    }

    static GraphicsPath Capsule(Rectangle r)
    {
        var p = new GraphicsPath();
        if (r.Width <= 1 || r.Height <= 1) { p.AddRectangle(r); return p; }
        if (r.Height > r.Width)
        {
            int d = r.Width;
            p.AddArc(r.X, r.Y, d, d, 180, 180);
            p.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
        }
        else
        {
            int d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        }
        p.CloseFigure();
        return p;
    }

    static void RefreshTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _lightTaskbar = (key?.GetValue("SystemUsesLightTheme") as int? ?? 0) == 1;
        }
        catch { _lightTaskbar = false; }
    }

    static Color ThemeOutlineColor() =>
        _lightTaskbar ? Color.FromArgb(70, 70, 70) : Color.FromArgb(220, 220, 220);
}
