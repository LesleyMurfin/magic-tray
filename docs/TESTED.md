# Hardware reports

This file is **who tested what**. It is not the device catalog.

## Help us test

Only **three** devices have ever been confirmed by a tester. A fourth, the Magic Mouse v2 `0269`, has two independent field reports covering part of the picture ([#124](https://github.com/LesleyMurfin/magic-tray/issues/124) and [#164](https://github.com/LesleyMurfin/magic-tray/issues/164)); they are in the reports table below, they are reports rather than confirmations, and half of the picture is still an open question. Everything else in the catalog should work. Nobody has confirmed it yet.

Got an Apple mouse, keyboard or trackpad? [Send a test report](https://github.com/LesleyMurfin/magic-tray/issues/new?template=test-report.md). It's a short table, and "it didn't work" is just as useful as "it worked".

These six are the priority. Every one is in the app and confirmed by nobody:

| Device | Code | Status |
|--------|------|--------|
| Magic Keyboard | `024F` / `0250` | needs a tester |
| Magic Keyboard with Touch ID | `0267` / `026C` | needs a tester |
| Magic Keyboard (2021) | `029C` | needs a tester |
| Magic Trackpad (AA batteries) | `030E` | needs a tester |
| Magic Trackpad 2 (Lightning) | `0265` | needs a tester |
| Magic Trackpad 2024 (USB-C) | `0324` | needs a tester |

Bluetooth Magic Keyboards matter most. For a trackpad we only need the battery percent, because Magic Tray does not offer a driver change for a trackpad.

## The catalog

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

One row per (PID, transport, Magic Tray version, driver). Tried the device on more than one driver? Send one row per driver, because battery and scroll can differ between them.

## Reports

| Model | PID | Transport | Tray | Windows | Battery | Driver | SDP | Tester | Date | Notes |
|-------|-----|-----------|------|---------|---------|--------|-----|--------|------|-------|
| Magic Mouse 2024 (USB-C, v3) | `0323` | Bluetooth | 1.1.0 | Windows 11 | ok (30%) | KMDF | n/a | LesleyMurfin | 2026-09-02 | Live tray |
| Magic Mouse v1 | `030D` | Bluetooth | 1.1.0 | Windows 11 | ok (100%) | Boot Camp | n/a | LesleyMurfin | 2026-09-01 | Live tray; unified Feature 0x47 after poll |
| Apple Wireless Keyboard (2011) | `0239` | Bluetooth | 1.1.0 | Windows 11 | ok (16%) | n/a | patched | LesleyMurfin | 2026-09-02 | Live tray |
| Magic Mouse v2 | `0269` | Bluetooth | not stated | Windows 11 | no reading | Boot Camp | n/a | @centralcaetite | 2026-09-07 | Reported in [#124](https://github.com/LesleyMurfin/magic-tray/issues/124): scroll works on Apple's driver, tray found no battery. Taken before the battery-discovery fix, so the cause is open |
| Magic Mouse v2 | `0269` | Bluetooth | not stated | Windows 11 | ok (73%) | Stock | n/a | @centralcaetite | 2026-09-07 | Same report: percent shows on stock Windows, no scrolling at all. The percent itself arrived in a later comment on the same issue, 2026-10-19 |
| Magic Mouse v2 | `0269` | Bluetooth | 1.1.0 | Windows 11 Pro 26H2 | no reading | Boot Camp | n/a | @AlecMakarov | 2026-10-12 | Reported in [#164](https://github.com/LesleyMurfin/magic-tray/issues/164), with a screenshot: the tray row reads "Battery unavailable" while Apple's driver is bound. Taken on 1.1.0, before the battery-discovery fix, so the cause is open |
| Magic Mouse v2 | `0269` | Bluetooth | 1.1.0 | Windows 11 Pro 26H2 | ok (83%) | Stock | n/a | @AlecMakarov | 2026-10-12 | Same report, with a screenshot: 83% on stock Windows, no scrolling at all |

The four `0269` rows are **reports, not confirmations**, from two testers who have never met. They cover which driver was bound, whether the wheel scrolled and whether a percent appeared. Both testers have now quoted a stock-Windows percent - **73%** in the first report, **83%** in the second - and the second report fills in two of the gaps the first left: the Magic Tray version (1.1.0) and the Windows version. That is two independent accounts agreeing, not a reading - nobody on this project has yet read a `0269` off its own hardware. Both reports also predate a fix on this branch: Magic Tray's device discovery kept only one HID interface per device, so a mouse whose battery lives on a second interface could be reported as having no battery at all. That fault alone could produce either Boot Camp row, so nothing here says Apple's driver costs a v2 its percent. A `0269` report on the next version, on each driver, is still what would settle it. Nothing here is copied to the Apple Wireless Mouse `0310`, which still has no report at all.
