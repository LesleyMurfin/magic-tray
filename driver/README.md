# KMDF does not live here

`LesleyMurfin/magic-tray` is the tray client. It does **not** vendor `MagicMouseDriver` source, INF, vcxproj, `.sys`, or sign/install scripts.

**KMDF home:** https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix

The tray **pulls that repo** when the user selects Best / MagicMouseDriver for PID **0323**, then runs **that repo’s** easy sign+install script (`v1-binary-patch/installer/Install-MagicMousePatch.ps1` today; a `v2-kmdf-driver` installer if they publish one later). It does not copy those files into this folder and does not generate a local `bind-filter.ps1`.

| Device | What the tray does |
|--------|-------------------|
| 0323 | Pull `magic-mouse-v3-windows-fix` and run **their** installer |
| 030D / 0269 / 0310 | Pull tealtadpole Boot Camp INF (do not retarget with KMDF) |
| Keyboard | PATH-C SDP patch (`scripts/kbd-patch-cachedservices.ps1`) — not Keymagic2 |

If the pulled repo has no installer (or their script cannot find its shipped binary), install reports that and stops. Do not fall back to vendoring source or scripts here.
