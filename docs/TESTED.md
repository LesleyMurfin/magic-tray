# Hardware reports

This file is **who tested what**. It is not the device catalog.

The catalog Magic Tray actually loads is in the app, and CI fails if it drifts:

| What | Where |
|------|--------|
| Mice and trackpads | [`MouseBatteryDevice.KnownMice`](../MagicMouseTray/MouseBatteryDevice.cs) |
| Keyboards | [`KeyboardBatteryDevice.KnownKeyboards`](../MagicMouseTray/KeyboardBatteryDevice.cs) |
| CI: every mouse PID has a USB row | [`EveryKnownMousePid_HasUsbVid05acRow`](../MagicMouseTray.Tests/MouseBatteryDeviceTests.cs) |
| CI: every keyboard PID has a USB row | [`EveryKeyboardPid_HasUsbVid05acRow`](../MagicMouseTray.Tests/KeyboardBatteryDeviceTests.cs) |
| CI: every display name resolves | [`KindForNameTests`](../MagicMouseTray.Tests/KindForNameTests.cs) |

If your device is **not in those tables**, do not add it here. [Open an issue](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md) with the Hardware Id PID so it can be added to `KnownMice` / `KnownKeyboards` (and CI) first.

If it **is** in the catalog and you ran Magic Tray on it, add a report row below.

## How to add a report

1. Confirm the PID exists in `KnownMice` or `KnownKeyboards`. Missing → [issue](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md).
2. Tray version (footer, or exe Properties → Details).
3. Device Manager → HID device → Details → **Hardware Ids**.
4. Bluetooth, USB, or both.
5. After **Refresh Now**: percent, or `No reading`.
6. Mice: driver badge (`KMDF` / `Boot Camp` / `Stock` / `Patched Apple` / `Not bound`).
7. Keyboards: SDP `patched` or `unpatched`.

Open a PR that adds **one row**. Sign the commit (`git commit -s`).

| Column | Values |
|--------|--------|
| Battery | `ok` / `no reading` |
| Driver | badge text, or `n/a` |
| SDP | `patched` / `unpatched` / `n/a` |

One row per (PID, transport, Magic Tray version).

## Reports

| Model | PID | Transport | Tray | Windows | Battery | Driver | SDP | Tester | Date | Notes |
|-------|-----|-----------|------|---------|---------|--------|-----|--------|------|-------|
| Magic Mouse 2024 (USB-C) | `0323` | Bluetooth | 1.1.0 | Windows 11 | ok (30%) | KMDF | n/a | LesleyMurfin | 2026-09-02 | Live tray |
| Magic Mouse v1 | `030D` | Bluetooth | 1.1.0 | Windows 11 | ok (100%) | Boot Camp | n/a | LesleyMurfin | 2026-09-01 | Live tray; unified Feature 0x47 after poll |
| Apple Wireless Keyboard (2011) | `0239` | Bluetooth | 1.1.0 | Windows 11 | ok (16%) | n/a | patched | LesleyMurfin | 2026-09-02 | Live tray |
