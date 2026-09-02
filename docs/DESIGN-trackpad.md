# DESIGN — Magic Trackpad as a first-class battery device

Status: Battery product implemented. Tap 1/2/3 + 3-finger middle is **implementing** in KMDF (`docs/DESIGN-trackpad-tap.md`). `TrayApp.BuildDeviceRow` is unchanged — mouse driver radios and Unknown-mouse warnings already do not apply to trackpad kinds.

Magic Utilities ships trackpad tap, scroll, and gestures. Magic Tray battery rows stay enable + threshold + time alerts for Trackpad v1 `030E`, v2 `0265`, 2024 `0324`. **Tap** is our KMDF PTP vertical, not MU: 1-finger = primary, 2-finger = secondary, 3-finger = middle from Windows Precision Touchpad. No Boot Camp. No PathA. No custom button areas / silent click / pressure / desktop swipes.

## User flow

Pair a Magic Trackpad (or plug it in over USB). Open the tray. The row is a battery device, same shape as a keyboard:

```
Magic Trackpad           73%
  Enabled on this PC       ✓
  ────────
  Low battery alert
    10%  15%  20%  25%
```

v2 / 2024 use their display names (`Magic Trackpad 2`, `Magic Trackpad 2024`). Battery text is `No reading` until the first HID percent, then `N%`. Drain extras (`%/h`, `~Nh` / `~Nd`) are the same helpers mice use.

There is **no** Driver submenu. No KMDF / Patched Apple / Stock radios. No Boot Camp / Stock Windows radios. No Scroll vs Battery. No “Unknown Apple mouse — check for an app update”. No orange Fix scroll. No Fix battery reads (that is keyboard SDP only).

Uncheck **Enabled on this PC**: persist `enabled_<pid>=false`, skip poll and alerts for that PID, keep the row. Same as mice and keyboards. V1 of enable does not disconnect Bluetooth.

## PIDs and kinds

Numeric facts already in `MouseBatteryDevice.KnownMice`. Do not invent BLE `0001004C` PIDs that are not in that table. USB `VID_05AC` `PID_030E` (trackpad v1) is owned by the USB VID coverage vertical — this design assumes `TryKnownMouse("030e")` already resolves via the existing BT `PID&030E` row.

| PID  | Kind                 | Display name           | Power        | Connector (alerts) |
| ---- | -------------------- | ---------------------- | ------------ | ------------------ |
| 030E | `MagicTrackpadV1`    | Magic Trackpad         | AA           | — (replace AA)     |
| 0265 | `MagicTrackpadV2`    | Magic Trackpad 2       | rechargeable | Lightning          |
| 0324 | `MagicTrackpadV3`    | Magic Trackpad 2024    | rechargeable | USB-C              |

`TryKnownMouse` is the name/kind lookup for health placeholders and tests. Discover still matches full VID+PID path tokens in `KnownMice`. Trackpads are not `KnownMousePids` (`030d` / `0310` / `0269` / `0323`) and sit on `NonScrollApplePids`, so driver health never feeds them as a mouse or as `UnknownAppleMouse`.

## Time alerts (reuse `BatteryAlertPolicy`)

No new policy. `IsAaPowered` already includes `MagicTrackpadV1`. Rechargeable kinds (v2 / v3) take the 24h night-before path. `PlugConnector`: Lightning for `MagicTrackpadV2`, USB-C for `MagicTrackpadV3`.

| Kind              | Window                         | Connected 0–1%              | Disconnect after last good 0–1% |
| ----------------- | ------------------------------ | --------------------------- | ------------------------------- |
| v1 AA             | `0 < hours ≤ 48` → `two_day`   | death modal, replace AA     | death modal                     |
| v2 Lightning      | `0 < hours ≤ 24` → `night_before` | plug Lightning now       | `CloseModal`, never death       |
| v3 USB-C          | `0 < hours ≤ 24` → `night_before` | plug USB-C now           | `CloseModal`, never death       |

No 10% floor. No evening reminder. Title is `Trackpad battery low` when the display name contains `Trackpad`. AA copy never contains USB-C / Lightning / charge. Plug copy never contains Replace.

## Radios stay mouse-only

`TrayMenu` helpers, not a new trackpad driver enum:

- `IsV3(kind, pid)` — `MagicMouseV3` or PID `0323` only. False for every trackpad kind / `030e` / `0265` / `0324`.
- `IsV1V2Mouse(kind)` — `MagicMouseV1` or `MagicMouseV2` only. False for trackpad kinds.
- `RecommendedLabel` — KMDF nag, Boot Camp nag, and keyboard SDP only. Null for trackpad kinds at every status / percent.
- `ShowPathAModeSwitch` / `ShowFixScroll` — false for trackpad kinds.

`BuildDeviceRow` already gates KMDF radios on `IsV3` and Boot Camp radios on `IsV1V2Mouse`. Trackpad rows therefore get enable + threshold only. Do not edit `TrayApp.cs` unless those gates regress.

`ShouldShowHealthRow` is false for trackpad PIDs on `Ok` / `NotBound` (they are not `KnownMousePids`). Trackpad rows appear from Discover battery events, not from mouse driver health.

## Files

| File | Role |
| --- | --- |
| `docs/DESIGN-trackpad.md` | This design. |
| `MagicMouseTray.Tests/TrayMenuTests.cs` | Append: radios / `IsV3` / `IsV1V2Mouse` / `RecommendedLabel` false or null for trackpad kinds. |
| `MagicMouseTray.Tests/MouseBatteryDeviceTests.cs` | Append: `TryKnownMouse` for `030e` / `0265` / `0324` only. Do not rewrite `KnownMice`. |
| `MagicMouseTray.Tests/BatteryAlertPolicyTests.cs` | 1–2 tests if missing: v1 AA 48h; v2 Lightning / v3 USB-C 24h. Policy itself is unchanged. |
| `MagicMouseTray/TrayApp.cs` | Do not edit unless radios or Unknown-mouse warnings leak onto trackpad kinds. |

## Tests

WindowsDesktop, slice filter only. No live HID.

- `TryKnownMouse("030e"|"0265"|"0324")` → display name + `MagicTrackpadV1`/`V2`/`V3`.
- `IsV1V2Mouse` false and `IsV3` false for those kinds (PID `030e` / `0265` / `0324`).
- `RecommendedLabel` null for trackpad kinds (`NotBound` and `Ok`).
- `ShowPathAModeSwitch` / `ShowFixScroll` false for trackpad kinds.
- `ShouldShowHealthRow([], trackpadPid, Ok|NotBound)` false.
- Trackpad v1 at 50% / 48h → `two_day`, Buy AA, no USB-C / Lightning.
- Trackpad v2 at 50% / 24h → `night_before`, Lightning, no USB-C / Replace.
- Trackpad v3 at 50% / 24h → `night_before`, USB-C, no Lightning / Replace.

## Non-goals

- Custom button areas, silent click, click pressure, 3-finger drag, desktop swipes, media keys, modifier remaps.
- Vendoring Magic Utilities, `MagicMouse.sys`, `MagicTrackpad.sys`, or any MU binary.
- KMDF / Boot Camp / PathA **radios** on trackpad rows (tap bind is INF + KMDF, not a tray radio this vertical).
- Rewriting `KnownMice` USB rows (USB VID coverage owns `PID_030D` / `PID_030E`).
- README / MU feature matrix (MuFreeParity tap rows).
- Push. Killing the live tray. Publishing unless `TrayApp.cs` changed (it did not).

Tap 1/2/3 and 3-finger middle: `docs/DESIGN-trackpad-tap.md`.
