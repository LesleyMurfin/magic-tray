# DESIGN — Tray menu + driver SELECT

Status: **ACTIVE** — 2026-08-31.

Product: magic-tray is a **free** Magic Utilities alternative (battery, alerts, Start with Windows, driver SELECT). It is not a paid clone of Magic Utilities’ proprietary drivers.

Hard rules (Lesley Murfin):

- Pull public GitHub / Apple Boot Camp packages — **not** `LesleyMurfin/*` driver repos
- Do not vendor KMDF / INF / `.sys` in `magic-tray/driver/`
- Do not default to chrischip (HVCI off / self-signed catalog)
- Do not silent-rebind live Windows
- Menu stays simple

## Verified mapping (2026-08-31)

| Device | Winner | Why | Skipped |
|--------|--------|-----|---------|
| 0323 | `sbagirici/apple-magic-mouse-scroll-fix-windows` `driver/applewirelessmouse.sys` | Same WHQL `.sys` blob as the Boot Camp dump; binds 0323; Win11 24H2; her v3 README baseline | chrischip (0323 unconfirmed, test-sign, HVCI off); LesleyMurfin KMDF (not the package source); supermarsx / SecondRocket mirrors |
| 030D / 0310 / 0269 | `tealtadpole/MagicMouse2DriversWin11x64` `AppleWirelessMouse/` | INF lists those PIDs (2019 Apple). Rain9333 parent is **identical** INF/CAT/SYS SHAs — not strictly better. Her tray cites tealtadpole. | Rain9333 (same bits); 0323 not in that INF |
| Keyboard | `timsutton/brigadier` → `AppleKeyboardMagic2/Keymagic2.inf` | Official Boot Camp from Apple CDN | huaikitty 1★ re-pack; Pearipherals / MagicWindows (not kernel drivers) |

## Menu

```
device rows (name + battery + bound filter)
  Driver → Best | named public package
---
Start with Windows
---
Refresh battery
Open logs
---
Quit
```

## Related

- `MagicMouseTray/DriverPackageCatalog.cs` — package map
- `MagicMouseTray/DriverInstaller.cs` — zip / brigadier pull + elevated install
- `driver/README.md` — pointer only, not SSOT
