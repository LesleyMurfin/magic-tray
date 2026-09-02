---
name: Missing device / PID
about: This Apple mouse, keyboard, or trackpad is not in KnownMice / KnownKeyboards
title: "device: PID xxxx — model name"
labels: device
---

The in-app catalog is `MouseBatteryDevice.KnownMice` and `KeyboardBatteryDevice.KnownKeyboards`. CI walks those lists. Do not open a `docs/TESTED.md` PR until the PID is in that catalog.

**I checked KnownMice / KnownKeyboards and this PID is not there.**

- Magic Tray version (tray footer):
- Windows version (`winver`):
- Device (mouse / keyboard / trackpad) and marketing name:
- Hardware Ids from Device Manager (paste, e.g. `HID\VID_05AC&PID_0323` or `PID&0323`):
- Connected over: Bluetooth / USB / both
- What the tray shows today (nothing / unknown model / other):
- `%APPDATA%\MagicMouseTray\debug.log` excerpt (optional):
