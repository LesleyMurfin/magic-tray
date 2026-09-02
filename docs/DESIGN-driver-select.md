# DESIGN — 0323 driver select (KMDF / Patched Apple / Stock)

Status: Proposed. V1 design only — no C# in this vertical.

Today the v3 Driver submenu is KMDF (click installs) + Stock (display-only, disabled). `Classify` treats `applewirelessmouse` on 0323 as leftover `StockKmdf`. PATH-A install is a ship-blocker. `V3RecycleManager` (fecd365) is in-tree and unused. This design makes three lasting driver choices, and a Scroll vs Battery switch only while Patched Apple is bound.

## User flow

v3 row → **Driver:** `<badge>`:

```
KMDF (recommended)
Patched Apple driver
Stock Windows
```

Exactly one radio checked from `Classify`. Labels above are the only user-visible choice names. No `PATH-A`, no `Install-MagicMousePatch`, no `applewirelessmouse` in menu text.

| Click | What happens |
| --- | --- |
| **KMDF** | Existing `OfferV3KmdfInstallAsync`. MessageBox first: self-signed; Test Mode on; Memory Integrity (HVCI) off. Cancel aborts. Never falls back to the patched Apple installer. |
| **Patched Apple driver** | User-initiated only (V5). Same honesty: patched Apple `.sys` is not WHQL after patch; Test Mode on; HVCI off; scroll and battery are mutually exclusive. Cancel aborts. Never runs from poll / `Classify` / startup / missing KMDF. |
| **Stock Windows** | User-initiated unbind (V5): leave `HidBth`, no KMDF bind, no Apple filter on 0323. Not a Test Mode path. Not `FLIP:NoFilter` (that is a PathA mode, not a lasting stock choice). |

When the checked radio is **Patched Apple driver**, two more radios (V4), still under Driver:

```
Scroll
Battery
```

Sticky mode, not the old idle recycle loop.

- **Scroll** → `V3RecycleManager.SubmitFlipAndWait(FlipPhase.AppleFilter)` (Mode B: scroll works, battery N/A).
- **Battery** → `SubmitFlipAndWait(FlipPhase.NoFilter)` (Mode A: battery readable, scroll broken).

Do not call `ExecuteRecycleCycle`. Do not start `RunLoop`. Checkmarks from live `IsV3InModeA` / `IsV3InModeB` (expose as `internal` if needed; do not change the flip).

KMDF keeps native scroll+battery. Global **Battery reads** stays visible iff `PatchedKmdf` (today’s rule). Hidden for PathA (the Scroll/Battery radios replace it) and Stock.

v1/v2 Fix scroll and keyboard Fix battery reads are unchanged.

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
| `NotBound` | none — KMDF still recommended |

`NotBound` is not a fourth choice. `RecommendedLabel` nags KMDF only for 0323 `NotBound`. `PathAPatched` and `StockKmdf` are valid choices, not defects. `V3Badge`: `PatchedKmdf` → `KMDF`; `PathAPatched` → `Patched Apple`; `StockKmdf` → `Stock`; else `Not bound`.

V2 does not touch `Aggregate` or `IconAttention`. `PathAPatched` is not a worst-state, so Aggregate stays `Ok` for a bound PathA 0323; 0323 attention stays `UnknownAppleMouse` / `Error` only.

## Verticals (V2–V5)

V2 is **only** the enum + `Classify` / `PreferredBoundName` + tests. No menu, no installer, no recycle.

| Vertical | Files | Change |
| --- | --- | --- |
| **V2** Classify | `MagicMouseTray/DriverHealthChecker.cs`; `MagicMouseTray.Tests/DriverHealthCheckerTests.cs` | Add `PathAPatched`. Prefer Apple filter name on 0323 when KMDF is absent. Classify that name as `PathAPatched`. Invert today’s `Classify_0323_AppleWirelessMouse_IsNotPatched_NotPathAOk`. |
| **V3** menu radios | `MagicMouseTray/TrayApp.cs` (`TrayMenu` + `BuildDeviceRow` Driver submenu); `MagicMouseTray.Tests/TrayMenuTests.cs` | Three radios, easy labels, checked from status. KMDF click stays `OfferV3KmdfInstallAsync`. PathA/Stock radios display+check only; clicks land in V5. Drop the extra orange “Install recommended KMDF…” item — the KMDF radio is the install. Keep `ShowFixScroll` false for all 0323 statuses including `PathAPatched`. |
| **V4** mode switch | `MagicMouseTray/TrayApp.cs`; `MagicMouseTray/V3RecycleManager.cs` (expose current mode only); `MagicMouseTray.Tests/TrayMenuTests.cs` | Scroll/Battery radios iff `PathAPatched`. Call existing `SubmitFlipAndWait`. Do not edit `FlipPhase`, `WriteRequest`, `StartTask`, `C:\mm-dev-queue`, or `MM-Dev-Cycle`. Do not use `EnableV3Recycle` / idle loop. No new config key. |
| **V5** publish | `MagicMouseTray/DriverInstaller.cs`; `MagicMouseTray/DriverPackageCatalog.cs`; `MagicMouseTray.Tests/DriverPackageCatalogTests.cs`; `MagicMouseTray/TrayApp.cs` (PathA/Stock clicks); `README.md`; `CONTRIBUTING.md` | User-initiated Patched Apple offer (`v1-binary-patch/installer/Install-MagicMousePatch.ps1` from the existing v3-fix zip — catalog constant, never KMDF fallback). User-initiated Stock restore. Honest Test Mode MessageBox on PathA (same facts as KMDF). README: three choices; Test Mode for KMDF **and** Patched Apple, not Stock. CONTRIBUTING: PathA must not be silent and must not be a KMDF fallback; user-initiated offer is allowed. |

## Reuse vs rewrite

Reuse:

- `V3RecycleManager` flip protocol as committed (fecd365): `FlipPhase.NoFilter` / `AppleFilter`, `SubmitFlipAndWait`, queue files, `MM-Dev-Cycle`. Do not redesign.
- `OfferV3KmdfInstallAsync` + its Test Mode / HVCI MessageBox.
- `DriverInstaller` never-silent pattern; `IsPathAInstaller` still identifies the patch script so KMDF lookup cannot pick it.
- `DriverPackageCatalog` as the only package URL/name source.
- `Classify` / `PreferredBoundName` signatures (`appleFilterPackagePresent` / `kmdfPackagePresent` stay).

Rewrite (small):

- 0323 Apple filter: leftover `StockKmdf` → first-class `PathAPatched`.
- 0323 `PreferredBoundName`: stop ignoring `applewirelessmouse` LowerFilters when KMDF is not bound.
- Driver submenu: KMDF + disabled Stock → three radios; PathA grows Scroll/Battery.

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
- `RecommendedLabel` 0323: non-null only on `NotBound`; null on `PatchedKmdf`, `PathAPatched`, `StockKmdf`.

**V4**:

- Helper: Scroll/Battery switch visible iff v3 + `PathAPatched`; hidden for `PatchedKmdf` / `StockKmdf`.
- No tests that reimplement flip timing or queue files.

**V5**:

- Catalog PathA script path contains `Install-MagicMousePatch` / `v1-binary-patch`; KMDF path still does not.
- `FindKmdfOneClick` still refuses PathA-only trees and never returns the patch script.
- `IsPathAInstaller` still true for the patch script (used to block KMDF fallback, not to block the dedicated PathA offer).

## Non-goals

- This vertical: any C#, tests, or restore besides this file.
- Silent / poller / startup PathA or Stock.
- KMDF-missing → PathA fallback.
- Redesigning `V3RecycleManager` flip, queue, or `MM-Dev-Cycle`.
- Idle auto-recycle (`EnableV3Recycle` / `RunLoop`) as the Scroll vs Battery UX.
- `PATH-A` in menu labels.
- v1/v2 tealtadpole, keyboard PATH-C, Magic Utilities.
- Changing `Aggregate` / `IconAttention` / `DeviceCapability` in V2.
- New config keys.
