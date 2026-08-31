# DESIGN — Tray menu + driver SELECT

Status: **ACTIVE** — 2026-08-31. KMDF is **not** in this repo.

Hard rule (Lesley Murfin):

- KMDF Magic Mouse lives at https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix
- `LesleyMurfin/magic-tray` must not vendor `MagicMouseDriver` source / INF / vcxproj / `.sys`
- Tray is a simple client (status, battery, Start with Windows, Quit) **plus** driver SELECT
- On select (or Best), the tray **pulls** that package from the owning GitHub repo and installs it
- No leftover operator UX: no `V3RecycleManager`, no `C:\mm-dev-queue`, no `MM-Dev-Cycle` flip, no tealtadpole, no F7 DSE story

## Verified repos (2026-08-31)

| Need | Repo | Result |
|------|------|--------|
| KMDF / mouse v3 packages | `LesleyMurfin/magic-mouse-v3-windows-fix` | **Home.** `v1-binary-patch/`, `v2-kmdf-driver/` |
| Keyboard | `LesleyMurfin/apple-kb-monitor`, `apple-peripherals` | **Not found** (404) |
| Mouse hardware 030D / 0269 | LesleyMurfin siblings | **Not found.** v3-windows-fix is 0323-only |

## Menu

```
device rows (name + battery + bound filter)
  Driver → Best | KMDF | v3 | v2 | v1   (pull from magic-mouse-v3-windows-fix)
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
- `MagicMouseTray/DriverInstaller.cs` — zip pull + elevated pnputil
- `driver/README.md` — pointer only, not SSOT
