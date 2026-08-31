# DESIGN — Tray menu (superseded)

Status: **SUPERSEDED** — 2026-08-31. The 2026-07-29 proposal (per-device driver badges plus auto-remediation / “Fix scroll driver” / Battery Reads recycle) is withdrawn.

Hard split (Lesley Murfin, 2026-08-30):

- KMDF belongs in `driver/` (`MagicMouseDriver`), **not** in the tray.
- The tray does not install, bind, write `LowerFilters`, run `pnputil`, start/stop the service, repair M13, dual-filter, or reboot into a driver.
- The tray is a user-facing client: status, battery if already readable, start with Windows, quit.

Live Windows state this design must not fight:

- PID **0323** — sole `LowerFilters=MagicMouseDriver`.
- PID **030D** — stays `applewirelessmouse`.

## Current menu (this repo)

```
device rows (name + battery + already-bound filter name)
---
Start with Windows
Show Logitech devices
---
Refresh battery
Test battery alert
Open logs
---
Quit
```

No “Install Apple Driver”, no tealtadpole download, no Battery Reads / PATH-B flip, no PowerShell.

## Related

- `README.md` — user-facing client docs
- `driver/README.md` — KMDF home
- `MagicMouseTray/DriverHealthChecker.cs` — read-only status (0323 → MagicMouseDriver, 030D → applewirelessmouse)
