# Magic Tray

Windows tray app that shows Apple Magic Mouse (and keyboard) battery on Windows 10/11. No subscription.

The tray is a **user-facing client**: status, battery when the device already exposes it, start with Windows, quit, and a **driver SELECT**. On select (or Best for the detected device) it **pulls** the package from the GitHub repo that owns that driver and installs it. It does **not** vendor KMDF source, INF, or `.sys`.

**KMDF Magic Mouse home:** https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix

![Tray icon showing 83% battery](docs/screenshot-tray.png)

## The Problem

Apple Magic Mouse on Windows 11 has no native battery indicator. Magic Mouse Utilities — the only working commercial solution — requires a paid subscription that breaks scroll entirely when the trial expires.

## Features

- Battery % in the tray icon (color-coded: green → yellow → orange → red as battery drains)
- Tooltip and right-click menu show each device and its battery
- Adaptive polling: checks every 24h above 20%; tightens when the battery is low
- Low-battery toast at your threshold (10 / 15 / 20 / 25%), plus a 1% persistent warning
- Start with Windows (a per-user registry entry — no installer)
- Driver SELECT per device: Best / KMDF / v3 / v2 / v1 — pull from GitHub, then elevated `pnputil`
- Single `.exe`, no leftover `C:\mm-dev-queue` / `MM-Dev-Cycle` flip UX

## Supported Mice

| Model | Bluetooth PID | Status |
|-------|--------------|--------|
| Magic Mouse 2024 (USB-C) | 0x0323 | ✅ Confirmed — driver packages in [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) |
| Magic Mouse v1 (AA battery) | 0x030D | Battery read if present. No LesleyMurfin driver repo (do not retarget unless this device is present and you pick a package) |
| Magic Mouse v2 | 0x0269 | ⚠ Included, not tested. No LesleyMurfin driver repo |

## Supported Keyboards

| Model | Bluetooth PID | Status |
|-------|--------------|--------|
| Apple Wireless Keyboard (2011, A1314) ANSI/ISO/JIS | 0x0239 / 0x023A / 0x023B | Battery when a Feature report is already exposed. No LesleyMurfin keyboard-driver repo (`apple-kb-monitor` / `apple-peripherals` not found) |
| Magic Keyboard (A1644) / ISO | 0x024F / 0x0250 | ⚠ Included, not tested |
| Magic Keyboard with Touch ID (A2449) / ISO | 0x0267 / 0x026C | ⚠ Included, not tested |

## Install

1. Download `MagicMouseTray.exe` from [Releases](../../releases)
2. Run it — no installer
3. A tray icon appears. Right-click for status, Start with Windows, driver SELECT, and Quit.

**Requires**: Windows 10 1809+ (build 17763) or Windows 11, x64

Use the release build. Do not replace a working install from a leftover copy under `C:\temp`.

## Driver SELECT (pull, do not vendor)

KMDF and the v1/v2 0323 packages live in **https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix**, not in this repo.

| Menu choice | Pulled from |
|-------------|-------------|
| Best | `v2-kmdf-driver/` (KMDF) for PID 0323 |
| KMDF | `v2-kmdf-driver/` |
| v3 | `v2-kmdf-driver/` (Magic Mouse 2024 / 0323) |
| v2 | `v2-kmdf-driver/` |
| v1 | `v1-binary-patch/` |

The tray downloads that repo’s `main` zip, looks for an INF under the package path, and runs elevated `pnputil /add-driver`. It does **not** run leftover `install-driver.ps1` / `mm-dev.ps1`, does **not** flip `LowerFilters` via `MM-Dev-Cycle`, and does **not** install from `magic-tray/driver/`.

If the pulled path has no INF, install fails with that message — the package is not published yet. The tray never silently rebinds; install runs only after the user picks a package and confirms.

030D unpaired: no 030D install unless that mouse is present and you select a package. There is no LesleyMurfin 030D driver repo.

## Right-click menu

| Item | What it does |
|------|-------------|
| Device rows | Name, battery %, bound filter, **Driver** picker |
| Low battery alert | 10 / 15 / 20 / 25% per device |
| Start with Windows | Toggle auto-start on login |
| Show Logitech devices | Optional HID++ battery for directly connected Logitech mice |
| Refresh battery | Read now |
| Test battery alert | Fire a sample toast |
| Open logs | `%APPDATA%\MagicMouseTray\debug.log` |
| Quit | Exit |

## How battery reading works

The tray reads Apple HID reports with Win32 P/Invoke (`HidD_GetInputReport` / `HidD_GetFeature`). It never flips the device stack to take a reading. If a device is present but the report is not exposed, the menu shows **Battery unavailable**.

## Building from source

Requires .NET 8 SDK (Windows).

```powershell
dotnet publish -c Release
# Output: bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\MagicMouseTray.exe
```

KMDF is built and published in [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix), not here.

## SmartScreen warning

When you first run `MagicMouseTray.exe`, Windows may show "We can't verify who created this file." This is normal for unsigned open-source software — click **Run**.

If you downloaded the file and it shows the full SmartScreen block ("Windows protected your PC"), click **More info → Run anyway**. Alternatively, right-click the file → Properties → check **Unblock** → OK.

**For developers building from source on WSL**: Windows treats the WSL filesystem as a network path, which always triggers this dialog. Copy the built exe to a local Windows folder (for example `%USERPROFILE%\Downloads`) before running.

## Diagnostics

Log file: `%APPDATA%\MagicMouseTray\debug.log`

Key log lines:
- `MOUSE_BATTERY_OK` — successful read
- `DRIVER_PULL` / `DRIVER_INSTALL` — GitHub pull + pnputil
- `DRIVER_CHECK status=...` — read-only filter detection
- `TOAST_SENT` — notification fired

## Releases & versioning

Releases are built by CI on a `v*` tag (`.github/workflows/release.yml`).

## License

MIT
