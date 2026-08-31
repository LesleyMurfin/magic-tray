# KMDF does not live here

`LesleyMurfin/magic-tray` is the tray client. It does **not** vendor `MagicMouseDriver` source, INF, vcxproj, `.sys`, or sign/install scripts.

**KMDF home:** https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix (`v2-kmdf-driver/Install-KMDF.cmd`)

The tray **pulls that repo’s default branch** when the user selects Best / MagicMouseDriver for PID **0323**, then runs **`v2-kmdf-driver/Install-KMDF.cmd`** (SYSTEM tasks `MM-Kmdf-Install` / `MM-Kmdf-PostBoot`). It does not copy those files into this folder, does not generate `bind-filter.ps1`, and does **not** run PATH-A `v1-binary-patch/installer/Install-MagicMousePatch.ps1`.

| Device | What the tray does |
|--------|-------------------|
| 0323 | Pull default branch and run `v2-kmdf-driver/Install-KMDF.cmd` |
| 030D / 0269 / 0310 | Pull tealtadpole Boot Camp INF (do not retarget with KMDF) |
| Keyboard | PATH-C SDP patch (`scripts/kbd-patch-cachedservices.ps1`) — not Keymagic2 |

If `Install-KMDF.cmd` is not on the default branch yet (draft [KMDF PR #3](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/pull/3)), install reports that and stops. Do not pull the draft branch. Do not fall back to PATH-A. Do not vendor source or scripts here.
