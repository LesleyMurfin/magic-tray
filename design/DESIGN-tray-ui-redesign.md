# DESIGN — Tray Menu Redesign: Per-Device Driver Clarity + Auto-Remediation

Status: Implemented.

## BUILD notes (issue #68)

- Product name in the UI is **Magic Tray**. Battery + driver-fix actions only (no gestures, trackpad custom clicks, or media-key remaps).
- Driver actions are user-initiated via `DriverInstaller` (never silent rebind). Auto-remediation from the original proposal is confirm-first / user-initiated.
- v1/v2 `[Fix scroll]` when `NotInstalled`/`NotBound`.
- Keyboard `[Fix battery reads]` on sentinel `-2`.
- v3 (0323) badge `Patched` / `Stock` / `Unknown` from `DriverHealthChecker.GetPerDeviceStatus()`. LowerFilters `NotBound` is **not** a v3 scroll warning. `PatchedKmdf` = bound `MagicMouseDriver`.
- `Battery reads` is visible iff a v3 mouse is connected **and** status is `PatchedKmdf`. It is not a recycle toggle. `V3RecycleManager` is gone.
- `UnknownAppleMouse` is scoped to that row. No unscoped global driver warning.
- Layout: devices / separator / settings / separator / Refresh Now, Diagnostics, Quit.
- Driver package URLs live in `DriverPackageCatalog` only — the tray does not triplicate them.


## Problem

Current tray menu (live screenshot, 2026-07-29) is a flat, undifferentiated list:
`Devices (3)` → global settings (Low Battery Threshold, Start with Windows) → an
unscoped `⚠ Unknown mouse model — check for app update` warning not tied to any
listed device → an always-visible `Battery Reads [Off]` toggle → action buttons.

Three concrete problems:
1. No per-device driver visibility — user can't tell which device uses which
   driver, or whether it's the recommended one.
2. The driver-health warning is global/ambiguous instead of attached to the
   specific device it's about.
3. `Battery Reads` is shown unconditionally, but it only does anything for the
   v3 Magic Mouse's patched driver — meaningless noise for v1 and for stock/KMDF v3.

## Current driver-detection code (verified, not assumed)

- `DriverHealthChecker.cs` already computes a per-scan `DriverStatus`: `Ok /
  NotInstalled / NotBound / UnknownAppleMouse / Error`, keyed off BTHENUM PID
  scan (`KnownPids = 030d, 0269, 0310, 0323`) against the `AppleWirelessMouse`
  filter driver service. This is the v1 scroll-fix driver. It already excludes
  non-scroll Apple HID devices (`NonScrollApplePids`: trackpads, keyboards) from
  false-positive warnings — the mechanism this design's "unscoped warning" bug
  should route into per-device instead of a global toast.
- `DeviceCapability.cs` / `DeviceRegistry.cs` — existing per-device capability
  model; this design extends it with a `DriverInfo` field, not a new registry.
- v3 (Magic Mouse 2024) driver options live in `apple-peripherals/magic-mouse-v3-fix/`:
  `v1-binary-patch` and `v2-kmdf-driver` (our patched driver) vs. Windows' stock
  HID/KMDF driver. **Open question for implementation**: this repo does not yet
  expose a single runtime API that reports "which of {stock, v2-kmdf-driver} is
  currently bound for v3" — the BUILD milestone must add one (likely a service/
  driver-name registry lookup analogous to `DriverHealthChecker.GetStatus()`,
  scoped to the v3 PID). Do not assume the mechanism; verify against the driver
  package's actual install/service name before implementing the quick-switch.

## Design

### Per-device rows (replaces flat `Devices (3)` list)

Each device row shows: name, battery (if applicable), and a driver badge:

```
🖱  Magic Mouse (v1)        73%   [Fix scroll driver]      <- driver: NotBound/NotInstalled
🖱  Magic Mouse 2024 (v3)   —     Driver: Patched ▾        <- quick-switch dropdown
⌨  Apple Wireless KB 2011  25%
⌨  Apple Wireless KB 2011  --    [Fix battery reads]       <- battery read blocked (-2)
```

- v1 row: if `DriverStatus != Ok`, show an inline `[Fix scroll driver]` action
  button on that row (not a separate global warning). Clicking it runs the
  existing driver-install path (the fix already implemented per commits
  dc755bd/a353e0c/1dd0f36/b269d5d) and re-checks status on completion.
- v1 row auto-remediation trigger: on poll/refresh, if `DriverStatus in
  {NotInstalled, NotBound}` AND the device is a known v1 PID (`030d`/`0310`),
  auto-run the fix script without requiring the button click, then surface a
  toast on success/failure. (User's ask: "if scrolling or battery doesn't work,
  it gets the right driver" — auto, not just a manual button. Button remains as
  a manual re-trigger / opt-out affordance — exact UX for auto vs. confirm-first
  is a call for whoever builds this to make with Lesley, not decided here.)
- Keyboard row (Apple Wireless Keyboard, A1314): battery read already has a
  code-level signal for this exact case — `KeyboardBatteryDevice.cs` returns
  sentinel `-2` ("present but blocked — patch needed") when the device
  responds but the Feature-report battery cap isn't exposed yet, distinct
  from `-1` (device not found/not paired, no action shown). When the reading
  is `-2`, show an inline `[Fix battery reads]` action on that row — same
  placement/pattern as the v1 mouse's `[Fix scroll driver]` button. Clicking
  it runs the existing `scripts/kbd-patch-cachedservices.ps1` (patches the
  BTHPORT `CachedServices` SDP cache to expose RID `0x47` as a Feature
  report), then re-checks the reading. This script requires elevation
  (`#Requires -RunAsAdministrator`, writes `HKLM\SYSTEM\...\BTHPORT`) — the
  button triggers a UAC prompt; per the SPEC's decided elevation model
  (`SPEC-magic-tray-keyboard-battery.md`), a one-time elevated install
  additionally registers a SYSTEM-scheduled task (M-KB3, not yet built) so
  this runs automatically after an un-pair/re-pair erases the patch, without
  needing the button. The tray-side manual button is this design's
  contribution; the scheduled-task auto-trigger is out of scope here (M-KB3
  is its own build item).
- v3 row: always shows which driver is bound — `Patched` or `Stock/KMDF` (or
  `Unknown` if detection can't resolve it) — plus a quick-switch control
  (dropdown or toggle) to flip between the two installed options.
- `UnknownAppleMouse` status (new/unrecognized Apple PID) renders as its own row
  with the existing global warning text, but now scoped to that specific device
  entry instead of floating unattached at the bottom of the menu.

### `Battery Reads` — conditional visibility

Rule: show the `Battery Reads` toggle **if and only if** a v3 Magic Mouse (2024)
is connected **and** its currently-bound driver is the patched one
(`v2-kmdf-driver`). Hidden in every other state: no v3 present, v3 present on
stock/KMDF, v3 present with unresolved/unknown driver. This is a single derived
boolean (`showBatteryReadsToggle = v3Connected && v3Driver == Patched`),
recomputed on every device-list refresh — not a static settings-menu item.

### Layout grouping (fixes the "no visual separation" problem)

1. Device rows (as above), each self-contained with its own status/actions.
2. Separator.
3. Global settings: Low Battery Threshold, Start with Windows, (conditionally)
   Battery Reads.
4. Separator.
5. Actions: Refresh Now, Read Battery Now, Test Notification, Open Logs, Open
   Diagnostics Folder, Quit.

## Out of scope / explicitly not decided here

- Whether v1 auto-remediation runs silently or asks for confirmation first.
- The exact API/service-name check for v3 driver identification (BUILD-time
  investigation, not a design assumption).
- Visual/XAML styling specifics — this design specifies structure and logic,
  not pixel layout.
- M-KB3 (scheduled-task auto-re-patch on un-pair/re-pair) — tracked in the
  SPEC, not part of this UI design; this design only adds the manual
  `[Fix battery reads]` button.

## Related

- `docs/SPEC-magic-tray-keyboard-battery.md` — existing device/capability spec,
  this design extends rather than replaces it. Also the source of the
  `-2` sentinel meaning and the `kbd-patch-cachedservices.ps1` elevation model.
- Keyboard battery-blocked sentinel: `MagicMouseTray/KeyboardBatteryDevice.cs`
  (`-2` = present but blocked, `-1` = not found). Fix script:
  `scripts/kbd-patch-cachedservices.ps1`.
- v1 driver fix history: dc755bd, a353e0c, 1dd0f36, b269d5d.
- v3 driver options: `apple-peripherals/magic-mouse-v3-fix/{v1-binary-patch,v2-kmdf-driver}`.
