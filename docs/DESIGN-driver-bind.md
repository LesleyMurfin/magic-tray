# DESIGN — Detect the mouse driver, not Bluetooth HID

Status: Implemented. Small ADW vertical so 0323 KMDF shows as bound.

Live: `DRIVER_CHECK pid=0x0323 service=HidBth bound=HidBth status=StockKmdf` while scroll works and another session says KMDF. The tray only read the BTHENUM **instance** `Service` / `LowerFilters`. `HidBth` is the Bluetooth HID **function** driver, not the mouse driver.

KMDF `MagicMouseDriver` may sit on:

- the BTHENUM **hardware-id** key `LowerFilters` (live: `MagicMouseDriver` there, instance has `HidBth` + `MagicMouseDriver204Scroll`)
- the HID / MouHID child `Enum\HID\…` `Service` / `LowerFilters`

v1 `applewirelessmouse` on BTHENUM instance LowerFilters is still valid — keep it.

## Rule

BTHENUM instance `Service=HidBth` is not the mouse driver. Merge:

1. BTHENUM hardware-id + instance `Service` / `LowerFilters` (`REG_SZ` or `REG_MULTI_SZ`)
2. `Enum\HID` keys whose name contains the PID (`0323` / `030d` / `0269` / `0310`) `Service` / `LowerFilters`

KMDF wins.

0323 bound name after merge:

1. `MagicMouseDriver` in either layer’s Service or LowerFilters → `MagicMouseDriver` → `PatchedKmdf`
2. else `applewirelessmouse` in either layer → `applewirelessmouse` → `PathAPatched` (0323) or `Ok` (v1)
3. else BTHENUM Service (`HidBth`) → `StockKmdf` on 0323

v1 (`030d` / `0269` / `0310`): BTHENUM LowerFilters `applewirelessmouse` still `Ok`. Do not start returning `HidBth` as a v1 bound name.

## Log

Both layers, then the merged name:

```
DRIVER_CHECK pid=0x0323 bth=HidBth hid=MagicMouseDriver bound=MagicMouseDriver status=PatchedKmdf
```

`Bound:` in the Driver submenu already prints `BoundDriverName`. After merge that is `MagicMouseDriver`, not `HidBth`. No TrayApp rename.

## Files

| File | Role |
| --- | --- |
| `docs/DESIGN-driver-bind.md` | This design. |
| `MagicMouseTray/DriverHealthChecker.cs` | Scan `Enum\HID` by PID; merge into `PreferredBoundName`; log `bth=` / `hid=` / `bound=`. |
| `MagicMouseTray.Tests/DriverHealthCheckerTests.cs` | Merge contracts below. |

## Tests

WindowsDesktop `DriverHealthCheckerTests`. No live HID required for the merge helper.

- BTHENUM `HidBth` + HID LowerFilters `MagicMouseDriver` → bound `MagicMouseDriver` → `PatchedKmdf`
- BTHENUM `applewirelessmouse` still `Ok` for `030d`
- `HidBth` only → `StockKmdf`
- BTHENUM LowerFilters `MagicMouseDriver` + HID `mouhid` → `PatchedKmdf` (live 0323 shape)

## Non-goals

- Silent rebind. PATH-A. Push.
- TrayApp feature work (Bound line stays `Bound: {BoundDriverName}`).
- Changing `Classify` / `PreferredBoundName` tables except by feeding them merged names.
