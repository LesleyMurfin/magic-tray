# DESIGN — Where trackpad tap / scroll live (existing driver, not tray)

Status: Placement only. No new `.sys` / INF in magic-tray. Do not edit `TrayApp.cs` this vertical. Do not recommend `TrackpadPtp.c`.

**Recommendation: existing tealtadpole Boot Camp trackpad filter for v1 (`030E`) only, offered like `OfferV1V2ScrollFix` (open GitHub, user right-clicks the INF). Not the tray exe. Not a new Magic Tray driver. Not KMDF PTP.**

| Feature | Lives in | One sentence |
|---|---|---|
| Trackpad 1/2/3-finger tap | **Existing driver** — tealtadpole `AppleWirelessTrackpad.inf` (`030E` only) | Tray offer clones `OfferV1V2ScrollFix`: open the GitHub repo; user right-clicks the INF. Catalog-signed `applewtp`; Test Mode not required. No `0265`/`0324` installer exists — do not invent one. |
| Trackpad two-finger / “smooth” scroll | **Existing driver** — same `applewtp` filter (`030E` only) | tealtadpole README: vertical and horizontal scroll after that INF; tray `SendInput` is not a pointer device; MU pixel scroll stays Out. |
| Mouse surface scroll (0323) | **Existing driver** — `v2-kmdf-driver/Install-KMDF.cmd` + `GestureEngine.c` | Ordinary HID Wheel / AC Pan already; tray confirm-first KMDF offer. v1/v2 mice stay Boot Camp `AppleWirelessMouse.inf`. |

Magic Keyboard stays on existing SDP scripts (no kernel keyboard driver). Trackpad 2 (`0265`) and Trackpad 2024 (`0324`): **no offer** — no existing installer.

## Why the tray cannot be a correct Windows pointer

`MagicMouseTray` is a session-1 WinForms app. It already opens HID handles only to **read battery**. It is not on the HID device stack.

Putting tap or scroll in the exe would mean `SendInput` / `mouse_event` or a userland `ReadFile` of Apple touch frames. That is not a Windows pointer device:

1. **Not on the HID stack.** Windows pointer I/O needs a bound HID lower filter (`applewtp` / `applewirelessmouse` / KMDF) or a hidclass collection. `SendInput` injects synthetic mouse messages. Settings never see a touchpad.

2. **Fights mouhid / hidclass.** COL01 is already held by the HID stack (`OPEN_FAILED err=5` in the README). Exclusive userland `ReadFile` steals reports from `mouhid` or fails.

3. **Focus, UIPI, UAC.** `SendInput` from a medium-IL tray does not reach elevated windows or the Secure Desktop. Taps and wheel would work in Notepad and fail in an elevated terminal.

4. **No Secure path.** Injected input is synthetic. Kernel HID reports from a bound filter are a first-class mouse or touchpad device.

5. **Lifetime.** Kill the tray and pointer I/O dies. A bound INF stays until the user uninstalls it.

A 2026-04 PSN note (H-007) once listed a Phase 4A userland `SendInput WM_MOUSEWHEEL` daemon as a *possible* 0323 path. That experiment lost: 0323 scroll shipped as KMDF `TranslateMouse2ToHid`. Do not revive it inside `MagicMouseTray`.

## Existing packages to reuse (do not vendor, do not rewrite)

Same catalog pattern as mice and keyboards: other-repo path + user confirms. Magic-tray does not grow `.sys` source.

### Mice (already offered)

| What | Repo / path | Binds | Notes |
|---|---|---|---|
| 0323 KMDF scroll | [LesleyMurfin/magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) `v2-kmdf-driver/Install-KMDF.cmd` | `PID&0323` only | `GestureEngine.c` maps `0x12` `TOUCH_STATE_DRAG` → Wheel / AC Pan. Ordinary wheel, not MU pixel scroll. Published scroll INF (`MagicMouseDriver-kmdf-204-scroll.inf`) is 0323-only. |
| 0323 Patched Apple | same zip `v1-binary-patch/installer/Install-MagicMousePatch.ps1` | 0323 | User-initiated; not a KMDF fallback. |
| v1/v2 Boot Camp | [tealtadpole/MagicMouse2DriversWin11x64](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) `AppleWirelessMouse/AppleWirelessMouse.inf` | `030D`, `0310`, `0269` | Catalog-signed `applewirelessmouse`. Tray today **opens the GitHub page** (`OfferV1V2ScrollFix`); does not `pnputil`. |

Constants: `MagicMouseTray/DriverPackageCatalog.cs`.

### Magic Keyboard (already offered)

| What | Path | Notes |
|---|---|---|
| PATH-C SDP cache patch | `scripts/kbd-patch-cachedservices.ps1` + `scripts/Install-KeyboardBattery.cmd` | Registry patch so Windows can poll battery. **No `.sys`.** Requires `-Mac`. |

### Magic Trackpad v1 — existing installer (the only one)

| | |
|---|---|
| Repo | [tealtadpole/MagicMouse2DriversWin11x64](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) **master** |
| INF | `AppleWirelessTrackpad/AppleWirelessTrackpad.inf` |
| Catalog | `applewtp.cat` |
| Binary / service | `AppleWTP.sys` / **`applewtp`** |
| Hardware ID | `BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&030e` only |
| DriverVer | 06/21/2018,6.1.7000.0 |
| Test Mode | **Not required** (catalog-signed Boot Camp, same as v1 mouse) |
| User flow | Same as v1 mouse `OfferV1V2ScrollFix`: open the GitHub page; user downloads `AppleWirelessTrackpad/`; right-click `AppleWirelessTrackpad.inf` → Install. Tray does not `pnputil`. Never silent / poll / startup. |

That INF’s hardware ID list is a single row. It does **not** name `0265` or `0324`. A comment says new PIDs go in `AppleWirelessTrackpad.NT.AddReg`; none were added.

### Trackpad 2 / 2024 — no existing installer

Searched: tealtadpole `AppleWirelessTrackpad.inf` (`030E` only); published 0323 KMDF (`Install-KMDF.cmd` / `MagicMouseDriver-kmdf-204-scroll.inf`, `0323` only); `DriverPackageCatalog` (no trackpad URL). **No `0265` / `0324` installer anywhere.**

Do **not** recommend `v2-kmdf-driver/TrackpadPtp.c`, unshipped KMDF `1c7b8e0`, a Magic Tray PTP `.sys`, or new hardware IDs. `docs/DESIGN-trackpad-tap.md` is not a magic-tray deliverable.

MU-style pixel / smooth scroll, custom areas, silent click, 3-finger **drag**, and desktop swipes stay **Out** (`docs/DESIGN-mu-free-parity.md`).

## Tray offer (v1 trackpad only)

Clone `OfferV1V2ScrollFix` for Magic Trackpad v1 (`030E` / `MagicTrackpadV1`) only:

- Open `https://github.com/tealtadpole/MagicMouse2DriversWin11x64` (same host as v1 mouse; trackpad folder is `AppleWirelessTrackpad/`).
- User right-clicks `AppleWirelessTrackpad.inf`. Test Mode not required.
- `0265` / `0324` rows stay battery-only (`docs/DESIGN-trackpad.md`): no Driver submenu, no KMDF, no fake INF.

This DESIGN vertical does not land C#. When the offer is wired, it is that clone — not `Install-KMDF.cmd`, not `TrackpadPtp.c`.

## Files

| File | Role |
|---|---|
| `docs/DESIGN-trackpad-where.md` | This placement decision. |
| `docs/DESIGN-trackpad.md` | Battery rows; `0265`/`0324` stay without radios. |
| `docs/DESIGN-mu-free-parity.md` | MU pixel/smooth scroll Out. |
| `MagicMouseTray/DriverPackageCatalog.cs` | Existing mouse + keyboard URLs (do not edit this vertical). |
| tealtadpole `AppleWirelessTrackpad/AppleWirelessTrackpad.inf` | Existing `030E` installer (`applewtp` / `applewtp.cat`). |
| `v2-kmdf-driver/GestureEngine.c` | Live 0323 touch→wheel (other worktree; evidence). |

## Non-goals

- C#, `TrayApp.cs`, tests, publish, push, new `.sys` / INF in magic-tray.
- `TrackpadPtp.c` / `1c7b8e0` / inventing `0265`/`0324` hardware IDs.
- Vendoring Magic Utilities, `MagicMouse.sys`, `MagicTrackpad.sys`, or Boot Camp binaries into this repo.
- Silent `pnputil` of `AppleWirelessTrackpad.inf`.
- Claiming MU pixel scroll or Test Mode for this catalog-signed trackpad INF.
