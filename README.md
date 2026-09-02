# Magic Tray

**Free Magic Mouse battery and scroll on Windows 10/11.** MIT licensed. No subscription. No trial that kills scroll.

Magic Tray is a Windows tray app for **Apple Magic Mouse** (v1, v2, and **Magic Mouse 2024 / Magic Mouse v3**, PID `0323`), **Magic Keyboard**, and **Magic Trackpad**. It shows battery percent in the tray and can install a scroll driver you confirm. It is a free alternative to [Magic Utilities](https://magicutilities.net/) — not a clone of MU’s proprietary drivers, gestures, trackpad suite, or key remaps.

Released 2 September 2026 · [Download v1.1.0](https://github.com/LesleyMurfin/magic-tray/releases/tag/v1.1.0) · [Site](https://lesleymurfin.github.io/magic-tray/)

If this saved you a paid subscription, **star both repos**: [Magic Tray](https://github.com/LesleyMurfin/magic-tray) and the [v3 Windows driver](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix). Goal: a publicly signed KMDF so Test Mode goes away — [stars & signing](https://lesleymurfin.github.io/magic-tray/funding.html).

![Windows 11 system tray with Magic Tray](docs/screenshot-tray.png)

![Magic Tray 1.1.0 menu: Magic Keyboard, Magic Mouse 2024 (v3) KMDF, Magic Mouse v1](docs/screenshot-menu.png)

---

## Contents

- [Magic Tray vs Magic Utilities](#magic-tray-vs-magic-utilities)
- [Install](#install)
- [Features](#features)
- [Tray menu (1.1.0)](#tray-menu-110)
- [Supported mice](#supported-mice)
  - [Magic Mouse 2024 / v3 driver choices](#magic-mouse-2024-v3-0323-driver-choices)
- [Supported keyboards](#supported-keyboards)
- [Supported trackpads](#supported-trackpads)
- [Scroll](#scroll)
  - [Magic Mouse v1 / v2](#magic-mouse-v1--v2-030d-0269)
  - [Magic Mouse 2024 / v3](#magic-mouse-2024--v3-0323)
  - [Test Mode](#test-mode-0323-kmdf-and-patched-apple)
- [Why Magic Mouse v3 scroll breaks on Windows](#why-magic-mouse-v3-scroll-breaks-on-windows)
- [Keyboard battery](#keyboard-battery)
- [FAQ](#faq)
- [Building from source](#building-from-source)
- [Diagnostics](#diagnostics)
- [Releases](#releases)
- [License](#license)
- [Which driver](https://lesleymurfin.github.io/magic-tray/drivers.html)
- [Why v3 is hard](https://lesleymurfin.github.io/magic-tray/v3.html)
- [Stars & signing](https://lesleymurfin.github.io/magic-tray/funding.html)
- [llms.txt](llms.txt) (for agents)

---

## Magic Tray vs Magic Utilities

| | Magic Tray (free, MIT) | Magic Utilities (paid) |
|---|---|---|
| Mouse battery in the tray | Yes (Bluetooth + USB HID when Windows exposes it) | Yes |
| Keyboard battery | Yes, after a one-time SDP patch you run | Yes |
| Trackpad battery | Yes. No scroll/gesture driver. | Yes, plus a paid trackpad suite |
| Time-based battery alerts | Yes. Floor 10 → 5 → 1. See [docs/ALERTS.md](docs/ALERTS.md). | Customizable percent alerts |
| Scroll driver (mice) | Yes, user-initiated | Yes, proprietary (mouse + trackpad) |
| Driver signature | v1/v2: catalog-signed Boot Camp. **v3 KMDF / Patched Apple are self-signed** (Test Mode + Memory Integrity off). Not WHQL. | WHQL; works with Secure Boot |
| Gestures, trackpad tap / 3-finger, media-key remaps | **Not shipped** | Yes |
| Subscription / trial that disables scroll | Never | Required after trial |

If you need MU’s gesture or trackpad suite, use Magic Utilities. If you need battery + mouse scroll on Windows without a subscription, use Magic Tray.

---

## Install

1. Download `MagicMouseTray.exe` from [Releases](https://github.com/LesleyMurfin/magic-tray/releases/latest).
2. Run it. No installer. The tray does not need admin.
3. The icon shows battery percent once a supported device is paired.

**Requires:** Windows 10 1809+ (build 17763) or Windows 11, x64.

The same Release attaches the keyboard battery patch (`Install-KeyboardBattery.cmd` + `kbd-patch-cachedservices.ps1`). You only need those if you want keyboard battery. **Pass `-Mac`.** There is no default MAC.

> Windows may show SmartScreen on the first run (unsigned open-source exe). **More info → Run anyway**, or right-click → Properties → **Unblock**.

---

## Features

- Battery percent on the tray for Magic mice, keyboards, and trackpads
- Alerts: [docs/ALERTS.md](docs/ALERTS.md) (10 → 5 → 1, plus time warnings)
- Persistent 0–1% warning (replace AA, or plug in USB-C / Lightning)
- Start with Windows
- User-initiated **mouse** scroll-driver install (v1/v2 Boot Camp; Magic Mouse 2024 / v3: KMDF, Patched Apple, or Stock). Trackpads have no driver radios.
- User-initiated keyboard SDP patch
- Bluetooth menu opens Windows Bluetooth Settings
- Optional Logitech rows (off by default)
- Single self-contained `MagicMouseTray.exe`

**Battery reads** appears only when a Magic Mouse 2024 / v3 is connected **and** the bound driver is KMDF. Hidden otherwise.

Magic Tray does not install a driver until you confirm. No silent rebind. No Magic Utilities binaries.

---

## Tray menu (1.1.0)

Right-click the tray icon.

| Item | What it does |
|------|-------------|
| *N* devices | Each Magic device: battery %, driver badge, enable, per-device alert |
| Bluetooth | Opens Windows Bluetooth Settings |
| Low battery threshold | 10% / 5% / 1% (default 10%) |
| Start with Windows | Auto-start on login |
| Show Logitech devices [Off] | Experimental. Off by default. |
| Battery reads | Status only. Visible on 2024 / v3 + KMDF. |
| Refresh Now | Immediate battery read |
| Diagnostics | Logs, test notification, capture scripts |
| Help/Documentation | Alerts doc, this repo, report a bug |
| Quit | Exit |

Footer: **Magic Tray 1.1.0**.

---

## Supported mice

Catalog: [`KnownMice`](MagicMouseTray/MouseBatteryDevice.cs) (CI: [`EveryKnownMousePid_HasUsbVid05acRow`](MagicMouseTray.Tests/MouseBatteryDeviceTests.cs)). Reports: [docs/TESTED.md](docs/TESTED.md). PID missing? [Open an issue](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md).

| Model | PID | Recommended driver | Scroll | Battery | Tested |
|---|---|---|---|---|---|
| Magic Mouse 2024 (USB-C, **Magic Mouse v3**) | `0x0323` | **KMDF** from [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) | Yes | Yes | [yes — KMDF](docs/TESTED.md) |
| Magic Mouse v1 | `0x030D` | **Boot Camp** [tealtadpole INF](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) | Yes | Yes | [row](docs/TESTED.md) |
| Magic Mouse v2 | `0x0269` | Same Boot Camp INF | Yes | Yes | — |
| Apple Wireless Mouse | `0x0310` | Same Boot Camp INF | Yes | Yes | — |

### Magic Mouse 2024 / v3 (`0323`) driver choices

KMDF is the only 2024 / v3 path with **both** scroll and battery. Pick one lasting driver; install steps under [Scroll](#scroll).

| 0323 driver | Scroll | Battery | Test Mode |
|---|---|---|---|
| **KMDF (recommended)** | Yes | Yes | Required |
| Patched Apple | Yes **or** battery — not both | Mutually exclusive | Required |
| Stock Windows (`HidBth`) | No | Yes if Windows exposes HID | Not required |

0323 battery is **only** HID Input `0x90` on COL02. Magic Tray does not use Feature `0x47` for Magic Mouse v3, and does not treat iPhone Hands-Free / WMI as mouse battery.

---

## Supported keyboards

Catalog: [`KnownKeyboards`](MagicMouseTray/KeyboardBatteryDevice.cs) (CI: [`EveryKeyboardPid_HasUsbVid05acRow`](MagicMouseTray.Tests/KeyboardBatteryDeviceTests.cs)). Bluetooth battery needs the [SDP patch](#keyboard-battery). USB HID battery is read when Windows exposes `VID_05AC` + the same PID.

| Model | PID | Driver | Scroll | Battery | Tested |
|---|---|---|---|---|---|
| Apple Wireless Keyboard (2011, A1314) ANSI / ISO / JIS | `0x0239` / `0x023A` / `0x023B` (`0x0255` / `0x0256` / `0x0257`) | SDP patch (not a kernel driver) | n/a | Yes after patch | [yes — `0x0239`](docs/TESTED.md) |
| Magic Keyboard (A1644) / ISO | `0x024F` / `0x0250` | SDP patch | n/a | Yes after patch | Not hardware-tested here |
| Magic Keyboard with Touch ID (A2449) / ISO | `0x0267` / `0x026C` | SDP patch | n/a | Yes after patch | Not hardware-tested here |
| Magic Keyboard (2021) / Touch ID / Numeric Keypad | `0x029C` / `0x029A` / `0x029F` | SDP patch | n/a | Yes after patch | Not hardware-tested here |
| Magic Keyboard (2024, USB-C) / Touch ID / Numeric Keypad | `0x0320` / `0x0321` / `0x0322` | SDP patch | n/a | Yes after patch | Not hardware-tested here |

---

## Supported trackpads

Battery only: percent, enable, threshold, time alerts. **No** KMDF / Boot Camp radios. **No** Magic Utilities trackpad gestures.

| Model | PID | Driver | Scroll | Battery | Tested |
|---|---|---|---|---|---|
| Magic Trackpad (v1) | `0x030E` | None | n/a | Yes (AA) | — |
| Magic Trackpad 2 | `0x0265` | None | n/a | Yes (Lightning) | — |
| Magic Trackpad 2024 (USB-C) | `0x0324` | None | n/a | Yes (USB-C) | — |

Hardware reports: [docs/TESTED.md](docs/TESTED.md). Missing PID: [open an issue](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md).

---

## Scroll

Scroll needs an Apple mouse filter driver **installed and bound**. The tray offers the install after you confirm.

### Magic Mouse v1 / v2 (`030D`, `0269`)

Install the Boot Camp INF from [tealtadpole/MagicMouse2DriversWin11x64](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) (Apple Wireless Mouse folder). Right-click the `.inf` → **Install**, or use the tray. Catalog-signed. **Test Mode is not required.**

### Magic Mouse 2024 / v3 (`0323`)

The tealtadpole INF does not cover PID `0323`. Magic Tray clones [LesleyMurfin/magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) `main`.

**KMDF (recommended).** Runs `v2-kmdf-driver/Install-KMDF.cmd`. If that file is missing, the tray **stops**. It will not fall back to the patched Apple installer.

**Patched Apple.** User-initiated only. Runs `v1-binary-patch/installer/Install-MagicMousePatch.ps1`. Scroll and battery are mutually exclusive on this path. No fallback to KMDF.

**Stock Windows.** Unbind to `HidBth`. Test Mode not required.

After install, remove and re-pair the mouse if scroll still does not work.

### Test Mode (0323 KMDF and Patched Apple)

The 2024 / v3 KMDF package and the patched Apple `.sys` are **self-signed**, not WHQL. Windows will not load them until Test Mode is on and Memory Integrity (HVCI) is off. You do **not** need the one-time F7 “Disable driver signature enforcement” boot.

Do this **before** `Install-KMDF.cmd` or the patched Apple installer:

1. Elevated Command Prompt: `bcdedit /set testsigning on`
2. Windows Security → Device security → Core isolation → **Memory integrity** = Off
3. Reboot (desktop watermark “Test Mode” is expected)
4. Run the installer or confirm the tray Driver radio

To leave Test Mode: `bcdedit /set testsigning off`, then reboot. Turning it off while a self-signed 0323 driver is still bound will stop scroll.

v1/v2 Boot Camp installs skip this section.

---

## Why Magic Mouse v3 scroll breaks on Windows

The 2024 Magic Mouse (v3, USB-C, PID `0323`) is not in Apple’s old Boot Camp INF. On Windows the HID stack can collapse collections (often called Mode A vs Mode B): scroll and battery fight over the same device. Stock Windows often has battery and no scroll; a patched Apple filter can restore scroll and drop battery.

**Research and the KMDF fix live in a separate repo** (this tray repo does not vendor kernel sources):

- [LesleyMurfin/magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) — KMDF installer + patched Apple path
- [HID research (RID 0x90, collections, DSM)](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/blob/main/v1-binary-patch/docs/hid-research.md)
- [Bug analysis (Mode A/B)](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/blob/main/v1-binary-patch/docs/bug-analysis.md)
- [Mode A/B diagram](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/blob/main/v1-binary-patch/docs/diagrams/diagram-mode-ab.md)

Magic Tray’s recommended path is KMDF from that repo: scroll **and** battery (Input `0x90` on COL02).

---

## Keyboard battery

Apple wireless keyboards declare battery as Input-only, so Windows cannot poll it. A one-time registry patch of the Bluetooth SDP cache (`HKLM\...\BTHPORT\Parameters\Devices\<MAC>`) exposes battery as a Feature report. No kernel driver.

**You must pass the keyboard MAC.** `kbd-patch-cachedservices.ps1` requires `-Mac` (12 hex digits, colons optional).

Find it in Device Manager → Bluetooth → keyboard → Details → **Bluetooth device address**, or from the tray when it offers the patch.

**From a Release:** keep `Install-KeyboardBattery.cmd` and `kbd-patch-cachedservices.ps1` in the same folder, then elevated:

```bat
Install-KeyboardBattery.cmd -Mac aabbccddeeff
```

Toggle Bluetooth off/on so Windows re-reads the cache.

**From source:**

```powershell
.\scripts\kbd-patch-cachedservices.ps1 -Mac aabbccddeeff
```

Re-pairing the keyboard erases the patch. Until it is applied, the tray shows that the keyboard needs the SDP-cache patch.

---

## FAQ

**Does Magic Mouse 2024 (Magic Mouse v3) work on Windows 11?**  
Yes. Use Magic Tray 1.1.0 and install the KMDF driver from [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix). That path needs Test Mode and Memory Integrity off. Battery is HID Input `0x90` on COL02.

**Is this Magic Utilities?**  
No. Free MIT. No subscription. No MU binaries. No gesture / trackpad / media-key suite.

**Will scroll stop when a trial expires?**  
No. There is no trial.

**Do trackpads get scroll or tap-to-click?**  
No. Battery and alerts only.

---

## Building from source

Requires the .NET 8 SDK (Windows).

```powershell
dotnet publish -c Release
# Output: bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\MagicMouseTray.exe
```

The exe filename stays `MagicMouseTray.exe`. The product name is **Magic Tray**.

KMDF sources live in [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) (`v2-kmdf-driver/`). This repo does not vendor that driver.

---

## Diagnostics

Log file: `%APPDATA%\MagicMouseTray\debug.log`

| Line | Meaning |
|---|---|
| `OK battery=83%` | Successful read |
| `OPEN_FAILED err=5` | COL01 skipped (Windows holds that handle) |
| `DRIVER_CHECK status=...` | Per-device driver health |
| `TOAST_SENT` | Low-battery notification |
| `CRITICAL_ALERT_SHOWN` | 1% persistent window |

---

## Releases

CI builds on a `v*` tag: test, publish win-x64, optional Authenticode (`SIGN_PFX_*`), then attach `MagicMouseTray.exe`, `kbd-patch-cachedservices.ps1`, `Install-KeyboardBattery.cmd`, and `SHA256SUMS`. `scripts/verify-release.ps1` must pass before the GitHub Release is created.

`FileVersion` / `AssemblyVersion` are `1.1.0.0`.

---

## License

[MIT](LICENSE) · Copyright (c) 2026 Lesley Murfin.
