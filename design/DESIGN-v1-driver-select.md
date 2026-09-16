# DESIGN — v1/v2 driver select (Boot Camp / Stock Windows)

Status: V3 Implemented (Stock unbind). V2 menu radios Implemented.

Today the v1/v2 row shows **Fix scroll (Recommended)** only when `ShowFixScroll` is true (`NotInstalled` / `NotBound`). Live 030D `bound=applewirelessmouse` `status=Ok` takes the other branch: a disabled `Driver: applewirelessmouse` line and no choices. 0323 always shows lasting radios. This design gives v1/v2 the same pattern: two radios always visible, checked from `Classify`.

v1/v2 drivers are **not** KMDF and **not** PATH-A. Boot Camp is the tealtadpole **unpatched** Apple `applewirelessmouse` INF (catalog-signed, no Test Mode). Stock Windows is `HidBth` with no Apple filter.

## User flow

v1/v2 row (`030D`, `0269`, `0310` — `IsV1V2Mouse`) → **Driver:** `<badge>`:

```
Boot Camp
Stock Windows
```

Exactly one radio checked from `Classify`. Labels above are the only user-visible choice names. No `PATH-A`, no `applewirelessmouse`, no `tealtadpole`, no `KMDF` in menu text.

| Click | What happens |
| --- | --- |
| **Boot Camp** | Existing `OfferV1V2ScrollFix`. Opens the documented tealtadpole page. Catalog-signed Boot Camp INF; Test Mode is **not** required. Do not `pnputil` / rebind from the tray. Never falls back to a 0323 installer. |
| **Stock Windows** | User-initiated `OfferV1V2StockRestore(pid)`. MessageBox: stock Windows HID (`HidBth`); no Apple filter; Test Mode not required. Cancel aborts. Unbind `applewirelessmouse` LowerFilters on this v1/v2 PID only (BTHENUM + matching HID), then `pnputil /restart-device` those BTHENUM instances. Not `OfferV3StockRestoreAsync`. Never 0323 KMDF/PathA uninstall scripts. Never `FLIP:NoFilter`. Never silent / poll / startup. |

Drop the extra orange **Fix scroll (Recommended)** item — the Boot Camp radio is the install. `ShowFixScroll` becomes unused for the menu once radios land (keep the helper false, or delete call sites only). Keyboard **Fix battery reads** is unchanged. 0323 radios are unchanged.

## States

`DriverStatus` today, reused — **no new enum**. v1/v2 `Classify` already:

| Bound name | Apple package | Status |
| --- | --- | --- |
| `applewirelessmouse` | — | `Ok` |
| not Apple | present | `NotBound` |
| not Apple | absent | `NotInstalled` |

`PreferredBoundName` for Apple-filter PIDs (`030d` / `0269` / `0310`) already returns `applewirelessmouse` from LowerFilters or Service, else `null`. Do not start returning `HidBth` as a v1/v2 bound name. Do not classify v1/v2 `applewirelessmouse` as `PathAPatched` (0323 only). Do not classify v1/v2 as `PatchedKmdf` / `StockKmdf`.

Radio checked:

| Status | Radio |
| --- | --- |
| `Ok` | Boot Camp |
| `NotInstalled` (no Apple bind / no Apple package) | Stock Windows |
| `NotBound` | none — Boot Camp still recommended |

`NotBound` is not a third choice. `RecommendedLabel` nags Boot Camp only for v1/v2 `NotBound`. `Ok` and `NotInstalled` are valid choices, not defects.

`V1V2Badge`:

| Status | Badge |
| --- | --- |
| `Ok` | `Boot Camp` |
| `NotInstalled` | `Stock` |
| `Error` | `Error` |
| else (`NotBound`, …) | `Not bound` |

Today `V1V2Badge` is `null` on `Ok` and `"Needs scroll driver"` on `NotInstalled`/`NotBound` — that is why the live Ok row has no Driver submenu. Replace it.

`RecommendedLabel` v1/v2: non-null only on `NotBound` → `Recommended: Boot Camp`. Null on `Ok` and `NotInstalled`. Stop using `Recommended: tealtadpole scroll driver` in the menu.

`IconAttention` for non-0323: `NotInstalled` is valid Stock, so it is **not** attention (0323 already treats `StockKmdf` that way). `NotBound` stays attention. `UnknownAppleMouse` / `Error` stay attention. Do not change the 0323 `IconAttention` branch.

`ShouldShowHealthRow`: `030D` `NotInstalled` must appear (Stock radios reachable). Today it is `false` — invert that. `030D` `Ok` already appears.

V2 does not touch `Aggregate`. `NotInstalled` remains a worst-state there (same role as 0323 `StockKmdf`).

## Verticals (V2–V3)

This file is V1. V2 is **menu then Boot Camp click**. V3 is **Stock click**. No Classify rewrite. No 0323 menu work.

| Vertical | Files | Change |
| --- | --- | --- |
| **V2** menu radios | `MagicMouseTray/TrayApp.cs` (`TrayMenu` + `BuildDeviceRow` v1/v2 branch); `MagicMouseTray.Tests/TrayMenuTests.cs` | **Implemented.** Two radios, easy labels, always visible, checked from status. Boot Camp click stays `OfferV1V2ScrollFix`. Drop orange Fix scroll. `V1V2Badge`, `V1V2CheckedDriverRadio`, `RecommendedLabel`, `ShowFixScroll` false, `IconAttention` (`NotInstalled` not attention), `ShouldShowHealthRow` (`NotInstalled` shown). |
| **V3** Stock unbind | `MagicMouseTray/DriverInstaller.cs`; `MagicMouseTray/TrayApp.cs` (Stock click); `MagicMouseTray.Tests/DriverPackageCatalogTests.cs`; `MagicMouseTray.Tests/TrayMenuTests.cs` | **Implemented.** `PlanV1V2StockRestore(pid)` is the testable contract (`ExternalScripts` empty). `OfferV1V2StockRestore` shows the MessageBox then runs that plan. Stock radio clickable when status is `Ok` (and `NotBound`); checked+disabled when `NotInstalled`. Boot Camp still `OfferV1V2ScrollFix`. Reject 0323 / non-v1/v2 PIDs. |

## Reuse vs rewrite

Reuse:

- `OfferV1V2ScrollFix` as committed: open `DriverPackageCatalog.TealtadpolePageUrl`. Do not `pnputil` / rebind.
- `Classify` / `PreferredBoundName` signatures and v1/v2 table (`Ok` / `NotBound` / `NotInstalled`).
- `DriverPackageCatalog` as the only tealtadpole URL/name source.
- 0323 `BuildDeviceRow` radio shape (checked radio disabled; unchecked clickable; recommended line inside Driver). Copy structure, two radios, no Scroll/Battery.

Rewrite (small):

- v1/v2 `BuildDeviceRow`: disabled `Driver: {bound}` → Driver submenu with Boot Camp / Stock Windows.
- `V1V2Badge` / recommended copy as above.
- `ShowFixScroll` no longer the install affordance.

Do not add KMDF or PATH-A to v1/v2. Do not put `PATH-A` in menu labels.

## Tests per vertical

**V2** (`TrayMenuTests` only):

- Radio copy is exactly `Boot Camp`, `Stock Windows`.
- No user-visible v1/v2 driver string contains `PATH-A`, `applewirelessmouse`, `KMDF`, or `tealtadpole`.
- `V1V2CheckedDriverRadio(Ok)` → `Boot Camp`.
- `V1V2CheckedDriverRadio(NotInstalled)` → `Stock Windows`.
- `V1V2CheckedDriverRadio(NotBound)` → `null`.
- `V1V2Badge(Ok)` → `Boot Camp`; `V1V2Badge(NotInstalled)` → `Stock`; `V1V2Badge(NotBound)` → `Not bound`.
- `ShowFixScroll` false for v1/v2 `Ok`, `NotInstalled`, and `NotBound` (radios replaced it). Still false for all 0323 statuses.
- `RecommendedLabel` v1/v2: non-null only on `NotBound` (`Recommended: Boot Camp`); null on `Ok` and `NotInstalled`.
- `IconAttention("030d", NotInstalled)` false; `IconAttention("030d", NotBound)` true.
- `ShouldShowHealthRow([], "030d", NotInstalled)` true.
- 0323 radio helpers unchanged (`V3DriverRadioLabels`, `V3CheckedDriverRadio`).

**V3**:

- Stock offer is v1/v2 scoped: `PlanV1V2StockRestore` `ExternalScripts` empty; unbind script does not contain `Install-KMDF.cmd`, `Uninstall-KMDF.cmd`, `Install-MagicMousePatch`, `Uninstall-MagicMousePatch`, or `FLIP:NoFilter`. Rejects 0323.
- Boot Camp still `OfferV1V2ScrollFix` (`V1V2BootCampPageUrl` = tealtadpole page URL).
- `V1V2StockRadioEnabled`: true on `Ok` / `NotBound`; false on `NotInstalled`. 0323 radio helpers unchanged.
- No tests that reimplement PnP unbind timing.

## Non-goals

- V1-only: this file without C#. V2/V3 land the menu and Stock unbind.
- KMDF on v1/v2.
- PATH-A / patched Apple `.sys` / Test Mode for v1/v2.
- `PATH-A` in menu labels.
- Changing the 0323 Driver submenu.
- Silent / poller / startup Boot Camp or Stock.
- Keyboard PATH-C, Magic Utilities.
- New `DriverStatus` values.
- Changing `Classify` / `PreferredBoundName` / `Aggregate`.
- New config keys.
