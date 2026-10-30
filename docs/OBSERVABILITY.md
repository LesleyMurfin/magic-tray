# Magic device observability on Windows 11

This is the **userspace** source of truth for how Magic Tray reads Apple Magic batteries, how often it polls, how driver health is reported, and which log lines mean what.

Kernel scroll for Magic Mouse 2024 / v3 (`0323`) is **not** documented here. That lives in [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) (Mode A/B, KMDF, HID collections). The tray never writes `LowerFilters` itself.

Operator doc, not a site page: it is robots-disallowed like every other `.md` under `docs/`.

## What we observe

| Device | PID | Battery how | What we never use |
|--------|-----|-------------|-------------------|
| Magic Mouse 2024 / v3 | `0323` | HID **Input `0x90` on COL02**, percent in `buf[2]` | Feature `0x47`, WMI, iPhone Hands-Free |
| Magic Mouse v1 / v2 / Wireless | `030D` / `0269` / `0310` | Input `0x90` first; Feature only if that collection is absent | WMI / Hands-Free |
| Magic Keyboard (all KnownKeyboards) | `0239`…`0322` | Feature `0x47` **after** the Bluetooth SDP cache patch | A kernel mouse filter |
| Magic Trackpad 1 / 2 / 2024 | `030E` / `0265` / `0324` | HID when Windows exposes it | A tray-installed trackpad `.sys` |

Catalog (CI-enforced): [`KnownMice`](../MagicMouseTray/MouseBatteryDevice.cs), [`KnownKeyboards`](../MagicMouseTray/KeyboardBatteryDevice.cs). Hardware reports: [TESTED.md](TESTED.md). Alerts: [ALERTS.md](ALERTS.md).

## Poll cadence

[`AdaptivePoller`](../MagicMouseTray/AdaptivePoller.cs) + [`DrainRateTracker`](../MagicMouseTray/DrainRateTracker.cs):

- Above the attention floor (default **10%**): check about **every 24h**.
- Below the floor: interval from **observed** drain (hours-to-threshold / 3). No invented %/hour.
- Floor: **5 min** for Magic Mouse v3, **30 min** for other devices.
- Each HID read has a **5s** timeout so one wedged device cannot stall the tray.
- While waiting a long interval, a **15s** name-set probe picks up newly paired devices.
- **Refresh Now** cancels the wait and reads immediately.

Disabled PIDs stay in the menu but are not HID-read and do not toast.

## Driver health (mice only)

[`DriverHealthChecker`](../MagicMouseTray/DriverHealthChecker.cs) looks at what is **bound** on the HID/BTHENUM node. It does not rebind.

`DriverStatus` values: `Ok` (v1/v2 `applewirelessmouse` bound, or no Apple mouse paired), `PatchedKmdf` (0323 on `MagicMouseDriver`), `PathAPatched` (0323 on patched Apple), `StockKmdf` (0323 on stock HidBth), `NotInstalled`, `NotBound`, `UnknownAppleMouse`, `Error`.

0323: KMDF is the only path with **scroll and** Input `0x90` battery. Patched Apple is mutually exclusive. Stock is battery-if-exposed, no scroll.

v1/v2: catalog-signed Boot Camp. Test Mode not required.

## Logs

`%APPDATA%\MagicMouseTray\debug.log`

| Line | Meaning |
|------|---------|
| `MOUSE_BATTERY_OK device=… pct=N% (Input 0x90 COL02)` | v3 read succeeded |
| `MOUSE_BATTERY_OK device=… pct=N% (unified Feature 0xNN)` / `(split)` | v1/v2 read succeeded |
| `MOUSE_OPEN_FAILED path=… err=5` | Collection skipped (Windows holds that handle) |
| `KB_OPEN_FAILED device=… err=…` | Keyboard collection could not be opened |
| `DRIVER_CHECK pid=… bth=… hid=… bound=… status=… scm=… cands=…` | Per-device bind |
| `DRIVER_CHECK status=… devices=N` | Aggregate |
| `DRIVER_CHECK_FAILED err=…` | Registry read threw |
| `TOAST_SENT title=…` | Low-battery notification |
| `CRITICAL_ALERT_SHOWN device=… pct=…` | 0–1% persistent window |
| `POLL_SCHEDULED devices=N lowest_pct=… next_in=…` | Next interval |

Percent sentinels: `-1` no reading this cycle, `-2` device present but battery unreadable (v3 without Input `0x90` on COL02), `-3` three consecutive `-1` cycles for that device. Only `>= 0` is recorded by `DrainRateTracker`.

## Two repos

| | [Magic Tray](https://github.com/LesleyMurfin/magic-tray) | [v3 Windows driver](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) |
|---|---|---|
| Battery %, alerts, HID IDs, poll, logs | **This repo** | No tray UI |
| 0323 kernel scroll / Mode A/B | Installs KMDF from there (`Install-KMDF.cmd` on `main`) | **That repo** |
| v1/v2 scroll | Boot Camp (tealtadpole) | Out of scope |
