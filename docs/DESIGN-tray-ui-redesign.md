# DESIGN — Tray menu + driver SELECT

Status: **ACTIVE** — 2026-08-31.

Product: free Magic Utilities alternative. Easy tray + SELECT that installs. KMDF **ships from** `LesleyMurfin/magic-mouse-v3-windows-fix`, not from this repo.

Hard rules:

- Do not vendor `Driver.c` / INF / vcxproj / `.sys` into `magic-tray/driver/`
- 0323 Best **pulls** `v2-kmdf-driver` from that repo and binds sole `MagicMouseDriver` (0323 only)
- 030D / 0269 / 0310: tealtadpole public INF — do not retarget with KMDF
- Keyboard: PATH-C SDP patch only — not Keymagic2, not keyboard KMDF
- No Magic Utilities binaries, no leftover mm-dev dual-filter scripts, no chrischip HVCI-off
- No silent rebind

## Menu

```
device rows (name + battery + bound filter)
  Driver → Best | named package
---
Start with Windows
---
Refresh battery
Open logs
---
Quit
```

## Related

- `MagicMouseTray/DriverPackageCatalog.cs`
- `MagicMouseTray/DriverInstaller.cs`
- `driver/README.md` — pointer only
