# MagicMouseDriver (KMDF)

This folder is the **KMDF home**. `MagicMouseDriver.vcxproj` / `MagicMouseDriver.sln` build the kernel filter. The tray app in `MagicMouseTray/` does **not** install, bind, repair, or reboot into this driver.

## Live bind (do not undo)

Magic Mouse 2024 (PID **0323**) is already bound as the **sole** lower filter:

`LowerFilters=MagicMouseDriver`

Stack: `HidBth` / `MagicMouseDriver` / `BthEnum`.

Do **not** install a dual filter (`LowerFilters=MagicMouseDriver,applewirelessmouse`). Do **not** retarget the older Magic Mouse (PID **030D**) — that device stays on `applewirelessmouse`.

The INF (`MagicMouseDriver.inf`) documents the same sole-filter rule. Operator install (`pnputil`, signing) stays here, not in the tray UI or `scripts/mm-task-setup.ps1`.
