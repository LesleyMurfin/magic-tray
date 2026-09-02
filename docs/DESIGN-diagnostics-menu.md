# DESIGN — Diagnostics menu launches existing scripts

Status: Implemented. Small ADW vertical so Lesley can capture driver/device state from the tray.

The user wants **existing repo scripts**, not a new in-process dump formatter. There is no `DriverStateDump` and no `driver-state.txt`. Diagnostics runs `powershell.exe -File` on scripts the resolver finds next to the exe, in `scripts\`, or by walking up to the repo root the same way `FindKeyboardPatchScript` does.

`scripts/` has on the order of a hundred lab tools. The tray does not list them. Allowlist only, and only when the file is present. The published exe does not copy these scripts; a ship install without a checkout simply omits the items.

## Menu

Tray → **Diagnostics** →

```
Test notification
Open logs
Open diagnostics folder
Run capture-state.ps1              ← if found (current state)
Run diagnose-driver.ps1            ← if found
Run mm-bt-stack-snapshot.ps1       ← if found
Run mm-devmgr-dump.ps1             ← if snapshot missing and this exists
```

| Item | Script | Why |
| --- | --- | --- |
| Test notification | — | Keep. Preview toast. |
| Open logs | — | Keep. `%APPDATA%\MagicMouseTray\debug.log`. |
| Open diagnostics folder | — | Keep. Same directory. |
| Run capture-state.ps1 | `scripts/capture-state.ps1` (or next to exe) | Current device/driver state (COL01/COL02, LowerFilters, service). |
| Run diagnose-driver.ps1 | `diagnose-driver.ps1` at **repo root** (or next to exe / `scripts\`) | applewirelessmouse .sys, sc, pnputil, LowerFilters. |
| Run mm-bt-stack-snapshot.ps1 | `scripts/mm-bt-stack-snapshot.ps1` | BT HID stack + filter chain. Preferred over devmgr. |
| Run mm-devmgr-dump.ps1 | `scripts/mm-devmgr-dump.ps1` | Fallback when snapshot is absent. |

Not on the menu: mm-rev-eng, mm-magicutilities-capture, ETW/tracelog, wheel counter, mm-state-flip, or any other lab script.

Snapshot vs devmgr: one stack-dump slot. Prefer `mm-bt-stack-snapshot.ps1`; use `mm-devmgr-dump.ps1` only if snapshot is not found.

## Resolver

Same walk as `DriverInstaller.FindKeyboardPatchScript`:

1. `{start}\<name>` (next to the exe / injected test root)
2. `{start}\scripts\<name>`
3. Walk up to 6 parents: `{dir}\<name>` and `{dir}\scripts\<name>`

`start` defaults to `AppContext.BaseDirectory`. Tests pass a fake tree.

`diagnose-driver.ps1` lives at the repo root, not under `scripts\`, which is why the walk checks `{dir}\<name>` as well as `{dir}\scripts\<name>`.

Launch: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<path>"`, `UseShellExecute = true`, working directory = the script’s folder. No extra args, no C# formatter, no invented dump file.

## Why not 120 scripts / not a C# dump

- Lesley asked for the scripts that already exist. A C# `Format(...)` would drift from those scripts and hide Bound/Service behind a second implementation.
- Shipping the whole `scripts\` folder would put reverse-engineering, Magic Utilities capture, state-flip, and ETW one click from a normal user.
- Allowlisted names + “if present” means a published exe without a checkout stays a battery tray. A bench checkout next to `bin\` gets Capture / Diagnose / Snapshot.

## Files

| File | Role |
| --- | --- |
| `docs/DESIGN-diagnostics-menu.md` | This design. |
| `MagicMouseTray/DiagnosticScripts.cs` | Names, labels, `Find`, snapshot-or-devmgr, `powershell -File` start info. No WinForms. No dump text. |
| `MagicMouseTray/TrayApp.cs` (`BuildMenu` Diagnostics) | Keep Test notification, Open logs, Open diagnostics folder. Add allowlisted items when `Find` returns a path. |
| `MagicMouseTray.Tests/DiagnosticScriptsTests.cs` | Fake tree: finds `capture-state.ps1`; root `diagnose-driver.ps1`; snapshot preferred; does not invent a dump format. |

## Tests

WindowsDesktop, resolver only. No live HID. No `Process.Start`.

- Fake tree with `scripts/capture-state.ps1` and a nested `bin\...` start dir → `Find("capture-state.ps1")` returns that file.
- `diagnose-driver.ps1` at fake repo root is found from the nested start dir.
- Next-to-start `capture-state.ps1` is found without a `scripts\` folder.
- Both snapshot and devmgr present → stack-dump path is the snapshot, not devmgr.
- Snapshot missing, devmgr present → stack-dump path is devmgr.
- Missing name → null. Allowlist does not include mm-rev-eng, mm-magicutilities-capture, mm-state-flip, or ETW scripts.
- `StartInfo` is `powershell.exe` with `-File` and the script path. Arguments do not mention `driver-state.txt`.
- Labels are exactly `Run capture-state.ps1`, `Run diagnose-driver.ps1`, `Run mm-bt-stack-snapshot.ps1`, `Run mm-devmgr-dump.ps1`. No `PATH-A` in labels.

## Non-goals

- `DriverStateDump.cs`, `driver-state.txt`, or any C# health/battery formatter.
- Bundling lab scripts with the published exe.
- PATH-A, enable/alerts, mm-rev-eng, mm-magicutilities-capture, ETW, wheel counter, mm-state-flip.
- Push. Changing Classify.
