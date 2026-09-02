# DESIGN — Time-based battery alerts (10/5/1 percent rungs)

Status: Settings UI contract is 10/5/1 going down, with observed time in the label. Hours from `DrainRateTracker.GetHoursToEmpty` only — never invent a duration. C# menus owned by TrayAlertHelp.

Percent is a bad predictor. A Magic Keyboard at 50% can have two days left, or twenty. Time still wins at 50% if hours-to-empty is in the window. The 10% floor is an extra rung, not a replacement: when time did not fire, toast `percent` at `1 < pct <= threshold` (default 10). Rate-unknown at 10% is the live-PC gap this rung closes. Rate-unknown at 11% (or 16%) stays silent until 0–1%.

Hours-to-empty comes from `DrainRateTracker.GetHoursToEmpty` only. Never invent a rate.

## Rules

| Rule | Behavior |
| --- | --- |
| Percent toast at ≤ threshold when time did not fire | After the time-window branch: if `1 < pct <= threshold` (default 10) and `percent` not in `fired`, toast `EventPercent` with `FallbackBody`. Rate unknown **or** rate known outside the time window both qualify. |
| Time wins at 50% | If hours-to-empty is in the window, toast even at 50% (or 20%, or 90%). Time toast at 50%/48h still wins over percent. 10%/40h AA is still `two_day`. |
| Rate-unknown at 10% toasts `percent` | Live-PC gap: keyboard at 16% with rate unknown stays silent (above 10). Rate-unknown at 10% AA and v3 toast `percent`. Rate-unknown at 11% → `None`. |
| 9%/8 days rate-known toasts `percent` | AA at 9% / 192h is outside the 48h window, so the percent rung fires. Not silence. |
| AA 48h + death | Keyboard, Magic Mouse v1, Trackpad v1: toast when `0 < hours ≤ 48`. Death modal at connected 0–1%, and on disconnect when last good was 0–1%. |
| v3 24h USB-C + connected 0–1% | Rechargeable (v3 / v2 Lightning / trackpad v2+): toast when `0 < hours ≤ 24`. Connected 0–1% → plug-now modal. Disconnect → `CloseModal`, never death. |
| Copy | Time: AA replace / buy AA. Plug devices: USB-C (v3) or Lightning (v2). Percent rung: existing `FallbackBody` (`Replace batteries soon` / `Plug in USB-C\|Lightning soon`). Never “charge”. |

Windows (inclusive): AA `hoursToEmpty <= 48`; rechargeable `hoursToEmpty <= 24`. `hoursToEmpty` must be `> 0` and `rateKnown`.

No evening reminder. The 24h USB-C toast is the night-before warning. No second `evening_yyyy-MM-dd` toast.

## Settings UI

Time alerts are policy, not a percent radio. AA 48h + death and rechargeable 24h night-before + 0–1% plug-now stay in `BatteryAlertPolicy`. The tray does not offer 15/20/25 as “how early to warn.”

Percent picker goes **down from 10**: `10`, `5`, `1` only (default 10). Global **Low battery threshold** and per-device **Low battery alert** list those three rungs in that order. Do not bring back 15/20/25 as product defaults.

Each choice includes an estimated time frame. Hours come from `DrainRateTracker.GetHoursToEmpty` only (observed %/h). Never invent a rate or a duration.

- Per-device, rate known (`GetHoursToEmpty(device, pct) > 0`): `10%  (~Nd)` when hours ≥ 24, else `10%  (~Nh)`. Same pattern for 5 and 1.
- Per-device, rate unknown (fewer than two readings, or hours ≤ 0): bare `10%` / `5%` / `1%`. Do **not** print `~2 days` or `~24h` as a guess. The percent floor still applies.
- Global (no single device): `10%  then time alerts` — no invented hours. Same for 5 and 1.

`Config.IsValid` accepts 10, 5, and 1. Default `GlobalThreshold` stays 10. Legacy ini `threshold=15|20|25` and `threshold_<pid>=15|20|25` are ignored on load, so `threshold_030d=20` is not the live floor. Choosing 10, 5, or 1 from either menu persists that value.

## Evaluate order

Same signature as today (TrayApp-compatible):

`Evaluate(kind, name, pct, threshold, hoursToEmpty, rateKnown, nowLocal, lastGoodPct, fired)`

1. **Disconnect** (`pct < 0`): AA death modal if `lastGoodPct` is 0 or 1 and `death` not fired; else AA `None`. Rechargeable: `CloseModal`, not death.
2. **Connected 0–1%** (`pct <= 1`): AA death replace-batteries modal; rechargeable plug-now modal. Independent of rate. Event `death`.
3. **Time window** (`rateKnown && hoursToEmpty > 0`): AA `two_day` if ≤ 48h and not fired; rechargeable `night_before` if ≤ 24h and not fired. Percent is not consulted. Time toast at 50%/48h still wins over percent.
4. **Percent rung** (`1 < pct <= threshold`, default 10): toast `EventPercent` (`"percent"`) with `FallbackBody` if `percent` not already in `fired`. Rate unknown **or** rate known outside the time window both qualify. Else `None`. `nowLocal` unused (evening removed).

Death / plug-now beats the time toast: 1% with 10h left is the modal, not 48h/24h copy. Time beats percent: 10%/40h AA is `two_day`, not `percent`.

AA disconnect after last good 8% is **not** death. Death-on-disconnect is last good 0–1% only (the connected critical band, then the HID vanished).

`RearmFired(..., int threshold)`: drop `death` when `pct > 1`; drop time events when hours leave the window; drop `percent` when `pct > threshold`. TrayApp already has `threshold` — pass it. Do not early-return on `pct > threshold`.

## Copy

| Event | Body |
| --- | --- |
| AA `two_day` | `{name} — about {N} day(s) of battery left. Buy AA batteries.` (`N = max(1, ceil(hours/24))`) |
| AA `death` | `{name} batteries are dead. Replace the batteries.` |
| Rechargeable `night_before` | `{name} — about {H}h left. Plug in {USB-C\|Lightning} before you leave tonight.` |
| Rechargeable connected 0–1% | `{name} is at {pct}%. Plug in {USB-C\|Lightning} now.` |
| `percent` | existing `FallbackBody`: AA `{name} is at {pct}%. Replace batteries soon.` Plug `{name} is at {pct}%. Plug in {USB-C\|Lightning} soon.` |

AA bodies never contain `USB-C`, `Lightning`, or `charge`. Plug bodies never contain `Replace`. Connector: Lightning for Magic Mouse v2 / Trackpad v2; USB-C otherwise.

Diagnostics **Test notification** (`PreviewToast`) may still use percent copy. That is not an alert-table toast.

## Files

| File | Role |
| --- | --- |
| `docs/DESIGN-time-alerts.md` | This design. |
| `MagicMouseTray/BatteryAlertPolicy.cs` | Pure `Evaluate`. No WinForms. |
| `MagicMouseTray.Tests/BatteryAlertPolicyTests.cs` | Observable contracts below. |
| `MagicMouseTray/TrayApp.cs` | Pass `threshold` into `RearmFired`. No `if (pct > threshold) return`. Global **Low battery threshold** and per-device **Low battery alert** offer 10/5/1 going down (`Config.ThresholdChoices`), with observed time in the label. |
| `MagicMouseTray/Config.cs` | `IsValid` is 10, 5, or 1. Default `GlobalThreshold` 10. Legacy 15/20/25 ignored on load. `ThresholdChoices` is `{ 10, 5, 1 }`. |
| `MagicMouseTray.Tests/ConfigTests.cs` | Valid thresholds are 10, 5, and 1; 15/20/25 rejected and not offered; legacy 20 is not the live floor. |

`DrainRateTracker` is unchanged: it still does not invent a %/h.

## Tests

No live HID. Policy only.

- AA at 50% / 48h → `two_day` toast, “2 days”, “Buy AA batteries”, no USB-C.
- v3 at 50% / 24h → `night_before` toast, “USB-C”, “24h”, no Replace.
- AA at 50% / 72h → `None`. v3 at 50% / 25h → `None`.
- AA at 10% / 40h → `two_day` (time beats percent).
- AA at 9% / 192h (8 days) → `EventPercent`, `FallbackBody` Replace-soon copy.
- Rate unknown at 10% (AA and v3) → `EventPercent` toast, correct `FallbackBody` copy.
- Rate unknown at 11% → `None`. Rate unknown at 16% (live keyboard) → `None`.
- Rate unknown at connected 0 or 1% → still death / plug-now modal.
- AA connected 0% or 1% → death modal, Replace, no USB-C.
- AA disconnect, last good 1 (or 0) → death modal. Last good 8 → `None`.
- v3 connected 0–1% → plug USB-C modal. v3 disconnect → `CloseModal`, not death.
- Mixed Discover omit: dropped v3 closes modal; dropped keyboard with last good 1 deaths.
- `ShouldCloseModal` only when the critical device name matches.
- AA copy never USB-C/charge; v3 copy never Replace/charge.
- `threshold: 10` does not suppress a 50% / 48h AA toast.
- `RearmFired` drops `percent` when `pct` is 15 (above threshold).
- Valid thresholds are 10, 5, and 1. `SetThreshold`/`SetGlobalThreshold` of 15/20/25 no-ops. Load of `threshold_030d=20` → live floor 10.
- `Config.ThresholdChoices` is `{ 10, 5, 1 }` going down — menus do not offer 15/20/25. Rate-unknown per-device labels are bare `10%`/`5%`/`1%`; they do not invent `~2 days` / `~24h`. Global is `10%  then time alerts`.

## TrayApp wiring

`Evaluate` keeps the old signature.

`ApplyBatteryAlert` must **not** return early on `pct > threshold`. A 50% / 20h reading must reach `Evaluate`. Rearm `_firedEvents` when hours leave the window, when `pct > 1` after death, and when `pct > threshold` for `percent`. Pass `threshold` into `RearmFired`. Keep passing `threshold` and `DateTime.Now`.

`AdaptivePoller` already Evaluate’s Discover-omit as `pct=-1`. Leave that.

Do not kill the live tray until publish.

## Non-goals

- `BluetoothSettings.cs`, push.
- Changing `DrainRateTracker` ceilings or inventing hours.
- Evening reminder.
- Killing the live tray before publish.
- PATH-A in copy.
- Icon colors, driver radios, USB VID, README MU matrix.
