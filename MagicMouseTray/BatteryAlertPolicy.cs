// SPDX-License-Identifier: MIT
namespace MagicMouseTray;

internal enum BatteryAlertAction
{
    None,
    Toast,
    Modal,
}

internal readonly record struct BatteryAlertDecision(
    BatteryAlertAction Action,
    string? Title,
    string? Body,
    string? EventId,
    bool CloseModal);

// Pure evaluate for the alert table. WinForms is not required to test it.
// Time-to-empty comes from DrainRateTracker.GetHoursToEmpty — never invented.
// Time wins at 50% if hours are in the AA 48h / rechargeable 24h window.
// Percent is an extra rung: 1 < pct <= threshold (default 10) when time
// did not fire. nowLocal stays on the signature; policy does not use it.
internal static class BatteryAlertPolicy
{
    internal const string EventTwoDay = "two_day";
    internal const string EventDeath = "death";
    internal const string EventNightBefore = "night_before";
    internal const string EventPercent = "percent";

    internal static bool IsAaPowered(DeviceKind kind) =>
        kind is DeviceKind.MagicKeyboard
            or DeviceKind.MagicMouseV1
            or DeviceKind.MagicTrackpadV1;

    // Names present last poll and missing from this Discover snapshot. Each must
    // be Evaluate'd with pct=-1 so AA death and v3 CloseModal run on mixed drop.
    internal static IReadOnlyList<string> NamesOmittedFromDiscover(
        IEnumerable<string> previouslySeen,
        IEnumerable<string> discoveredNow)
    {
        var live = new HashSet<string>(discoveredNow, StringComparer.OrdinalIgnoreCase);
        var omitted = new List<string>();
        foreach (var name in previouslySeen)
        {
            if (!string.IsNullOrEmpty(name) && !live.Contains(name))
                omitted.Add(name);
        }
        return omitted;
    }

    // Close the critical modal only for the device that opened it.
    internal static bool ShouldCloseModal(bool closeModal, string? criticalDevice, string name) =>
        closeModal
        && !string.IsNullOrEmpty(name)
        && string.Equals(criticalDevice, name, StringComparison.OrdinalIgnoreCase);

    internal static BatteryAlertDecision Evaluate(
        DeviceKind kind,
        string name,
        int pct,
        int threshold,
        double hoursToEmpty,
        bool rateKnown,
        DateTime nowLocal,
        int lastGoodPct,
        IReadOnlySet<string> fired)
    {
        _ = nowLocal;

        var title = TitleFor(name);
        var aa = IsAaPowered(kind);

        if (pct < 0)
        {
            if (aa)
            {
                if (lastGoodPct is 0 or 1 && !fired.Contains(EventDeath))
                    return Modal(title, AaDeathBody(name), EventDeath);
                return None();
            }

            return new BatteryAlertDecision(BatteryAlertAction.None, null, null, null, CloseModal: true);
        }

        if (pct <= 1)
        {
            if (!fired.Contains(EventDeath))
            {
                var body = aa ? AaDeathBody(name) : PlugNowBody(kind, name, pct);
                return Modal(title, body, EventDeath);
            }

            return None();
        }

        if (HoursInTimeWindow(kind, hoursToEmpty, rateKnown))
        {
            if (aa && !fired.Contains(EventTwoDay))
                return Toast(title, AaTwoDayBody(name, hoursToEmpty), EventTwoDay);
            if (!aa && !fired.Contains(EventNightBefore))
                return Toast(title, NightBeforeBody(kind, name, hoursToEmpty), EventNightBefore);
        }
        else if (pct > 1 && pct <= threshold && !fired.Contains(EventPercent))
        {
            return Toast(title, FallbackBody(kind, name, pct), EventPercent);
        }

        return None();
    }

    // AA: 0 < hours ≤ 48. Rechargeable: 0 < hours ≤ 24. Percent is not consulted.
    internal static bool HoursInTimeWindow(DeviceKind kind, double hoursToEmpty, bool rateKnown)
    {
        if (!rateKnown || hoursToEmpty <= 0)
            return false;
        return IsAaPowered(kind) ? hoursToEmpty <= 48 : hoursToEmpty <= 24;
    }

    // Rearm time events when hours leave the window. Rearm death when pct > 1
    // (replaced / charged). Rearm percent when pct climbs above threshold.
    // Do not rearm death on disconnect (hours unknown).
    internal static void RearmFired(
        HashSet<string> fired,
        DeviceKind kind,
        int pct,
        double hoursToEmpty,
        bool rateKnown,
        int threshold)
    {
        if (pct > 1)
            fired.Remove(EventDeath);
        if (!HoursInTimeWindow(kind, hoursToEmpty, rateKnown))
        {
            fired.Remove(EventTwoDay);
            fired.Remove(EventNightBefore);
        }
        if (pct > threshold)
            fired.Remove(EventPercent);
    }

    internal static (string Title, string Body) PreviewToast(DeviceKind kind, string name, int pct)
    {
        var p = pct >= 0 ? pct : 10;
        return (TitleFor(name), FallbackBody(kind, name, p));
    }

    internal static string TitleFor(string name)
    {
        if (name.IndexOf("Keyboard", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Keyboard battery low";
        if (name.IndexOf("Trackpad", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Trackpad battery low";
        return "Mouse battery low";
    }

    static BatteryAlertDecision None() =>
        new(BatteryAlertAction.None, null, null, null, false);

    static BatteryAlertDecision Toast(string title, string body, string eventId) =>
        new(BatteryAlertAction.Toast, title, body, eventId, false);

    static BatteryAlertDecision Modal(string title, string body, string eventId) =>
        new(BatteryAlertAction.Modal, title, body, eventId, false);

    static string PlugConnector(DeviceKind kind) =>
        kind is DeviceKind.MagicMouseV2 or DeviceKind.MagicTrackpadV2 ? "Lightning" : "USB-C";

    static string AaDeathBody(string name) =>
        $"{name} batteries are dead. Replace the batteries.";

    static string AaTwoDayBody(string name, double hours)
    {
        var days = (int)Math.Ceiling(hours / 24.0);
        if (days < 1) days = 1;
        var unit = days == 1 ? "day" : "days";
        return $"{name} — about {days} {unit} of battery left. Buy AA batteries.";
    }

    static string PlugNowBody(DeviceKind kind, string name, int pct) =>
        $"{name} is at {pct}%. Plug in {PlugConnector(kind)} now.";

    static string NightBeforeBody(DeviceKind kind, string name, double hours)
    {
        var h = Math.Max(1, (int)Math.Round(hours));
        return $"{name} — about {h}h left. Plug in {PlugConnector(kind)} before you leave tonight.";
    }

    static string FallbackBody(DeviceKind kind, string name, int pct)
    {
        if (IsAaPowered(kind))
            return $"{name} is at {pct}%. Replace batteries soon.";
        return $"{name} is at {pct}%. Plug in {PlugConnector(kind)} soon.";
    }
}
