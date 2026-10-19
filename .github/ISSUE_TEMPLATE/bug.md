---
name: Bug report
about: Something is wrong with battery, scroll, or the tray
title: "bug: "
labels: bug
---

Use **Help → Report a bug** in Magic Tray to pre-fill this with version, driver badges, battery readings, and a MAC-redacted log tail.

Battery percent wrong or missing? Open **Diagnostics → Run mm-hid-report.ps1** and attach the report file it saves in `%APPDATA%\MagicMouseTray\`. It answers your Windows build, Magic Tray version, device code, HID collections and which battery channel replied, with the Bluetooth address already removed. Or paste by hand:

**Device** (model name as printed on it):

**What happened:**

**What you expected:**

Paste the tray snapshot or `%APPDATA%\MagicMouseTray\debug.log` excerpt (no Bluetooth MAC).
