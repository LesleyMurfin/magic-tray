# DESIGN — 0323 driver select (KMDF / Patched Apple / Stock)

Status: V1 design, now partly built. Three slices implement it: `DriverAdvisor` (the advice line), `ModeFlip` (the Mode A/B battery flip) and `SystemConfigChecker` (configuration facts). Sections headed "as built" describe shipped behaviour; the rest is still design.

Starting point: the v3 Driver submenu was KMDF (click installs) plus Stock (display-only, disabled), `Classify` treated `applewirelessmouse` on 0323 as leftover `StockKmdf`, and PATH-A install was a ship-blocker. This design makes three lasting driver choices, and - only while Patched Apple is bound - a momentary Mode A/B flip that fetches one battery reading and puts the mouse straight back.

## User flow

v3 row → **Driver:** `<badge>`:

```
KMDF
Patched Apple driver
Stock Windows
```

Exactly one radio checked from `Classify`. Labels above are the only user-visible choice names. No `PATH-A`, no `Install-MagicMousePatch`, no `applewirelessmouse` in menu text.

The recommendation is not baked into a radio label. `DriverAdvisor.RecommendedFor(kind, pid)` marks exactly one `DriverOption` as `Recommended`, and `DriverAdvisor.AdviceLine(kind, pid, status)` renders one short line the tray shows next to the choices. That line is defined for every driver state, not only `NotBound`: `IsOnRecommended(kind, pid, status)` tells the tray whether the bound driver already is the recommended one, so a correct setup gets a confirmation rather than a nag. `AdviceLine` returns null only where there is no driver story to tell for the device, and then nothing is shown.

As built, that line exists in two lengths, because a `ToolStripItem` does not wrap and a paragraph handed to `.Text` draws a row the width of the screen: the Driver submenu draws `DriverAdviceView.AdviceShort` (one line, composed from `RecommendedFor` / `CurrentOptionId`, under a hard character cap the tests walk exhaustively), and the dialog behind that row draws `AdviceFull`, which is `DriverAdvisor.AdviceLine`'s paragraph verbatim.

`OptionsFor(kind, pid)` is the single source of the per-choice expectations. Each `DriverOption` carries `Pointer`, `Scroll` and `Battery` as a `CapabilityExpectation` - `Works`, `Dead`, `EitherOrOnly`, `NotApplicable` or `Unknown` - plus a `Why`. `EitherOrOnly` is the Patched Apple case: scroll and battery each work, but never at the same moment. These are predictions for a choice, not observations of the present; the live per-capability reads stay where they are (`docs/ENABLE-DISABLE.md`).

| Click | What happens |
| --- | --- |
| **KMDF** | Existing `OfferV3KmdfInstallAsync`. MessageBox first: self-signed; Test Mode on; Memory Integrity (HVCI) off. Cancel aborts. Never falls back to the patched Apple installer. |
| **Patched Apple driver** | User-initiated only (V5). Same honesty: patched Apple `.sys` is not WHQL after patch; Test Mode on; HVCI off; scroll and battery are mutually exclusive. Cancel aborts. Never runs from poll / `Classify` / startup / missing KMDF. |
| **Stock Windows** | User-initiated unbind (V5): leave `HidBth`, no KMDF bind, no Apple filter on 0323. Not a Test Mode path. Not `FLIP:NoFilter` (that is a PathA mode, not a lasting stock choice). |

KMDF keeps native scroll and battery together and needs no flip. Stock Windows has neither our filter nor a flip. v1/v2 keep their own two radios, **Boot Camp** and **Stock Windows**, which is where the old orange Fix scroll item went - `TrayMenu.ShowFixScroll` is now false for every kind and status - and the keyboard **Fix battery reads** offer is unchanged.

## Mode A/B battery flip, as built (Patched Apple only)

Mode A is the v3 HID path with `col02` present: the battery report is readable and scroll is dead. Mode B is the unified path with no `&col0x` collection: scroll works and the battery is unreadable. On the patched Apple driver the two are mutually exclusive, so no configuration gives both.

The V4 sticky Scroll / Battery pair is gone. Mode B is the only resting state, and a battery reading is fetched by a momentary flip: `ModeFlip.ReadBatteryViaFlip(readPercent, timeoutMs = 45000)` goes to Mode A, reads once, and puts the mouse back. Nothing in the shipped API parks the mouse in Mode A on purpose.

One cycle, one UAC prompt:

1. Before elevating, the tray writes a sentinel (`%APPDATA%\MagicMouseTray\mode-flip.sentinel`, the directory `Logger` already uses): schema version (`v=`), start time (`started=`, which doubles as the cycle's nonce), PID, and for every `Enum\BTHENUM` key the cycle will touch a `key=` line followed by a `present=` flag and one `filter=` line per name in that key's previous `LowerFilters` value - so "value absent" stays distinct from "value empty", and the value's order is recorded as written, with nothing sorted or deduplicated.
2. One elevated PowerShell run owns the whole cycle - flip to Mode A, hold, restore Mode B - so the user approves once, not twice.
3. That script reports phases through a status sidecar in `%TEMP%` named after the cycle, not just the device - `mm-modeflip-0323-<nonce>.status`, the nonce being the sentinel's own start timestamp - appends `ready` once Mode A is confirmed by `col02` being present, then blocks waiting for the matching `mm-modeflip-0323-<nonce>.done`. Per-cycle names are the point: a transcript an earlier or overlapping cycle left behind can never be read as this one's, and a handshake file under this cycle's own name that will not clear refuses the cycle rather than polling a file it does not own.
4. The tray, unelevated, sees `ready`, calls the `readPercent` callback it was handed - the ordinary battery read, no second reader - and then writes the done file.
5. The same still-running elevated script restores Mode B from a PowerShell `finally`, so the restore runs even when the middle of the cycle fails.
6. The post-state is verified, never assumed, and the two halves verify different things. The elevated script appends `restored` only when its own re-read of `LowerFilters` matches what it recorded **and** the unified v3 HID path with no `&col0x` is back. The tray's verdict - the one the outcome and the sentinel hang on - is `CompareTargets` and nothing else: every recorded key re-read, the recorded value required back verbatim and in its **recorded order** (Windows loads `LowerFilters` in order), case-insensitive per name, and tri-state, so a recorded key that has gone or a hive that cannot be read is no evidence rather than success. Mode B is still measured and logged beside every verdict (`filters_match=`, `mode_b=`), and deliberately cannot outvote the value: the devnode re-enumerates on the Bluetooth stack's own schedule and was measured arriving after the registry was already correct, so gating on it would report a restore that did land as failed. The sentinel is deleted only on that comparison - never on an observed Mode B and never on the script's own `restored` token - plus the one case where the UAC prompt produced no elevated process at all, because then nothing was written.

Outcomes, on `ModeFlipResult(Outcome, Percent, RestoredToModeB, Detail)`:

| `ModeFlipOutcome` | Meaning |
| --- | --- |
| `Ok` | Mode A reached, a percent read, and the recorded `LowerFilters` value verified back. |
| `NotPathA` | The device is not bound to the patched Apple driver, so there is nothing to flip. Nothing runs. |
| `NoInstances` | No live Bluetooth instance for the PID to act on. Nothing runs. |
| `Cancelled` | The UAC prompt was declined: no registry write, no device restart, the sentinel deleted again, and the mouse never left Mode B. |
| `FlipFailed` | Mode A was not confirmed within `timeoutMs` (45 s by default - that budget is the `ready` handshake, not the whole cycle), or the cycle refused to start at all: a recorded filter name that is not a plain service name, a handshake file that would not clear, a sentinel that could not be written. A cycle that started still restores and is still verified; one that refused wrote nothing to restore. |
| `BatteryUnreadable` | Mode A was reached, but the read came back with no percent. The restore still runs and is still verified. |
| `RestoreFailed` | The one outcome where the recorded `LowerFilters` value could not be confirmed back. `RestoredToModeB` is false and scroll may stay dead until it is restored. |

The restore sits in the `finally` and is proved by a re-read rather than assumed from an exit code, so `RestoredToModeB` is the `CompareTargets` verdict and nothing else. Anything short of the recorded value back is reported as `RestoreFailed`, outranking even a successful reading, which is why `RestoredToModeB` is false for `RestoreFailed` by construction and true for `Ok`, `FlipFailed` and `BatteryUnreadable`. On the refusals that never elevate - `NotPathA`, `NoInstances`, a preflight `FlipFailed` - it carries what the tray could see of the untouched stack instead, because there was nothing to restore.

Crash safety is the sentinel's whole job. `ModeFlip.StaleModeAOnStartup()` is true while that file exists and parses, which means a previous cycle died before its restore was verified - a crash, a kill, a reboot mid-flip. `ModeFlip.RestoreModeB()` is the recovery: its own single UAC prompt, the same verified re-read, and the sentinel removed only on success. That is the one-click restore the tray can offer at the next startup.

What the user sees, as built:

- **One action item** on the device's **Driver:** submenu, `ModeFlipView.MenuItemLabel()` - "Read battery now - stops the mouse for 10-20 seconds..." - and not a pair of sticky Scroll / Battery radios. There is no state the user is left parked in, so there is nothing for a checked radio to mean.
- **One OK/Cancel consent dialog** before anything is elevated, `ModeFlipView.OfferText(pid)`: the 10-20 second pause in pointer and scrolling (the figure the hardware test measured), the single administrator approval that covers the whole reading, and the promise that the tray always puts the mouse back and checks that it did. Cancel elevates nothing, writes nothing and persists no driver choice - the old sticky pair saved the choice before the flip was even attempted, so a silent failure left the config and the machine disagreeing.
- **One result dialog per cycle**, `TrayApp.ReportModeFlip` handing the result to `ModeFlipView.ResultText`, one branch per `ModeFlipOutcome`; only a verified restore is allowed to say the mouse is back in scroll mode.
- **A separate recovery item** on the same submenu, `ModeFlipView.RestoreItemLabel` - "Restore scroll mode" - present whenever this driver is bound, whether or not a flip has run, because every restore-failed sentence tells the user to hunt for exactly that wording.
- **A startup offer**, `ModeFlipView.StartupRestoreOffer()`, shown once per process while `ModeFlip.StaleModeAOnStartup()` is true: OK runs `RestoreModeB()`, Cancel leaves the offer for the next start.

The internal Mode A / Mode B names never reach the screen: the user-facing vocabulary is "battery mode" for the shape where the percent is readable and scroll is dead, and "scroll mode" for the resting shape.

### Removed: the `mm-dev-queue` protocol

Historical note, so nobody rebuilds it. The V4 design called `V3RecycleManager.SubmitFlipAndWait`, which wrote `C:\mm-dev-queue\request.txt` and ran `schtasks /run /tn "MM-Dev-Cycle"`. That scheduled task exists on one developer machine and nowhere else, so on every other install both mode clicks did nothing at all - and because the return value was discarded there was not even a toast to say so. It was a developer-machine artifact, not a feature, so it is deleted rather than fixed. Do not reintroduce a queue directory, a `request.txt` / `result.txt` pair, or a scheduled task: the flip is an in-tray elevated run with a sentinel and a verified post-state.

## Configuration facts, as built

`SystemConfigChecker.Check(kind, pid, status)` reports the state of the machine around the driver the device is on, as a list of `ConfigFact(Id, Title, Severity, Detail, ActionLabel, ActionUrlOrScript)`. `ConfigSeverity` is `Ok`, `Advisory` or `Blocking`: `Blocking` is a condition that stops the current driver from working at all, `Advisory` is worth knowing but not fatal, and `Ok` is a positive confirmation that something the driver needs is already in place. A fact carries `ActionLabel` and `ActionUrlOrScript` only when there is something concrete to offer. The checker reads and reports; it changes nothing by itself.

## States

`DriverStatus` today: `Ok`, `NotInstalled`, `NotBound`, `UnknownAppleMouse`, `Error`, `StockKmdf`, `PatchedKmdf`.

V2 adds one value: **`PathAPatched`**.

0323 `PreferredBoundName(pid, service, filters)` — KMDF still wins if both are present:

1. `MagicMouseDriver` in Service or LowerFilters → `MagicMouseDriver`
2. else `applewirelessmouse` in LowerFilters (or Service) → `applewirelessmouse`  *(today this step is skipped; 0323 Apple filters are ignored)*
3. else Service (`HidBth`) or null

0323 `Classify` after that bound name:

| Bound name | Package | Status |
| --- | --- | --- |
| `MagicMouseDriver` | — | `PatchedKmdf` |
| `applewirelessmouse` | — | `PathAPatched` |
| `HidBth` (or other non-KMDF/Apple service) | — | `StockKmdf` (even if KMDF leftover on disk) |
| null/empty | `kmdfPackagePresent` | `NotBound` |
| else (`HidBth` / none, no KMDF package) | — | `StockKmdf` |

Missing Apple LowerFilters is still not `NotBound` for 0323. Leftover `MagicMouseDriver.sys` on disk is not `NotBound` while `HidBth` is bound.

Radio checked:

| Status | Radio |
| --- | --- |
| `PatchedKmdf` | KMDF |
| `PathAPatched` | Patched Apple driver |
| `StockKmdf` | Stock Windows |
| `NotBound` | none - the advice line still recommends KMDF |

`NotBound` is not a fourth choice. `PathAPatched` and `StockKmdf` are valid choices, not defects - but the advice line is present in every 0323 state, not only `NotBound`: `DriverAdvisor.AdviceLine` speaks on `PatchedKmdf`, `PathAPatched`, `StockKmdf` and `NotBound` alike, confirming the setup when `IsOnRecommended` is true and naming the trade-off when it is not. `V3Badge`: `PatchedKmdf` -> `KMDF`; `PathAPatched` -> `Patched Apple`; `StockKmdf` -> `Stock`; `Error` -> `Error`; else `Not bound`.

V2 does not touch `Aggregate` or `IconAttention`. `PathAPatched` is not a worst-state, so Aggregate stays `Ok` for a bound PathA 0323; 0323 attention stays `UnknownAppleMouse` / `Error` only.

## Verticals (V2–V5)

V2 is **only** the enum + `Classify` / `PreferredBoundName` + tests. No menu, no installer, no recycle.

| Vertical | Files | Change |
| --- | --- | --- |
| **V2** Classify | `MagicMouseTray/DriverHealthChecker.cs`; `MagicMouseTray.Tests/DriverHealthCheckerTests.cs` | Add `PathAPatched`. Prefer Apple filter name on 0323 when KMDF is absent. Classify that name as `PathAPatched`. Invert today’s `Classify_0323_AppleWirelessMouse_IsNotPatched_NotPathAOk`. |
| **V3** menu radios | `MagicMouseTray/TrayApp.cs` (`TrayMenu` + `BuildDeviceRow` Driver submenu); `MagicMouseTray.Tests/TrayMenuTests.cs` | Three radios, easy labels, checked from status. KMDF click stays `OfferV3KmdfInstallAsync`. PathA/Stock radios display+check only; clicks land in V5. Drop the extra orange “Install recommended KMDF…” item — the KMDF radio is the install. Keep `ShowFixScroll` false for all 0323 statuses including `PathAPatched`. |
| **V4** mode flip | `MagicMouseTray/ModeFlip.cs` (new); `MagicMouseTray/V3RecycleManager.cs` (dev-queue protocol removed); `MagicMouseTray/TrayApp.cs` (wiring); tests alongside `ModeFlip` | User-initiated battery read on `PathAPatched` through `ModeFlip.ReadBatteryViaFlip`, plus the startup offer built on `StaleModeAOnStartup` / `RestoreModeB`. One elevated run per cycle, restore in a `finally`, sentinel, verified post-state. No sticky Mode A. No queue directory and no scheduled task. No new config key. |
| **V5** publish | `MagicMouseTray/DriverInstaller.cs`; `MagicMouseTray/DriverPackageCatalog.cs`; `MagicMouseTray.Tests/DriverPackageCatalogTests.cs`; `MagicMouseTray/TrayApp.cs` (PathA/Stock clicks); `README.md`; `CONTRIBUTING.md` | User-initiated Patched Apple offer (`v1-binary-patch/installer/Install-MagicMousePatch.ps1` from the existing v3-fix zip — catalog constant, never KMDF fallback). User-initiated Stock restore. Honest Test Mode MessageBox on PathA (same facts as KMDF). README: three choices; Test Mode for KMDF **and** Patched Apple, not Stock. CONTRIBUTING: PathA must not be silent and must not be a KMDF fallback; user-initiated offer is allowed. |

## Reuse vs rewrite

Reuse:

- Mode A/B mechanics only, as measured: removing the Apple filter name from the device's `LowerFilters` and restarting the device gives Mode A, re-adding it gives Mode B, Mode A is confirmed by `col02` being present and Mode B by the unified path with no `&col0x`. The transport that used to carry those steps is removed - see the "Removed" note above.
- `OfferV3KmdfInstallAsync` + its Test Mode / HVCI MessageBox.
- `DriverInstaller` never-silent pattern; `IsPathAInstaller` still identifies the patch script so KMDF lookup cannot pick it.
- `DriverPackageCatalog` as the only package URL/name source.
- `Classify` / `PreferredBoundName` signatures (`appleFilterPackagePresent` / `kmdfPackagePresent` stay).

Rewrite (small):

- 0323 Apple filter: leftover `StockKmdf` → first-class `PathAPatched`.
- 0323 `PreferredBoundName`: stop ignoring `applewirelessmouse` LowerFilters when KMDF is not bound.
- Driver submenu: KMDF plus a disabled Stock entry becomes three real choices, and on PathA the tray gains a user-initiated battery read rather than a sticky mode.

Do not revive idle recycle as UX. Do not treat `FLIP:NoFilter` as Stock.

## Tests per vertical

**V2** (`DriverHealthCheckerTests` only):

- `Classify("0323", "applewirelessmouse", …)` → `PathAPatched` (not `StockKmdf`, not `PatchedKmdf`, not `Ok`).
- `Classify("0323", "MagicMouseDriver", …)` still `PatchedKmdf` even if Apple package present.
- `Classify("0323", "HidBth", kmdfPackagePresent: false)` still `StockKmdf`.
- `Classify("0323", "HidBth", kmdfPackagePresent: true)` → `StockKmdf` (KMDF leftover on disk is not `NotBound` while HidBth is bound). `Classify("0323", null, kmdfPackagePresent: true)` → `NotBound`. Apple bound name is still `PathAPatched` even if KMDF sits on disk.
- `PreferredBoundName("0323", "HidBth", ["applewirelessmouse"])` → `applewirelessmouse`.
- `PreferredBoundName("0323", "MagicMouseDriver", ["applewirelessmouse"])` → `MagicMouseDriver` (KMDF wins).
- Existing v1 Apple-filter `Ok` / `NotBound` / `NotInstalled` cases unchanged.

**V3** (`TrayMenuTests`):

- `V3Badge(PathAPatched)` → `Patched Apple`.
- Radio copy is exactly `KMDF`, `Patched Apple driver`, `Stock Windows`.
- No user-visible string contains `PATH-A`.
- `ShowFixScroll` false for 0323 `PathAPatched`.

**Advisor** (`DriverAdvisor`):

- `DriverAdvisor.AdviceLine` 0323: non-null on `PatchedKmdf`, `PathAPatched`, `StockKmdf` and `NotBound`. Confirms when `IsOnRecommended` is true, names the trade-off when it is not.
- `OptionsFor` 0323 gives three options; Patched Apple reports `EitherOrOnly` for scroll and battery; Stock reports scroll `Dead`.

**V4** (`ModeFlip`):

- `StaleModeAOnStartup()` is true only while a parseable sentinel exists, and false once a restore has been verified.
- `ReadBatteryViaFlip` on a device that is not `PathAPatched` returns `NotPathA` and changes nothing.
- `MapOutcome` reports `RestoreFailed` for anything but a `true` `FiltersRestored`, including the tri-state `null`, and that outranks a percent that was read; `Ok`, `FlipFailed` and `BatteryUnreadable` are therefore only reachable with the recorded value verified back.
- `CompareTargets` is order-sensitive: the same names in a different order is `false`, a recorded key that is gone is `null`, and neither is success.
- No test reimplements flip timing, and none asserts a queue file or a scheduled task.

**V5**:

- Catalog PathA script path contains `Install-MagicMousePatch` / `v1-binary-patch`; KMDF path still does not.
- `FindKmdfOneClick` still refuses PathA-only trees and never returns the patch script.
- `IsPathAInstaller` still true for the patch script (used to block KMDF fallback, not to block the dedicated PathA offer).

## Non-goals

- Parking the mouse in Mode A as a sticky mode, or any background/idle auto-flip: the battery flip is user-initiated, and the only other entry point is the startup restore offer.
- Silent / poller / startup PathA or Stock.
- KMDF-missing → PathA fallback.
- Reviving the removed developer queue protocol in any form (see the "Removed" note above).
- `PATH-A` in menu labels.
- v1/v2 tealtadpole, keyboard PATH-C, Magic Utilities.
- Changing `Aggregate` / `IconAttention` / `DeviceCapability` in V2.
- New config keys.
