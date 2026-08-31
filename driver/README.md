# KMDF does not live here

`LesleyMurfin/magic-tray` is the tray client. It does **not** vendor `MagicMouseDriver` source, INF, vcxproj, or `.sys`.

**KMDF Magic Mouse home:** https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix

The tray **pulls** the package the user selects (or Best for the detected PID) from that repo and installs it. It does not treat this folder as SSOT.

Verified packages in that repo (do not invent names):

| Tray choice | Path in `magic-mouse-v3-windows-fix` | Notes |
|-------------|--------------------------------------|--------|
| v1 | `v1-binary-patch/` | Binary-patch package for Magic Mouse v3 (PID 0323) |
| v2 / KMDF | `v2-kmdf-driver/` | KMDF rewrite for the same 0323 mouse |
| v3 | same repo (0323 device) | “v3” is the Magic Mouse 2024 hardware this repo targets |
| Best | KMDF (`v2-kmdf-driver/`) for PID 0323 | User must choose; no silent rebind |

No LesleyMurfin GitHub repo was found for keyboard drivers (`apple-kb-monitor`, `apple-peripherals` — 404) or for Magic Mouse v1/v2 hardware (030D / 0269). Those picker rows stay disabled until a home exists.

Do not copy leftover WSL `v2-kmdf-driver/install-driver.ps1` or `mm-dev.ps1` into this tree.
