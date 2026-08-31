# MagicMouseDriver (KMDF)

This folder **is** the KMDF driver. `MagicMouseDriver.vcxproj` / `MagicMouseDriver.sln` build `MagicMouseDriver.sys`. The tray in `MagicMouseTray/` is a user client only — it does **not** install, bind, write `LowerFilters`, run `pnputil`, or start/stop this service.

| File | Role |
|------|------|
| `Driver.c` / `Driver.h` | KMDF filter: SDP IOCTL 0x410210 completion + 0323-only inject |
| `InputHandler.c` / `.h` | Rewrite SDP attribute 0x0206 (HIDDescriptorList) |
| `HidDescriptor.c` / `.h` | Descriptor C (RID 0x02 scroll + RID 0x90 battery) |
| `GestureEngine.c` / `.h` | RID 0x12 → RID 0x02 scroll translation (0323) |
| `MagicMouseDriver.inf` | PnP package: **PID 0323 only**, sole `LowerFilters=MagicMouseDriver` |
| `MagicMouseDriver.vcxproj` | EWDK / WDK KMDF project |

## Live bind (do not undo)

Magic Mouse 2024 (PID **0323**):

`LowerFilters=MagicMouseDriver` (sole filter)

Stack: `HidBth` / `MagicMouseDriver` / `BthEnum`.

The older Magic Mouse (PID **030D**) stays on `applewirelessmouse`. The INF does not list 030D or 0310. The source injects Descriptor C only when `ProductId == 0x0323`.

Do **not** install a dual filter (`MagicMouseDriver,applewirelessmouse`). Do **not** import leftover `v2-kmdf-driver/{install-driver,mm-dev}.ps1` — those clobber the live sole bind.

## Build

Requires Enterprise WDK (EWDK). No `.sys` binary is checked in; build from this source.

```
<EWDK>\LaunchBuildEnv.cmd
cd driver
msbuild MagicMouseDriver.vcxproj /p:Configuration=Release /p:Platform=x64
```

Signing is off in the vcxproj (`SignMode=Off`). Operators sign the package; the tray never does.

## Operator install (not a tray action)

```
pnputil /add-driver MagicMouseDriver.inf /install
```

This is kernel packaging work. It does not belong in the tray UI or in `scripts/mm-task-setup.ps1`.
