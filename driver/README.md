# KMDF does not live here

`LesleyMurfin/magic-tray` is the tray client. It does **not** vendor `MagicMouseDriver` source, INF, vcxproj, or `.sys`.

**KMDF home:** https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix (`v2-kmdf-driver/`)

The tray **pulls** that package when the user selects Best / MagicMouseDriver for PID **0323**, then installs it (elevated `pnputil` + sole `LowerFilters=MagicMouseDriver`). It does not copy those files into this folder.

| Device | What the tray does |
|--------|-------------------|
| 0323 | Pull+install KMDF from `magic-mouse-v3-windows-fix` / `v2-kmdf-driver` |
| 030D / 0269 / 0310 | Pull tealtadpole Boot Camp INF (do not retarget with KMDF) |
| Keyboard | PATH-C SDP patch (`scripts/kbd-patch-cachedservices.ps1`) — not Keymagic2 |

If the pulled `v2-kmdf-driver/` has no INF yet, install reports that and stops. Do not fall back to vendoring source here.
