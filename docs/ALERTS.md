# Battery alerts

Magic Tray warns you before a Magic Mouse, Magic Keyboard, or Magic Trackpad runs out. There are two kinds of warning: a **percent floor** you pick, and **time alerts** based on how fast that device has actually been draining.

There is no evening reminder. The old 15% / 20% / 25% choices are gone.

## Percent floor (10 → 5 → 1)

In the tray, **Low battery threshold** (all devices) and **Low battery alert** (one device) list:

- 10%
- 5%
- 1%

Default is **10%**. When the reading drops to your chosen percent or below (and a time alert did not already fire), Magic Tray shows a toast.

1% is also the critical band (death / plug-now), described below.

## Time alerts

Hours-to-empty comes only from **observed drain**: Magic Tray records battery percent over time and estimates remaining hours from that. It never invents a rate.

Until two successful readings exist, hours are unknown:

- The percent floor still applies.
- Toasts use the simple wording for that device kind (“replace batteries soon” or “plug in USB-C / Lightning soon”).
- Menu labels only show a time estimate when drain has been measured for that device (for example `10%  (~2d)` or `10%  (~8h)`). Until then they stay as **10% / 5% / 1%**. They will not show a made-up “~2 days” or “~24h”.

When hours *are* known, a time alert can fire even at a high percent (for example 50%) if remaining time is already in the window below.

### AA batteries — Magic Keyboard, Magic Mouse v1, Magic Trackpad v1

- Toast when about **48 hours** or less remain: buy AA batteries.
- At **0–1%** while connected: a death window — replace the batteries.
- If the device **disconnects** after the last good reading was 0–1%: the same death window.

Disconnect after a last good reading well above 1% is not death.

### Rechargeable — Magic Mouse 2024 (USB-C), Magic Mouse v2 (Lightning), Magic Trackpad 2 (Lightning), Magic Trackpad 2024 (USB-C)

- Toast when about **24 hours** or less remain: plug in tonight (USB-C or Lightning, matching the device).
- At **0–1%** while connected: a plug-now window.
- If it **disconnects**, that window **closes**. Disconnect is not treated as death — you can charge it.

## Skip alerts for one device

Each device row has **Enabled on this PC** (on by default). Uncheck it to skip battery polling and alerts for that device on this computer. The row stays so you can turn it back on. Bluetooth pairing does not change.

Use this when the same mouse is mostly used on a Mac and you do not want this PC toasting about it.

## Report a bug

The tray **Help / Documentation** menu opens this page, the repository, and GitHub issues.

Open an issue: https://github.com/LesleyMurfin/magic-tray/issues

Source: https://github.com/LesleyMurfin/magic-tray
