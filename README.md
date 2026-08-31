# Magic Tray

Free Windows tray app for Apple Magic Mouse and Magic Keyboard on Windows 10/11. **No subscription.**

This is a **free alternative** to [Magic Utilities](https://magicutilities.net/) — battery %, alerts, Start with Windows, and a driver SELECT that installs. It is **not** a paid clone of Magic Utilities’ proprietary drivers.

![Tray icon showing 83% battery](docs/screenshot-tray.png)

## The Problem

Apple Magic Mouse and Magic Keyboard on Windows have no native battery indicator. Magic Utilities works, but it is a paid subscription — and the trial can break scroll when it expires.

## Features

- Battery % in the tray icon
- Per-device alerts (10 / 15 / 20 / 25%)
- Start with Windows
- **Driver SELECT**: detect PID, install Best after you confirm (SELECT is the override)
- Single `.exe`

## Supported Mice

| Model | PID | Best |
|-------|-----|------|
| Magic Mouse 2024 (USB-C) | 0x0323 | **Pull** [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) and run **that repo’s** `Install-MagicMousePatch.ps1` — not vendored here |
| Magic Mouse v1 | 0x030D | [tealtadpole/MagicMouse2DriversWin11x64](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) Boot Camp INF |
| Magic Mouse v2 | 0x0269 | Same tealtadpole INF |

KMDF is **0323-only**. The tray will not retarget 030D with MagicMouseDriver.

## Supported Keyboards

Keyboard Best is the **PATH-C BTHPORT SDP cache patch** (`scripts/kbd-patch-cachedservices.ps1`) — zero kernel. Not Keymagic2, not brigadier, not keyboard KMDF.

## Driver SELECT

| Device | Action | Notes |
|--------|--------|--------|
| 0323 | Pull `LesleyMurfin/magic-mouse-v3-windows-fix` and run **that repo’s** sign+install script (`v1-binary-patch/installer/Install-MagicMousePatch.ps1`, or a later `v2-kmdf-driver` installer if they publish one) | This repo does not copy Driver.c / INF / .sys / install scripts. If their script or binary is missing after the pull, install stops. |
| 030D / 0310 / 0269 | Pull tealtadpole INF + `pnputil` | Stock Apple PIDs |
| Keyboard | Elevated SDP patch | PATH-C only |

No Magic Utilities binaries. No leftover `mm-dev` / `install-driver` dual-filter scripts. No chrischip (HVCI-off). No silent rebind — confirm first.

## Install

1. Download `MagicMouseTray.exe` from [Releases](../../releases)
2. Run it
3. Right-click: status, Start with Windows, **Driver**, Quit

**Requires**: Windows 10 1809+ or Windows 11, x64

## Right-click menu

| Item | What it does |
|------|-------------|
| Device rows | Name, battery %, bound filter, **Driver** |
| Low battery alert | 10 / 15 / 20 / 25% |
| Start with Windows | Auto-start on login |
| Refresh battery | Read now |
| Open logs | `%APPDATA%\MagicMouseTray\debug.log` |
| Quit | Exit |

## Building from source

```powershell
dotnet publish -c Release
```

KMDF and its sign+install scripts live in [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix). This repo pulls that tree and runs their installer.

## License

MIT
