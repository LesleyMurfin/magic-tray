---
name: Bug report
about: Something is wrong with battery, scroll, or the tray
title: "bug: "
labels: bug
---

Use **Help → Report a bug** in Magic Tray to pre-fill this with version, driver badges, battery readings, and a MAC-redacted log tail.

Battery percent wrong or missing? Also open **Diagnostics → Run mm-hid-report.ps1** (added in Magic Tray 1.1.1 - the menu item is not in 1.1.0) and attach the report file it saves in `%APPDATA%\MagicMouseTray\`. It adds the HID collections your device exposes and which battery channel replied, with the Bluetooth address already removed.

Either way, fill these in:

**Magic Tray version** (tray footer):

**Windows** (`winver`):

**Device** (PID / model):

**What happened:**

**What you expected:**

Paste the tray snapshot or `%APPDATA%\MagicMouseTray\debug.log` excerpt (no Bluetooth MAC).
