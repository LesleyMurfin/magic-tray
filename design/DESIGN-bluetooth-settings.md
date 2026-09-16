# DESIGN — Tray links to Windows Bluetooth Settings

Status: Implemented. Small ADW vertical so Lesley can pair the v1 mouse.

Windows has no public URI to flip the Bluetooth radio without Settings. Add/change devices and the radio toggle still open **Bluetooth & devices**. Rename of a Bluetooth HID device on Windows 11 is **Devices and Printers**, not Bluetooth Settings and not Rename this PC (`ms-settings:about`). This vertical does not invent WinRT `Radio.SetStateAsync`.

## User flow

Tray → **Bluetooth** → one of:

```
Add or change devices…
Turn Bluetooth on or off…
Rename a device…
```

- **Add or change devices…** and **Turn Bluetooth on or off…** open Windows Settings on **Bluetooth & devices**. Toggle Bluetooth at the top, or Add device (pair the v1 mouse).
- **Rename a device…** opens **Devices and Printers**. Right-click a HID device → Properties (or Rename) there.

The tray does not pair, toggle, or rename itself.

## URIs / two destinations

| Menu item | Destination | Why this page |
| --- | --- | --- |
| Add or change devices… | `ms-settings:bluetooth` | Add device lives here. |
| Turn Bluetooth on or off… | `ms-settings:bluetooth` | Radio toggle is at the top. No documented `ms-settings:` URI flips the radio alone. |
| Rename a device… | `control /name Microsoft.DevicesAndPrinters` (CLSID `{A8A91A66-3A7D-4424-8D24-04E180695C7A}`) | Win11 Bluetooth HID rename is Devices and Printers. Not `ms-settings:bluetooth` (device flyout does not rename HID the way users expect) and not `ms-settings:about` (that is Rename this PC). |

`ms-settings:bluetooth` is the Devices page, not a deep-link into a single device.

`BluetoothSettings.OpenRenamePage()` is distinct from `OpenDevicesPage()`. The Rename menu click calls `OpenRenamePage()`.

## Files

| File | Role |
| --- | --- |
| `MagicMouseTray/BluetoothSettings.cs` | `DevicesUri`, rename `control` target, labels, `OpenDevicesPage()` / `OpenRenamePage()` (`Process.Start` + `UseShellExecute`). Pure helper — no WinForms. |
| `MagicMouseTray/TrayApp.cs` (`BuildMenu`) | **Bluetooth** submenu after the Devices block, before **Low battery threshold**. Add/toggle call `OpenDevicesPage()`; Rename calls `OpenRenamePage()`. |
| `MagicMouseTray.Tests/BluetoothSettingsTests.cs` | Exact labels; add/change URI is `ms-settings:bluetooth`; rename is not `ms-settings:bluetooth` and not `ms-settings:about`; no `PATH-A` in labels. |
| `README.md` | One Features bullet. |

## Tests

- Labels are exactly `Bluetooth`, `Add or change devices…`, `Turn Bluetooth on or off…`, `Rename a device…`.
- `DevicesUri` is `ms-settings:bluetooth`.
- Rename destination is Devices and Printers (`control /name Microsoft.DevicesAndPrinters`), not `ms-settings:bluetooth` and not `ms-settings:about`.
- No user-visible string contains `PATH-A`.
- Tests do not call `OpenDevicesPage()` or `OpenRenamePage()` (that would launch a shell window).

## Non-goals

- WinRT `Radio.SetStateAsync` (or any in-process radio toggle).
- Pairing automation, HID pairing, or PIN handling.
- Renaming a device from the tray.
- Deep-links that Windows does not document.
- Push. Killing the live tray unless a published exe replaces it.
