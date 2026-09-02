# Magic Tray 1.1.0

Free Windows tray app for Apple Magic Mouse, Magic Keyboard, and Magic Trackpad on Windows 10/11. **No subscription.**

This is a **free alternative** to [Magic Utilities](https://magicutilities.net/) — battery %, time alerts, Start with Windows, and a driver install you confirm. It is **not** a paid clone of Magic Utilities' proprietary drivers, gesture suite, trackpad suite, or media-key remapping.

**MIT licensed. No trial. Scroll never turns off because a license expired.**

Released 2 September 2026.

![Magic Tray in the Windows 11 tray](docs/screenshot-tray.png)

![Magic Tray 1.1.0 menu — keyboard, Magic Mouse 2024 (KMDF), Magic Mouse v1](docs/screenshot-menu.png)

## Magic Tray vs Magic Utilities

| | Magic Tray (free, MIT) | Magic Utilities (paid) |
|---|---|---|
| Mouse battery in the tray | Yes (Bluetooth + USB HID when Windows exposes it) | Yes |
| Keyboard battery | Yes, after a one-time SDP patch you run (USB HID when Windows exposes it) | Yes |
| Trackpad battery | Yes (Bluetooth + USB HID). No scroll/gesture driver. | Yes, plus a paid trackpad suite |
| Time-based battery alerts | Yes. Percent floor 10 → 5 → 1 (default 10). AA: 48h toast + death modal. Rechargeable: 24h night-before toast + connected 0–1% plug-now. See [docs/ALERTS.md](docs/ALERTS.md). | Customizable percent alerts |
| Scroll driver install | Yes, user-initiated, **mice only** | Yes, proprietary (mouse + trackpad) |
| Driver signature | v1/v2: catalog-signed Boot Camp INF. **0323 KMDF and Patched Apple are self-signed** (Test Mode + Memory Integrity off). Not WHQL. Stock Windows is Microsoft-signed. | WHQL-signed drivers; works with Secure Boot / Memory Integrity |
| Secure Boot / HVCI | v1/v2 OK. 0323 KMDF and Patched Apple need Test Mode and Memory Integrity **off**. Stock does not. | Works with Secure Boot |
| Smooth scroll, middle-click modes, desktop swipes, trackpad tap / 3-finger, media / fn / modifier remaps | **Not shipped** | Yes |
| MU WHQL drivers / MU binaries | **Not shipped** | Yes |
| Subscription | Never | Required after trial |
| Trial that disables scroll | Never | Trial expiry can disable scroll |

If you need Magic Utilities' gesture, trackpad, or media-key suite, use Magic Utilities. If you need battery + mouse scroll on Windows without a subscription, use Magic Tray.

## Install

1. Download `MagicMouseTray.exe` from [Releases](../../releases).
2. Run it. No installer. The tray itself does not need admin.
3. The tray icon shows battery percent once a supported device is paired.

**Requires:** Windows 10 1809+ (build 17763) or Windows 11, x64.

The same Release attaches the keyboard battery patch (`Install-KeyboardBattery.cmd` + `kbd-patch-cachedservices.ps1`). You only need those files if you want keyboard battery readings. **Pass your keyboard's Bluetooth MAC** (`-Mac`). The script has no default MAC and will not run without it.

Windows may show a SmartScreen warning on the first run (unsigned open-source exe). Choose **More info → Run anyway**, or right-click the file → Properties → **Unblock**.

## Features

- Battery percent on the tray icon for Magic mice, keyboards, and trackpads (Bluetooth, and USB HID when Windows exposes it)
- How battery alerts work (percent floor 10 → 5 → 1, plus time warnings): [docs/ALERTS.md](docs/ALERTS.md)
- Persistent 0–1% warning (replace AA batteries, or plug in USB-C / Lightning while the device is still connected)
- Start with Windows
- User-initiated scroll-driver install for **mice** (v1/v2 tealtadpole Boot Camp INF; 0323: KMDF, Patched Apple, or Stock Windows). Trackpads do not get driver radios.
- User-initiated keyboard SDP patch
- Tray Bluetooth menu opens Windows Bluetooth Settings (pair a device, toggle the radio, rename)
- Optional Logitech battery rows (off by default)
- Single self-contained `MagicMouseTray.exe`

The **Battery reads** status item appears only when a Magic Mouse 2024 is connected **and** the bound driver is the patched KMDF package. Hidden otherwise.

Magic Tray does not install a driver until you confirm. No silent rebind. No Magic Utilities binaries.

## Tray menu (1.1.0)

Right-click the tray icon.

| Item | What it does |
|------|-------------|
| *N* devices | Header. Each paired Magic device expands to battery %, driver badge, enable, and a per-device alert. |
| Bluetooth | Opens Windows Bluetooth Settings |
| Low battery threshold | 10% / 5% / 1% (default 10%) |
| Start with Windows | Toggle auto-start on login |
| Show Logitech devices [Off] | Experimental Logitech battery rows. Off by default. |
| Battery reads | Status only. Visible when a 2024 mouse is on the patched KMDF driver. |
| Refresh Now | Force an immediate battery read |
| Diagnostics | Logs, test notification, capture scripts |
| Help/Documentation | How alerts work, this repository, report a bug |
| Quit | Exit the app |

Footer shows **Magic Tray 1.1.0**.

## Supported mice

Catalog in the app: [`KnownMice`](MagicMouseTray/MouseBatteryDevice.cs) (CI: [`EveryKnownMousePid_HasUsbVid05acRow`](MagicMouseTray.Tests/MouseBatteryDeviceTests.cs)). Hardware reports: [docs/TESTED.md](docs/TESTED.md). PID not listed? [Open an issue](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md).

| Model | Bluetooth PID | USB HID | Scroll driver | Battery |
|---|---|---|---|---|
| Magic Mouse 2024 (USB-C) | `0x0323` | `VID_05AC` `PID_0323` | KMDF (recommended), Patched Apple, or Stock Windows from [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) | HID Input report `0x90` on COL02 (`buf[2]`) |
| Magic Mouse v1 | `0x030D` | `VID_05AC` `PID_030D` | [tealtadpole Boot Camp INF](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) | HID Input `0x90` (AA) |
| Magic Mouse v2 | `0x0269` | `VID_05AC` `PID_0269` | Same tealtadpole INF | HID Input `0x90` (Lightning) |

0323 battery is **only** Input `0x90` on COL02. Magic Tray does not use Feature report `0x47` for the 2024 mouse, and does not treat iPhone Hands-Free / WMI as mouse battery. USB battery is whatever HID collection Windows already exposes — not Magic Utilities' "recharge and continue" driver.

## Supported keyboards

Catalog: [`KnownKeyboards`](MagicMouseTray/KeyboardBatteryDevice.cs) (CI: [`EveryKeyboardPid_HasUsbVid05acRow`](MagicMouseTray.Tests/KeyboardBatteryDeviceTests.cs)). Keyboard battery on Bluetooth needs the one-time SDP-cache patch (see [Keyboard battery](#keyboard-battery)). USB HID battery is read when Windows exposes `VID_05AC` + the same PID.


| Model | Bluetooth PID | USB HID | Status |
|---|---|---|---|
| Apple Wireless Keyboard (2011, A1314) ANSI / ISO / JIS | `0x0239` / `0x023A` / `0x023B` (`0x0255` / `0x0256` / `0x0257`) | `VID_05AC` + same PID | Confirmed (`0x0239`) |
| Magic Keyboard (A1644) / ISO | `0x024F` / `0x0250` | `VID_05AC` + same PID | Included, not hardware-tested here |
| Magic Keyboard with Touch ID (A2449) / ISO | `0x0267` / `0x026C` | `VID_05AC` + same PID | Included, not hardware-tested here |
| Magic Keyboard (2021) / Touch ID / Numeric Keypad | `0x029C` / `0x029A` / `0x029F` | `VID_05AC` + same PID | Included, not hardware-tested here |
| Magic Keyboard (2024, USB-C) / Touch ID / Numeric Keypad | `0x0320` / `0x0321` / `0x0322` | `VID_05AC` + same PID | Included, not hardware-tested here |

## Supported trackpads

Battery rows only: percent, enable, threshold, and time alerts. **No** KMDF / Boot Camp radios. **No** tap-to-click, 3-finger, smooth scroll, or other Magic Utilities trackpad gestures.

| Model | Bluetooth PID | USB HID | Battery |
|---|---|---|---|
| Magic Trackpad (v1) | `0x030E` | `VID_05AC` `PID_030E` | AA — 48h toast + death modal |
| Magic Trackpad 2 | `0x0265` | `VID_05AC` `PID_0265` | Lightning — 24h night-before + plug-now |
| Magic Trackpad 2024 (USB-C) | `0x0324` | `VID_05AC` `PID_0324` | USB-C — 24h night-before + plug-now |

Hardware reports: [docs/TESTED.md](docs/TESTED.md). Missing PID: [open an issue](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md).

## Scroll

Scroll needs an Apple mouse filter driver **installed and bound** to that mouse. The tray offers the install after you confirm.

### Magic Mouse v1 / v2 (`030D`, `0269`)

Install the Boot Camp INF from [tealtadpole/MagicMouse2DriversWin11x64](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) (Apple Wireless Mouse folder; INF: [`AppleWirelessMouse.inf`](https://raw.githubusercontent.com/tealtadpole/MagicMouse2DriversWin11x64/master/AppleWirelessMouse/AppleWirelessMouse.inf)).

Right-click the `.inf` → **Install**, or use the tray's driver action. That package is catalog-signed. **Test Mode is not required** for v1/v2.

### Magic Mouse 2024 (`0323`)

The tealtadpole INF does not cover PID `0323`. The 2024 mouse has three lasting driver choices. Magic Tray clones [LesleyMurfin/magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) default branch `main`.

**KMDF (recommended).** Runs:

```text
v2-kmdf-driver/Install-KMDF.cmd
```

If `Install-KMDF.cmd` is missing on `main`, the tray reports it and **stops**. It will not fall back to the patched Apple installer (`v1-binary-patch/installer/Install-MagicMousePatch.ps1`).

**Patched Apple driver.** User-initiated only. Runs `v1-binary-patch/installer/Install-MagicMousePatch.ps1` from the same zip. If that script is missing, the tray **stops** — it will not fall back to KMDF. Scroll and battery are mutually exclusive on this path.

**Stock Windows.** User-initiated unbind to `HidBth` (no KMDF bind, no Apple filter). Runs `v1-binary-patch/installer/Uninstall-MagicMousePatch.ps1` if that script is on the zip. Test Mode is not required.

After install, remove and re-pair the mouse if scroll still does not work.

### Test Mode (0323 KMDF and Patched Apple)

The 2024 KMDF package and the patched Apple `.sys` are **self-signed**, not WHQL. Windows will not load them until Test Mode is on and Memory Integrity (HVCI) is off. Stock Windows does **not** need this. Magic Utilities' drivers are WHQL-signed and work with Secure Boot; these two 0323 paths do not. You do **not** need the one-time F7 "Disable driver signature enforcement" boot.

Do this **before** `Install-KMDF.cmd` or the patched Apple installer:

1. Open an **elevated** Command Prompt.
2. Enable Test Mode:

   ```bat
   bcdedit /set testsigning on
   ```

3. Turn **off** Memory Integrity: Windows Security → Device security → Core isolation details → **Memory integrity** = Off. HVCI blocks self-signed kernel drivers.
4. Reboot. The desktop watermark "Test Mode" is expected.
5. Run `Install-KMDF.cmd` or the patched Apple installer (or confirm the matching tray Driver radio).

To leave Test Mode after a Microsoft-signed driver exists (none is in-tree today):

```bat
bcdedit /set testsigning off
```

Then reboot. Turning Test Mode off while a self-signed 0323 driver is still bound will stop scroll until you sign the driver or turn Test Mode back on.

v1/v2 tealtadpole installs do not need this section.

## Keyboard battery

Apple wireless keyboards declare battery as Input-only in the HID descriptor, so Windows cannot poll it. The one-time PATH-C fix patches the Bluetooth SDP cache (`HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\<MAC>`) so the battery report is a readable Feature report.

This is a registry patch. No kernel driver.

**You must pass the keyboard MAC.** `kbd-patch-cachedservices.ps1` requires `-Mac` (12 hex digits, colons optional). There is no default; omitting `-Mac` does not patch a developer machine.

Find the 12 hex digits (no colons) from Device Manager → Bluetooth → the keyboard → Properties → Details → **Bluetooth device address**, or from the tray when it offers the keyboard patch.

**From a Release:**

1. Download `Install-KeyboardBattery.cmd` and `kbd-patch-cachedservices.ps1` from the same [Release](../../releases) as the exe. Keep them in the same folder.
2. Run elevated, with your MAC:

   ```bat
   Install-KeyboardBattery.cmd -Mac aabbccddeeff
   ```

3. Toggle Bluetooth off/on (or re-connect the keyboard) so Windows re-reads the cache.

**From source:**

```powershell
# Elevated PowerShell — MAC is required:
.\scripts\kbd-patch-cachedservices.ps1 -Mac aabbccddeeff
```

The script backs up the original SDP blobs to `%APPDATA%\MagicMouseTray` (or `%TEMP%\MagicMouseTray`) before writing. **Re-pairing the keyboard erases the patch** — run it again with the same `-Mac` after you remove and re-add the keyboard.

Until the patch is applied, the tray shows that the keyboard needs the SDP-cache patch.

## Building from source

Requires the .NET 8 SDK (Windows).

```powershell
dotnet publish -c Release
# Output: bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\MagicMouseTray.exe
```

The published exe filename stays `MagicMouseTray.exe`. The product name shown in Windows is **Magic Tray**.

KMDF sources and `Install-KMDF.cmd` live in [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) (`v2-kmdf-driver/`). This repo does not vendor that driver.

## Diagnostics

Log file: `%APPDATA%\MagicMouseTray\debug.log`

| Line | Meaning |
|---|---|
| `OK battery=83%` | Successful read |
| `OPEN_FAILED err=5` | COL01 skipped (normal — Windows holds that handle) |
| `DRIVER_CHECK status=...` | Per-device driver health |
| `TOAST_SENT` | Low-battery notification fired |
| `CRITICAL_ALERT_SHOWN` | 1% persistent window shown |

## Releases

CI builds on a `v*` tag (`.github/workflows/release.yml`): restore, test, publish the single-file `win-x64` exe, optionally Authenticode-sign it when `SIGN_PFX_BASE64` / `SIGN_PFX_PASSWORD` secrets exist, and attach:

- `MagicMouseTray.exe`
- `kbd-patch-cachedservices.ps1`
- `Install-KeyboardBattery.cmd`

`FileVersion` / `AssemblyVersion` are `1.1.0.0` (exe Properties → Details).

## License

[MIT](LICENSE)

Copyright (c) 2026 Lesley Murfin.
