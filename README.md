# Magic Tray

Free Windows tray app for Apple Magic Mouse and Magic Keyboard on Windows 10/11. **No subscription.**

This is a **free alternative** to [Magic Utilities](https://magicutilities.net/) — battery %, alerts, Start with Windows, and a driver SELECT that installs public packages. It is **not** a paid clone of Magic Utilities’ proprietary Microsoft-signed drivers.

![Tray icon showing 83% battery](docs/screenshot-tray.png)

## The Problem

Apple Magic Mouse and Magic Keyboard on Windows have no native battery indicator. Magic Utilities works, but it is a paid subscription — and the trial can break scroll when it expires.

## Features

- Battery % in the tray icon (color-coded as the battery drains)
- Tooltip and right-click menu show each device and its battery
- Low-battery toast at your threshold (10 / 15 / 20 / 25%)
- Start with Windows (per-user registry — no installer)
- **Driver SELECT** per device: pulls the best public GitHub / Apple Boot Camp package and installs it (administrator approval). No silent rebind.
- Single `.exe`

## Supported Mice

| Model | Bluetooth PID | Best package (pulled, not vendored) |
|-------|--------------|-------------------------------------|
| Magic Mouse 2024 (USB-C) | 0x0323 | [sbagirici/apple-magic-mouse-scroll-fix-windows](https://github.com/sbagirici/apple-magic-mouse-scroll-fix-windows) — WHQL `AppleWirelessMouse.sys` + 0323 LowerFilters |
| Magic Mouse v1 (AA) | 0x030D | [tealtadpole/MagicMouse2DriversWin11x64](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) — Boot Camp INF (030D / 0310 / 0269) |
| Magic Mouse v2 | 0x0269 | Same tealtadpole INF |

Rain9333/MagicMouse2DriversWin10x64 is the parent dump (identical INF/CAT/SYS). It is not strictly better, so the picker uses the Win11 fork her tray already cites.

## Supported Keyboards

| Model | Bluetooth PID | Best package |
|-------|--------------|--------------|
| Apple Wireless Keyboard (2011) | 0x0239 / 0x023A / 0x023B | Boot Camp `AppleKeyboardMagic2` / `Keymagic2.inf` via [timsutton/brigadier](https://github.com/timsutton/brigadier) |
| Magic Keyboard / Touch ID | 0x024F / 0x0250 / 0x0267 / 0x026C / … | Same Keymagic2 extract |

## Install

1. Download `MagicMouseTray.exe` from [Releases](../../releases)
2. Run it — no installer
3. Right-click: status, Start with Windows, **Driver**, Quit

**Requires**: Windows 10 1809+ (build 17763) or Windows 11, x64

## Driver SELECT

On **Driver → Best** (or the named package) the tray downloads that repo and installs. It does **not** vendor KMDF, does **not** pull `LesleyMurfin/*` driver repos, does **not** use Magic Utilities binaries, and does **not** default to chrischip (self-signed catalog, HVCI off).

| Device | Pull | Why it won |
|--------|------|------------|
| 0323 | sbagirici `driver/applewirelessmouse.sys` | Official WHQL `.sys`; binds 0323; Win11 24H2; her v3 README cites this as the baseline |
| 030D / 0310 / 0269 | tealtadpole `AppleWirelessMouse/*.inf` | Stock Apple INF lists those PIDs; same bits as Rain9333; her tray cites the Win11 fork |
| Keyboard | brigadier → `Keymagic2.inf` | Official Apple Boot Camp keyboard driver from Apple’s CDN |

Install runs only after you confirm. If 0323 is still on a custom `MagicMouseDriver`, Best replaces that bind with `applewirelessmouse` for the selected PID only.

## Right-click menu

| Item | What it does |
|------|-------------|
| Device rows | Name, battery %, bound filter, **Driver** picker |
| Low battery alert | 10 / 15 / 20 / 25% per device |
| Start with Windows | Toggle auto-start on login |
| Refresh battery | Read now |
| Open logs | `%APPDATA%\MagicMouseTray\debug.log` |
| Quit | Exit |

## How battery reading works

The tray reads Apple HID reports with Win32 P/Invoke. It never flips the device stack to take a reading. If a device is present but the report is not exposed, the menu shows **Battery unavailable**.

## Building from source

Requires .NET 8 SDK (Windows).

```powershell
dotnet publish -c Release
# Output: bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\MagicMouseTray.exe
```

## SmartScreen warning

When you first run `MagicMouseTray.exe`, Windows may show "We can't verify who created this file." Click **Run**. For a full SmartScreen block: **More info → Run anyway**, or Properties → **Unblock**.

## Diagnostics

Log file: `%APPDATA%\MagicMouseTray\debug.log`

- `MOUSE_BATTERY_OK` — successful read
- `DRIVER_PULL` / `DRIVER_INSTALL` / `DRIVER_BIND` — GitHub pull + install
- `DRIVER_CHECK status=...` — read-only filter detection
- `TOAST_SENT` — notification fired

## Releases & versioning

Releases are built by CI on a `v*` tag (`.github/workflows/release.yml`).

## License

MIT
