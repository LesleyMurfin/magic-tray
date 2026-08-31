# Magic Tray

Windows tray app that shows Apple Magic Mouse (and keyboard) battery on Windows 10/11. No subscription.

The tray is a **user-facing client**: status, battery when the device already exposes it, start with Windows, quit. It does **not** install, bind, or repair the kernel driver.

![Tray icon showing 83% battery](docs/screenshot-tray.png)

## The Problem

Apple Magic Mouse on Windows 11 has no native battery indicator. Magic Mouse Utilities — the only working commercial solution — requires a paid subscription that breaks scroll entirely when the trial expires.

## Features

- Battery % in the tray icon (color-coded: green → yellow → orange → red as battery drains)
- Tooltip and right-click menu show each device and its battery
- Adaptive polling: checks every 24h above 20%; tightens when the battery is low
- Low-battery toast at your threshold (10 / 15 / 20 / 25%), plus a 1% persistent warning
- Start with Windows (a per-user registry entry — no installer)
- Single `.exe`, no admin rights, no PowerShell

## Supported Mice

| Model | Bluetooth PID | Status |
|-------|--------------|--------|
| Magic Mouse 2024 (USB-C) | 0x0323 | ✅ Confirmed |
| Magic Mouse v1 (AA battery) | 0x030D | ✅ Confirmed |
| Magic Mouse v2 | 0x0269 | ⚠ Included, not tested (device not available) |

## Supported Keyboards

| Model | Bluetooth PID | Status |
|-------|--------------|--------|
| Apple Wireless Keyboard (2011, A1314) ANSI/ISO/JIS | 0x0239 / 0x023A / 0x023B | ✅ Confirmed when the keyboard already exposes a battery Feature report |
| Magic Keyboard (A1644) / ISO | 0x024F / 0x0250 | ⚠ Included, not tested (device not available) |
| Magic Keyboard with Touch ID (A2449) / ISO | 0x0267 / 0x026C | ⚠ Included, not tested (device not available) |

## Install

1. Download `MagicMouseTray.exe` from [Releases](../../releases)
2. Run it — no installer, no admin rights
3. A tray icon appears. Right-click for status, Start with Windows, and Quit.

**Requires**: Windows 10 1809+ (build 17763) or Windows 11, x64

Use the release build. Do not replace a working install from a leftover copy under `C:\temp`.

## Scroll and the KMDF driver

Scroll on Magic Mouse 2024 (PID **0323**) is handled by the **KMDF filter** in [`driver/`](driver/) (`MagicMouseDriver.vcxproj`). Live bind is sole `LowerFilters=MagicMouseDriver` on the 0323 stack (`HidBth` / `MagicMouseDriver` / `BthEnum`).

The tray **does not** install that driver, write `LowerFilters`, run `pnputil`, start/stop the service, or reboot you into a driver. If scroll already works, leave the bind alone.

The older Magic Mouse (PID **030D**) stays on `applewirelessmouse`. Do not retarget 030D to `MagicMouseDriver`, and do not install both filters (`MagicMouseDriver,applewirelessmouse`).

Operator build/install for the KMDF package lives in `driver/` — not in this tray UI.

## Right-click menu

| Item | What it does |
|------|-------------|
| Device rows | Name, battery %, and (for mice) which filter is already bound |
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

The KMDF driver is a separate Visual Studio / MSBuild project: `driver/MagicMouseDriver.vcxproj`.

## SmartScreen warning

When you first run `MagicMouseTray.exe`, Windows may show "We can't verify who created this file." This is normal for unsigned open-source software — click **Run**.

If you downloaded the file and it shows the full SmartScreen block ("Windows protected your PC"), click **More info → Run anyway**. Alternatively, right-click the file → Properties → check **Unblock** → OK.

**For developers building from source on WSL**: Windows treats the WSL filesystem as a network path, which always triggers this dialog. Copy the built exe to a local Windows folder (for example `%USERPROFILE%\Downloads`) before running.

## Diagnostics

Log file: `%APPDATA%\MagicMouseTray\debug.log`

Key log lines:
- `MOUSE_BATTERY_OK` / `OK battery=` — successful read
- `OPEN_FAILED err=5` — COL01 skipped (normal — Windows holds this handle)
- `DRIVER_CHECK status=Ok/NotInstalled/NotBound/UnknownAppleMouse` — read-only filter detection
- `TOAST_SENT` — notification fired
- `CRITICAL_ALERT_SHOWN` — 1% persistent window shown

## Releases & versioning

Releases are built by CI on a `v*` tag (`.github/workflows/release.yml`): the workflow builds,
runs the unit tests on a `windows-latest` runner, publishes the single-file `win-x64` exe,
optionally Authenticode-signs it (if cert secrets are configured), and attaches it to a GitHub
release. The build stamps `FileVersion`/`AssemblyVersion` (currently `1.0.0.0`), visible via the
exe's Properties → Details tab.

## License

MIT
