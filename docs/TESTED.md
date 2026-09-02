# Hardware reports

Community results for Magic Tray on real devices. **In the catalog** (README) is not the same as **tested here**.

Open a PR that adds or updates **one row**. Do not edit README status columns in the same PR unless you also tested.

## How to add a row

1. Magic Tray version (tray footer, or exe Properties → Details).
2. Device Manager → the HID device → Details → **Hardware Ids** (`PID_xxxx` or `PID&xxxx`).
3. Bluetooth, USB, or both.
4. After **Refresh Now**: battery percent, or `No reading`.
5. Mice: driver badge (`KMDF` / `Boot Camp` / `Stock` / `Patched Apple` / `Not bound`).
6. Keyboards: SDP patch applied or not.

Optional: `%APPDATA%\MagicMouseTray\debug.log` (`OK battery=…`) and Windows build (`winver`).

| Column | Values |
|--------|--------|
| Battery | `ok` (percent shown) / `no reading` / `untested` |
| Driver | badge text, or `n/a` for keyboards and trackpads |
| SDP | `patched` / `unpatched` / `n/a` |

One row per (PID, transport, Magic Tray version). Duplicate PIDs with a different Windows build or tray version are fine.

## Reports

| Model | PID | Transport | Tray | Windows | Battery | Driver | SDP | Tester | Date | Notes |
|-------|-----|-----------|------|---------|---------|--------|-----|--------|------|-------|
| Magic Mouse 2024 (USB-C) | `0323` | Bluetooth | 1.1.0 | Windows 11 | ok (30%) | KMDF | n/a | LesleyMurfin | 2026-09-02 | Live tray screenshot |
| Magic Mouse v1 | `030D` | Bluetooth | 1.1.0 | Windows 11 | no reading | Boot Camp | n/a | LesleyMurfin | 2026-09-02 | Device row present; battery not read |
| Apple Wireless Keyboard (2011) | `0239` | Bluetooth | 1.1.0 | Windows 11 | ok (16%) | n/a | patched | LesleyMurfin | 2026-09-02 | Live tray screenshot |

## Not yet reported

These PIDs are in the app. No hardware report in this file yet.

### Mice

| Model | PID |
|-------|-----|
| Magic Mouse v2 | `0269` |
| Apple Wireless Mouse | `0310` |
| Magic Mouse 2024 (USB) | `0323` |
| Magic Mouse v1 (USB) | `030D` |

### Keyboards

| Model | PID |
|-------|-----|
| Apple Wireless Keyboard (2011) ISO / JIS | `023A` / `023B` |
| Apple Wireless Keyboard (2011 ANSI/ISO/JIS rev) | `0255` / `0256` / `0257` |
| Magic Keyboard / ISO | `024F` / `0250` |
| Magic Keyboard with Touch ID / ISO | `0267` / `026C` |
| Magic Keyboard (2021) / Touch ID / Numeric Keypad | `029C` / `029A` / `029F` |
| Magic Keyboard (2024, USB-C) / Touch ID / Numeric Keypad | `0320` / `0321` / `0322` |

### Trackpads (battery only — no scroll driver)

| Model | PID |
|-------|-----|
| Magic Trackpad (v1) | `030E` |
| Magic Trackpad 2 | `0265` |
| Magic Trackpad 2024 (USB-C) | `0324` |
