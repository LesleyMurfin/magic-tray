# Magic Tray

**Free Windows 10 and 11 app for Apple Magic Mouse, Magic Keyboard and Magic Trackpad.** It shows the battery percent next to your clock, and it helps you fix one-finger scrolling on a Magic Mouse. MIT licensed. No subscription, no trial.

Magic Tray is software you install on Windows. It is not a desk tray, stand, or cradle for Apple keyboards.

Released 2 September 2026 · [Download v1.1.0](https://github.com/LesleyMurfin/magic-tray/releases/tag/v1.1.0) · [magictray.app](https://magictray.app/)

The guides live on the website:

| I want to | Page |
|---|---|
| See my battery percent | [See my battery](https://magictray.app/battery.html) |
| Work out which device I own | [Which device?](https://magictray.app/devices.html) |
| Make my mouse scroll | [Make it scroll](https://magictray.app/drivers.html) |
| Get my keyboard battery to show up | [Keyboard battery](https://magictray.app/keyboard.html) |
| Know why the 2024 mouse is hard | [The 2024 mouse](https://magictray.app/v3.html) |
| Help pay for a driver Microsoft has checked | [Support](https://magictray.app/funding.html) |

![Windows 11 system tray with Magic Tray](docs/screenshot-tray.png)

![Magic Tray 1.1.0 menu: Magic Keyboard, Magic Mouse 2024, Magic Mouse v1](docs/screenshot-menu.png)

---

## Contents

- [What Magic Tray does about drivers](#what-magic-tray-does-about-drivers)
- [Magic Tray vs Magic Utilities](#magic-tray-vs-magic-utilities)
- [Install](#install)
- [Features](#features)
- [Tray menu (1.1.0)](#tray-menu-110)
- [Mice it knows](#mice-it-knows)
- [Keyboards it knows](#keyboards-it-knows)
- [Trackpads it knows](#trackpads-it-knows)
- [Keyboard battery](#keyboard-battery)
- [Help us test](#help-us-test)
- [Scrolling](#scrolling)
- [FAQ](#faq)
- [Building from source](#building-from-source)
- [Diagnostics](#diagnostics)
- [Releases](#releases)
- [Credits](#credits)
- [License](#license)
- [llms.txt](llms.txt) (for agents)

---

## What Magic Tray does about drivers

A driver is the software Windows uses to talk to your mouse. Magic Tray never changes one on its own. You pick the item in the tray menu, and a dialog asks you first.

What happens after you confirm depends on the device:

| Your device | Menu item | What the tray then does |
|---|---|---|
| Magic Mouse 2024, USB-C, `0323` | `KMDF` | Nothing yet. The community-built driver isn't published, so this reports that it can't find it |
| Magic Mouse 2024, USB-C, `0323` | `Patched Apple driver` | Runs the patched-Apple route. You get scrolling **or** battery, never both. Unconfirmed |
| Magic Mouse 2024, USB-C, `0323` | `Stock Windows` | Puts the standard Windows driver back |
| Magic Mouse v1 `030D`, v2 `0269`, Apple Wireless Mouse `0310` | `Boot Camp` | Opens the download page. You run the file yourself |
| Magic Mouse v1 `030D`, v2 `0269`, Apple Wireless Mouse `0310` | `Stock Windows` | Puts the standard Windows driver back |
| Magic Trackpad, AA batteries, `030E` | `Boot Camp` | Opens the download page. You run the file yourself |
| Magic Trackpad `0265`, Magic Trackpad `0324` | none | Refuses any driver change. Battery percent only |
| Magic Keyboard family | `Fix battery reads` | Runs the one-time battery unlock |

Two things Magic Tray **cannot** do for you. The patched-Apple route needs a Windows startup setting that allows drivers Microsoft has not checked, and it needs Memory integrity turned off. You change both yourself, by hand, and the app never touches them. What that costs you is on [Make it scroll](https://magictray.app/drivers.html#cost).

The `KMDF` item is wired up and waiting, but the driver it installs [isn't finished](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/tree/main/v2-kmdf-driver). Until it ships, a 2024 mouse gets battery percent from the tray and no scrolling fix worth recommending. Helping that along is what [Support](https://magictray.app/funding.html) is for.

---

## Magic Tray vs Magic Utilities

| | Magic Tray (free, MIT) | Magic Utilities (paid) |
|---|---|---|
| Mouse battery in the tray | Yes | Yes |
| Keyboard battery | Yes, after a one-time unlock you run | Yes |
| Trackpad battery | Yes. No scroll or gesture driver. | Yes, plus a paid trackpad suite |
| Battery warnings | Yes, at 10, 5 and 1 percent. See [docs/ALERTS.md](docs/ALERTS.md). | Customizable percent alerts |
| Help with mouse scrolling | Yes, after you pick it and confirm | Yes, with its own drivers |
| Has Microsoft checked the driver? | The older mice use Apple's driver, and Microsoft's check on it still passes. The 2024 mouse driver has not been checked, so it needs that Windows startup setting. | Yes. Works with Secure Boot on. |
| Gestures, trackpad tap, media-key remaps | **Not shipped** | Yes |
| Subscription, or a trial that turns scrolling off | Never | Required after the trial |

If you want gestures or the trackpad suite, buy [Magic Utilities](https://magicutilities.net/). If you want battery percent and mouse scrolling without a subscription, use Magic Tray.

---

## Install

1. Download `MagicMouseTray.exe` from [Releases](https://github.com/LesleyMurfin/magic-tray/releases/latest).
2. Double-click it. There's no installer, and it doesn't need admin.
3. Pair your device in Windows Bluetooth settings. The icon shows the percent.

**You need:** 64-bit Windows 10 version 1809 (build 17763) or newer, or any Windows 11.

Windows may warn you on the first run, because nobody has paid for the check that stops that warning. Click **More info**, then **Run anyway**. You can also right-click the file, open **Properties**, and click **Unblock**.

Where the percent shows up, and what to do when it doesn't: [See my battery](https://magictray.app/battery.html#read).

---

## Features

- Battery percent in the tray for Magic mice, keyboards and trackpads
- Warnings at 10, 5 and 1 percent, plus time-to-empty warnings: [docs/ALERTS.md](docs/ALERTS.md)
- A persistent 0 to 1 percent warning, so you can swap the AA batteries or plug the cable in
- Start with Windows
- Help with mouse scrolling, after you pick it and confirm
- The one-time keyboard battery unlock, after you confirm
- A menu item that opens Windows Bluetooth settings
- Optional Logitech rows, off by default
- One self-contained `MagicMouseTray.exe`

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
| Battery reads | Status only. Shown when a Magic Mouse 2024 (`0323`) is connected and bound to the community-built driver. |
| Refresh Now | Immediate battery read |
| Diagnostics | Logs, test notification, capture scripts |
| Help/Documentation | Alerts doc, this repo, report a bug |
| Quit | Exit |

Footer: **Magic Tray 1.1.0**.

---

## Mice it knows

All of these work on Windows 10 (1809+) and Windows 11. Not sure which one you have? [Turn it over and check](https://magictray.app/devices.html#identify).

Catalog: [`KnownMice`](MagicMouseTray/MouseBatteryDevice.cs) (CI: [`EveryKnownMousePid_HasUsbVid05acRow`](MagicMouseTray.Tests/MouseBatteryDeviceTests.cs)).

| Model | Code Windows shows | For scrolling | Battery | Confirmed by a tester? |
|---|---|---|---|---|
| Magic Mouse 2024 (USB-C) | `0323` | The [community-built driver](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/tree/main/v2-kmdf-driver), which the tray installs after you confirm | Yes | Yes |
| Magic Mouse v1 (AA batteries) | `030D` | [Apple's own driver](https://github.com/tealtadpole/MagicMouse2DriversWin11x64), which you download and run | Yes | Yes |
| Magic Mouse v2 (Lightning) | `0269` | [Apple's own driver](https://github.com/tealtadpole/MagicMouse2DriversWin11x64), which you download and run | Yes | Should work, nobody has confirmed it yet |
| Apple Wireless Mouse (AA batteries) | `0310` | [Apple's own driver](https://github.com/tealtadpole/MagicMouse2DriversWin11x64), which you download and run | Yes | Should work, nobody has confirmed it yet |

The 2024 mouse has three choices in the menu, and they aren't equal:

| Menu item | Scrolls | Battery | Needs the Windows startup setting |
|---|---|---|---|
| `KMDF`, the community-built driver | Yes | Yes | Yes |
| `Patched Apple driver` | One or the other, never both | One or the other, never both | Yes |
| `Stock Windows` | No | Usually yes | No |

Pick the first one. The second is an old experiment we kept for the record. Why this mouse is the awkward one: [The 2024 mouse](https://magictray.app/v3.html).

Your code isn't in the table? [Tell us about it](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md).

---

## Keyboards it knows

Your Magic Keyboard keeps the standard Windows Bluetooth driver. Nothing gets swapped. The battery percent needs the [one-time unlock](https://magictray.app/keyboard.html).

Catalog: [`KnownKeyboards`](MagicMouseTray/KeyboardBatteryDevice.cs) (CI: [`EveryKeyboardPid_HasUsbVid05acRow`](MagicMouseTray.Tests/KeyboardBatteryDeviceTests.cs)).

| Model | Code Windows shows | Battery | Confirmed by a tester? |
|---|---|---|---|
| Apple Wireless Keyboard (2011, A1314) ANSI / ISO / JIS | `0239` / `023A` / `023B` (`0255` / `0256` / `0257`) | Yes, after the unlock | Yes, `0239` |
| Magic Keyboard (A1644) / ISO | `024F` / `0250` | Yes, after the unlock | Should work, nobody has confirmed it yet |
| Magic Keyboard with Touch ID (A2449) / ISO | `0267` / `026C` | Yes, after the unlock | Should work, nobody has confirmed it yet |
| Magic Keyboard (2021) / Touch ID / Numeric Keypad | `029C` / `029A` / `029F` | Yes, after the unlock | Should work, nobody has confirmed it yet |
| Magic Keyboard (2024, USB-C) / Touch ID / Numeric Keypad | `0320` / `0321` / `0322` | Yes, after the unlock | Should work, nobody has confirmed it yet |

---

## Trackpads it knows

Battery percent, the enable switch, the warning level and the time warnings. There's no scroll or gesture driver for a trackpad.

| Model | Code Windows shows | Driver the tray offers | Battery | Confirmed by a tester? |
|---|---|---|---|---|
| Magic Trackpad (AA batteries) | `030E` | `Boot Camp` opens [the download page](https://github.com/tealtadpole/MagicMouse2DriversWin11x64/tree/master/AppleWirelessTrackpad). You run the file. | Yes | Should work, nobody has confirmed it yet |
| Magic Trackpad 2 (Lightning) | `0265` | None. The tray refuses any driver change. | Yes | Should work, nobody has confirmed it yet |
| Magic Trackpad (2024, USB-C) | `0324` | None. The tray refuses any driver change. | Yes | Should work, nobody has confirmed it yet |

Reports so far: [docs/TESTED.md](docs/TESTED.md). Which trackpad is yours: [Which device?](https://magictray.app/devices.html#kbtrackpad).

---

## Keyboard battery

Your keyboard keeps the standard Windows Bluetooth driver. There's no Apple software to install, and no Windows startup setting to change. One unlock turns the percent on.

- In the tray, open your keyboard and click **Fix battery reads**. Magic Tray finds the keyboard's Bluetooth address on its own, asks your permission, then runs the unlock.
- Prefer to run it yourself? Every release ships `Install-KeyboardBattery.cmd`. Right-click it and pick **Run as administrator**.
- Turn Bluetooth off and on again, and the percent appears.
- Re-pairing the keyboard erases the unlock. Run **Fix battery reads** once more. Until you do, the tray lists the keyboard and shows the battery as blocked.

Full page, including what the unlock changes: [Keyboard battery](https://magictray.app/keyboard.html#unlock).

Running the script from a clone rather than a release needs the keyboard's Bluetooth address:

```powershell
.\scripts\kbd-patch-cachedservices.ps1 -Mac aabbccddeeff
```

`-Mac` is required and has no default. Device Manager → Bluetooth → your keyboard → Details → **Bluetooth device address**.

---

## Help us test

Three devices have ever been confirmed by a tester: Magic Mouse 2024 `0323`, Magic Mouse v1 `030D`, and Apple Wireless Keyboard 2011 `0239`. Everything else in the app should work, and nobody has confirmed it yet.

These eight are the ones we need:

| Device | Code Windows shows |
|---|---|
| Magic Keyboard | `024F` / `0250` |
| Magic Keyboard with Touch ID | `0267` / `026C` |
| Magic Keyboard (2021) | `029C` |
| Magic Trackpad (AA batteries) | `030E` |
| Magic Trackpad 2 (Lightning) | `0265` |
| Magic Trackpad (2024, USB-C) | `0324` |

Bluetooth Magic Keyboards are what we're shortest of. Tell us your model and its code, your Windows version, and whether a percent showed up. For a keyboard, say what you saw before the unlock and after it.

[Send a test report](https://github.com/LesleyMurfin/magic-tray/issues/new?template=test-report.md). "It didn't work" is as useful as "it worked". Reports so far: [docs/TESTED.md](docs/TESTED.md).

Magic Tray shows **nothing at all** for your device? That's a different form: [tell us here](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md).

---

## Scrolling

Windows moves the pointer on a Magic Mouse without any help. One-finger scrolling needs a driver, and which one depends on your mouse.

- **Magic Mouse v1, v2 and the Apple Wireless Mouse.** Apple wrote a Windows driver for these years ago and it still works. Apple calls it Boot Camp. The tray's `Boot Camp` item opens [tealtadpole's download page](https://github.com/tealtadpole/MagicMouse2DriversWin11x64), and you install the file from there. Nothing about Windows changes. Eight steps, with pictures of each: [the easy path](https://magictray.app/drivers.html#v1v2).
- **Magic Mouse 2024 (USB-C).** Apple's old driver doesn't cover this one, so scrolling needs a different driver. The community-built one isn't finished yet, so there's nothing to download for it today. One other route exists, a patched version of Apple's driver, and it makes you choose: scrolling or battery percent, never both. It also needs a Windows startup setting changed and Memory integrity off, and nobody has confirmed it works yet. What's happening and why: [the hard path](https://magictray.app/drivers.html#v3).

Battery percent works on every one of these mice with no driver at all.

Nothing scrolling after an install? [Work through this list](https://magictray.app/drivers.html#stuck).

Getting the 2024 driver checked by Microsoft would remove the startup setting for everyone. That costs money the project doesn't have: [#89](https://github.com/LesleyMurfin/magic-tray/issues/89) and [Support](https://magictray.app/funding.html).

---

## FAQ

**Does the Magic Mouse 2024 (USB-C) work on Windows 10 and Windows 11?**  
The battery percent does, with no driver at all. Scrolling needs the community-built driver, plus a Windows startup setting you change yourself. [The 2024 mouse](https://magictray.app/v3.html) explains why.

**Will Magic Tray change a driver on its own?**  
No. You pick the item, a dialog asks you, and only then does anything happen. For the older mice and the AA trackpad it opens a download page and installs nothing.

**Can Magic Tray turn that Windows startup setting on for me?**  
No, and it can't turn Memory integrity off either. Both are yours to change. [What that costs you](https://magictray.app/drivers.html#cost).

**Is this Magic Utilities?**  
No. Free, MIT, no subscription. No Magic Utilities files, gestures, trackpad suite or key remaps.

**Will scrolling stop when a trial expires?**  
No. There's no trial.

**Do trackpads get scrolling or tap-to-click?**  
No. Battery percent and warnings only.

**My keyboard shows no percent.**  
Run the one-time unlock: [Keyboard battery](https://magictray.app/keyboard.html#unlock).

---

## Building from source

Requires the .NET 8 SDK (Windows).

```powershell
dotnet publish -c Release
# Output: bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\MagicMouseTray.exe
```

The exe filename stays `MagicMouseTray.exe`. The product name is **Magic Tray**.

KMDF sources live in [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) (`v2-kmdf-driver/`). This repo does not vendor that driver. `DriverInstaller.OfferV3KmdfInstallAsync` snapshots that repo's default branch and runs `v2-kmdf-driver/Install-KMDF.cmd` elevated after the user's OK. The snapshot is the branch tip, not a pinned checksum-verified release. If that script is not on the branch, the install throws and never falls back to `v1-binary-patch/installer/Install-MagicMousePatch.ps1`.

The v1/v2 and `030E` paths never call `pnputil` to install: `OfferV1V2ScrollFix` and `OfferTrackpadV1BootCamp` open the documented GitHub page and stop. `OfferV1V2StockRestore` unbinds `applewirelessmouse` on that PID only, leaving `HidBth`.

There is no `bcdedit` call anywhere in the app. Test signing and HVCI are the user's own manual steps.

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

CI builds on a `v*` tag: test, publish win-x64, optional Authenticode (`SIGN_PFX_*`), package, verify, then create the Release. `scripts/verify-release.ps1` must pass first, and it fails the build on a malformed archive rather than shipping one.

**The primary asset is `MagicTray-<tag>-win-x64.zip`.** Download that, not the bare exe. Its layout exists to satisfy the script resolvers in `DriverInstaller.FindKeyboardPatchScript` and `DiagnosticScripts`, which probe `<exe folder>/scripts/<name>` first:

```
MagicMouseTray.exe
scripts/kbd-patch-cachedservices.ps1
scripts/Install-KeyboardBattery.cmd
scripts/capture-state.ps1
scripts/diagnose-driver.ps1
scripts/mm-bt-stack-snapshot.ps1
README.txt
SHA256SUMS
```

Take the exe out of that folder on its own and the keyboard battery unlock and the whole Diagnostics menu stop resolving. The loose files are still attached for existing links, plus a `.sha256` sidecar for the archive.

`winget install MagicTray` installs the same archive as a portable package. Manifests and the submission flow are in [`packaging/winget/`](packaging/winget/README.md).

The shipped release is **v1.1.0**, and that is the version the app footer and the website state. `MagicMouseTray/MagicMouseTray.csproj` currently carries `1.1.1` / `1.1.1.0`, which is unreleased. Note `verify-release.ps1` gates the tag against that `<Version>`, so the next tag must be `v1.1.1` or the csproj must be changed to match whatever you tag.

### Why the app is unsigned, and what fixes it

Signing secrets are empty, so v1.1.0 shipped without the Microsoft check and Windows shows a SmartScreen warning on first run. The plain-language version of this is on [the funding page](https://magictray.app/funding.html); the mechanics are here because they are developer work, not user work.

| Level | What it takes | What it removes |
|---|---|---|
| Unsigned (today) | nothing | nothing. SmartScreen warns on the app; the 2024-mouse driver needs test signing on and HVCI off |
| Authenticode OV or EV | a code-signing certificate from a public CA in a verified name, renewed yearly | the app's SmartScreen warning. EV clears it immediately, OV needs reputation to build |
| Driver attestation | an EV certificate plus a Microsoft Partner Center account | lets the driver load with no test signing and no HVCI change |
| WHQL | attestation plus passing the Hardware Lab Kit test suite | Windows Update distribution |

The pipeline is already wired: `scripts/sign-app.ps1` runs on `v*` tags when `SIGN_PFX_BASE64` and `SIGN_PFX_PASSWORD` exist. A self-signed PFX is not enough; it must be a real CA-issued certificate in a verified name, or Windows treats it as untrusted.

---

## Credits

The LowerFilter sandwich (app → Windows HID → **filter** → Bluetooth → mouse) is from [sbagirici/apple-magic-mouse-scroll-fix-windows](https://github.com/sbagirici/apple-magic-mouse-scroll-fix-windows). That diagram is why the v1/v2 scroll path is understandable. **Please star their repo.** We redraw it on [the 2024 mouse page](https://magictray.app/v3.html#sbagirici). Their installer is for v1/v2 with Apple's signed `applewirelessmouse.sys`. The Magic Mouse 2024 (`0323`) needs a different driver, and the KMDF one in [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) is [still being built](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/tree/main/v2-kmdf-driver) — nothing to install there yet.

v1/v2 Boot Camp INF packaging: [tealtadpole/MagicMouse2DriversWin11x64](https://github.com/tealtadpole/MagicMouse2DriversWin11x64).

HID research behind the 2024 mouse: [HID research](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/blob/main/v1-binary-patch/docs/hid-research.md), [bug analysis](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/blob/main/v1-binary-patch/docs/bug-analysis.md), [Mode A/B diagram](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/blob/main/v1-binary-patch/docs/diagrams/diagram-mode-ab.md).

Image credits and licences for the photographs on the website: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

If Magic Tray saved you a subscription, star [Magic Tray](https://github.com/LesleyMurfin/magic-tray) and the [2024 mouse driver](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix). Other ways to help: [Support](https://magictray.app/funding.html#help).

---

## License

[MIT](LICENSE) · Copyright (c) 2026 Lesley Murfin.
