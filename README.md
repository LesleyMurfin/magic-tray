# Magic Tray

**Free Windows 10/11 app for Apple Magic Mouse, Magic Keyboard, and Magic Trackpad.** Battery percent in the system tray, plus the scroll driver your Magic Mouse actually needs. MIT licensed. No subscription. No trial that kills scroll.

**Documentation and driver guide: [the Magic Tray website](https://magictray.app/)** — [which driver do I need](https://magictray.app/drivers.html), [how to tell your Magic device apart](https://magictray.app/drivers.html#identify), [why Magic Mouse 2024 is different](https://magictray.app/v3.html).

Magic Tray is software you install on Windows. It is not a desk tray, stand, or cradle for Apple keyboards.

Magic Tray is a Windows tray app for **Apple Magic Mouse** — **Magic Mouse v1 (AA batteries)** PID `030D`, **Magic Mouse v2 (Lightning)** PID `0269`, and **Magic Mouse v3 (2024, USB-C)** PID `0323` — plus **Magic Keyboard** and **Magic Trackpad**. It shows battery percent in the tray and can install a scroll driver you confirm. The scroll drivers it points at are **Windows 10 and Windows 11** drivers: Apple's own signed Boot Camp INF for v1 and v2, and the separate KMDF driver for the v3, which Boot Camp never covered. It is a free alternative to [Magic Utilities](https://magicutilities.net/) — not a clone of MU’s proprietary drivers, gestures, trackpad suite, or key remaps.

Released 2 September 2026 · [Download v1.1.0](https://github.com/LesleyMurfin/magic-tray/releases/tag/v1.1.0) · [Website](https://magictray.app/)

If this saved you a paid subscription, **star both repos**: [Magic Tray](https://github.com/LesleyMurfin/magic-tray) and the [v3 Windows driver](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix). Goal: a publicly signed KMDF so Test Mode goes away — [stars & signing](https://magictray.app/funding.html).

![Windows 11 system tray with Magic Tray](docs/screenshot-tray.png)

![Magic Tray 1.1.0 menu: Magic Keyboard, Magic Mouse 2024 (v3) KMDF, Magic Mouse v1](docs/screenshot-menu.png)

---

## Contents

- [Magic Tray vs Magic Utilities](#magic-tray-vs-magic-utilities)
- [Just want a 2024 Magic Mouse driver?](#just-want-a-2024-magic-mouse-driver)
- [Features](#features)
- [Tray menu (1.1.0)](#tray-menu-110)
- [Supported mice](#supported-mice)
  - [What to pick](#magic-mouse-v3-2024-usb-c--what-to-pick)
- [Supported keyboards](#supported-keyboards)
- [Supported trackpads](#supported-trackpads)
- [Scroll](#scroll)
  - [Magic Mouse v1 and v2](#magic-mouse-v1-aa-batteries-and-v2-lightning-030d-0269)
  - [Magic Mouse v3 (2024, USB-C)](#magic-mouse-v3-2024-usb-c-0323)
  - [Which driver you are on](#which-driver-you-are-on-and-what-the-tray-recommends)
  - [Battery on the patched Apple driver](#battery-on-the-patched-apple-driver-the-mode-ab-flip)
  - [Test Mode](#test-mode-self-signed-0323-drivers)
- [Why Magic Mouse v3 scroll breaks on Windows](#why-magic-mouse-v3-scroll-breaks-on-windows)
- [Keyboard battery](#keyboard-battery)
- [FAQ](#faq)
- [Building from source](#building-from-source)
- [Diagnostics](#diagnostics)
- [Enable / disable / recover](docs/ENABLE-DISABLE.md)
- [Test plan](docs/TEST-PLAN.md)
- [Releases](#releases)
- [License](#license)
- [Which driver](https://magictray.app/drivers.html)
- [Why v3 is hard](https://magictray.app/v3.html)
- [Stars & signing](https://magictray.app/funding.html)
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
| Driver signature | v1/v2: Apple's own binary, Microsoft-countersigned on either install route - no Test Mode. **v3 KMDF / Patched Apple are self-signed** (Test Mode + Memory Integrity off). Not WHQL. | WHQL; works with Secure Boot |
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

## Just want a 2024 Magic Mouse driver?

You have the **Magic Mouse v3 (2024, USB-C)** — Apple sells it as just "Magic Mouse", and this project calls it v3 to keep it apart from the older models. Windows moves the pointer, but **the wheel does nothing**. That is normal on both Windows 10 and Windows 11 — neither ships a scroll driver for this mouse.

**What works right now**

Download **Magic Tray** from [Releases](https://github.com/LesleyMurfin/magic-tray/releases/latest) and run it. Battery percent appears as soon as the mouse is paired — no driver, no reboot, no admin. If the wheel is still dead, that is expected: the wheel needs a driver.

**What the wheel needs, honestly**

Scroll on this mouse needs our own **KMDF** driver, and that driver is not Microsoft-signed (not WHQL). Windows will only load it if you put the PC into **Test Mode** and turn **Memory integrity** off — a real security trade-off, plus a permanent desktop watermark. Getting it properly signed so none of that is needed is tracked in [#89](https://github.com/LesleyMurfin/magic-tray/issues/89).

**KMDF is still experimental and you cannot install it from the tray yet.** The driver lives in [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix), and the piece Magic Tray needs in order to install it has not reached that repository’s `main` branch — it is still an open pull request ([#5](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/pull/5)). Until it lands, choosing **KMDF** in the tray tells you it is unavailable and stops. It will not quietly install something else instead.

So: **battery today, scroll when the driver lands.** Two different things have to happen. The tray can only offer KMDF once the installer entrypoint reaches the driver repo’s `main` branch — that is [magic-mouse-v3-windows-fix#5](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/pull/5), and it is what to watch for scroll becoming available at all. Getting rid of Test Mode needs the driver properly signed, which is a separate job tracked in [#89](https://github.com/LesleyMurfin/magic-tray/issues/89).

**The three driver choices for this mouse**

- **KMDF** — the recommended one. Scroll **and** battery together. Self-signed, so it needs [Test Mode](#test-mode-self-signed-0323-drivers). Not installable from the tray yet, as above.
- **Patched Apple** — an old experiment, kept only for the record. Scroll **or** battery, never both at the same time. Also self-signed, so also needs Test Mode. **Do not pick this** unless you specifically want the experiment.
- **Stock Windows** — Windows’ own Bluetooth mouse driver. Pointer only: no scroll. Battery percent usually still reads. No Test Mode, and it removes our driver.

Side-by-side: [what to pick](#magic-mouse-v3-2024-usb-c--what-to-pick).

**Older Magic Mouse** (Lightning or AA batteries): none of the above applies to you. Pick **Boot Camp** in the tray. It is signed by Apple, so there is no Test Mode and no watermark. If you would rather install Apple's binary by hand, there is a [second route](#magic-mouse-v1-aa-batteries-and-v2-lightning-030d-0269) that ends up on the same driver.

---

## Features

- Battery percent on the tray for Magic mice, keyboards, and trackpads
- Alerts: [docs/ALERTS.md](docs/ALERTS.md) (10 → 5 → 1, plus time warnings)
- Persistent 0–1% warning (replace AA, or plug in USB-C / Lightning)
- Start with Windows
- User-initiated scroll-driver install for **mice** (older mice: Boot Camp; 2024 USB-C: KMDF). Trackpads do not get a scroll driver.
- User-initiated keyboard SDP patch
- Driver detection with a recommendation in **every** state: which driver the device is bound to right now, which one is recommended, and a confirmation when that is already the one you are on
- Per-driver expectations for **pointer**, **scroll wheel** and **battery percent**, so you know what a choice costs before you make it
- A configuration check for the conditions the driver you are on depends on, each marked fine, worth knowing, or blocking, with a fix action where there is one
- On the patched Apple driver only: a battery reading you ask for, fetched by briefly flipping the mouse into its battery mode and restoring its scroll mode
- Bluetooth menu opens Windows Bluetooth Settings
- Optional Logitech rows (off by default)
- Single self-contained `MagicMouseTray.exe`

**Battery percent on a Magic Mouse v3 (2024, USB-C)** depends on the driver. On KMDF it reads continuously. On stock Windows it usually still reads. On the patched Apple driver it cannot be read at all while scroll works, so the tray fetches it only when you ask, with the [Mode A/B flip](#battery-on-the-patched-apple-driver-the-mode-ab-flip).

Magic Tray does not install a driver until you pick one and confirm UAC. It never swaps drivers behind your back. The only change it ever makes without you choosing a driver is that Mode A/B flip: you ask for it, it does not change which driver is bound, and it always puts the mouse back.

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
| Help/Documentation | Alerts doc, this repo, report a bug (pre-filled diagnostics), request a feature |
| Quit | Exit |

Footer: **Magic Tray 1.1.0**.

---

## Supported mice

All three mice below are covered on **Windows 10 (1809+) and Windows 11**. Catalog: [`KnownMice`](MagicMouseTray/MouseBatteryDevice.cs) (CI: [`EveryKnownMousePid_HasUsbVid05acRow`](MagicMouseTray.Tests/MouseBatteryDeviceTests.cs)). Reports: [docs/TESTED.md](docs/TESTED.md). PID missing? [Open an issue](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md).

| Model | PID | Recommended driver | Scroll | Battery | Tested |
|---|---|---|---|---|---|
| Magic Mouse v3 (2024, USB-C) | `0x0323` | [**KMDF**](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/tree/main/v2-kmdf-driver) | Yes, with [Test Mode](#test-mode-self-signed-0323-drivers) | Yes | [yes — KMDF](docs/TESTED.md) |
| Magic Mouse v1 (AA batteries) | `0x030D` | [**Boot Camp**](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) | Yes | Yes | [row](docs/TESTED.md) |
| Magic Mouse v2 (Lightning) | `0x0269` | [**Boot Camp**](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) | Yes | Yes | — |
| Apple Wireless Mouse (AA batteries) | `0x0310` | [**Boot Camp**](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) | Yes | Yes | — |

### Magic Mouse v3 (2024, USB-C) — what to pick

| Choice in the tray | Wheel | Battery | Test Mode | Who it is for |
|---|---|---|---|---|
| [**KMDF**](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/tree/main/v2-kmdf-driver) | Yes | Yes | Required | Almost everyone. Recommended, but [not installable from the tray yet](#just-want-a-2024-magic-mouse-driver). |
| [Patched Apple](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/tree/main/v1-binary-patch) | Yes, but only in its scroll mode | Yes, but only in its battery mode - the tray can fetch one reading by [flipping there and back](#battery-on-the-patched-apple-driver-the-mode-ab-flip) | Required | Old experiment. Wheel and battery are mutually exclusive - one or the other, never both. Do not pick this. |
| Stock Windows | No | Often yes | Not needed | Pointer only. Hands the mouse back to Windows and removes our driver. |

---

## Supported keyboards

Catalog: [`KnownKeyboards`](MagicMouseTray/KeyboardBatteryDevice.cs) (CI: [`EveryKeyboardPid_HasUsbVid05acRow`](MagicMouseTray.Tests/KeyboardBatteryDeviceTests.cs)). Bluetooth battery needs the [SDP patch](#keyboard-battery). USB HID battery is read when Windows exposes `VID_05AC` + the same PID.

| Model | PID | Driver | Scroll | Battery | Tested |
|---|---|---|---|---|---|
| Apple Wireless Keyboard (2011, A1314) ANSI / ISO / JIS | `0x0239` / `0x023A` / `0x023B` (`0x0255` / `0x0256` / `0x0257`) | [SDP patch](scripts/kbd-patch-cachedservices.ps1) (not a kernel driver) | n/a | Yes after patch | [yes — `0x0239`](docs/TESTED.md) |
| Magic Keyboard (A1644) / ISO | `0x024F` / `0x0250` | [SDP patch](scripts/kbd-patch-cachedservices.ps1) | n/a | Yes after patch | Not hardware-tested here |
| Magic Keyboard with Touch ID (A2449) / ISO | `0x0267` / `0x026C` | [SDP patch](scripts/kbd-patch-cachedservices.ps1) | n/a | Yes after patch | Not hardware-tested here |
| Magic Keyboard (2021) / Touch ID / Numeric Keypad | `0x029C` / `0x029A` / `0x029F` | [SDP patch](scripts/kbd-patch-cachedservices.ps1) | n/a | Yes after patch | Not hardware-tested here |
| Magic Keyboard (2024, USB-C) / Touch ID / Numeric Keypad | `0x0320` / `0x0321` / `0x0322` | [SDP patch](scripts/kbd-patch-cachedservices.ps1) | n/a | Yes after patch | Not hardware-tested here |

---

## Supported trackpads

Battery only: percent, enable, threshold, time alerts. **No** KMDF / Boot Camp radios. **No** Magic Utilities trackpad gestures.

| Model | PID | Driver | Scroll | Battery | Tested |
|---|---|---|---|---|---|
| Magic Trackpad (AA batteries) | `0x030E` | None | n/a | Yes (AA) | — |
| Magic Trackpad 2 (Lightning) | `0x0265` | None | n/a | Yes (Lightning) | — |
| Magic Trackpad (2024, USB-C) | `0x0324` | None | n/a | Yes (USB-C) | — |

Hardware reports: [docs/TESTED.md](docs/TESTED.md). Missing PID: [open an issue](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md).

---

## Scroll

Scroll needs an Apple mouse filter driver **installed and bound** on Windows 10 or Windows 11. The tray offers the install after you confirm. Which driver depends on the PID: v1, v2 and the Apple Wireless Mouse take Apple's signed `applewirelessmouse.sys` filter, which has two install routes, and only the Magic Mouse v3 (2024, USB-C) needs the KMDF driver.

**What enables scroll, and what enables battery, per model and per driver: [docs/DRIVER-MATRIX.md](docs/DRIVER-MATRIX.md).** One table for scroll, one for battery, one row per driver on the 2024 mouse, with the measured evidence behind each cell and every unmeasured cell labelled as such.

### Magic Mouse v1 (AA batteries) and v2 (Lightning) (`030D`, `0269`)

Two routes put the same Apple-signed `applewirelessmouse.sys` on these mice. They differ in how the driver gets installed, not in what runs afterwards, and **neither needs Test Mode**.

**Route 1 - Apple's Boot Camp INF.** Install the INF from [tealtadpole/MagicMouse2DriversWin11x64](https://github.com/tealtadpole/MagicMouse2DriversWin11x64) (Apple Wireless Mouse folder). Right-click the `.inf` -> **Install**, or use the tray. Catalog-signed, and this is the route Magic Tray itself can run for you.

**Route 2 - the manual service install.** [sbagirici/apple-magic-mouse-scroll-fix-windows](https://github.com/sbagirici/apple-magic-mouse-scroll-fix-windows) copies the same Apple WHQL-signed `applewirelessmouse.sys` into place, creates the kernel service by hand, and injects that filter name into `LowerFilters` on the device's instance key. Nothing about it depends on the mouse matching a hardware ID in Apple's INF, which lists only `030D`, `0310` and `0269` - that INF-matching step is exactly what this route bypasses. **Magic Tray does not install this route**: you run it yourself, and the tray then reads the result the same way it reads any other bound filter.

Either way the binary keeps its Microsoft countersignature, so Test Mode is irrelevant to both routes. The tray decides that from the signatures on the file, not from your PID and not from how you installed - see [Test Mode](#test-mode-self-signed-0323-drivers).

### Magic Mouse v3 (2024, USB-C) (`0323`)

The tealtadpole Boot Camp INF does **not** cover PID `0323`. Scroll on this mouse needs the separate **KMDF** driver from [LesleyMurfin/magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix), which is self-signed and therefore needs [Test Mode](#test-mode-self-signed-0323-drivers).

**Availability.** The tray’s KMDF install step does not work yet. The installer entrypoint it looks for is still an open pull request on the driver repo ([#5](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/pull/5)) and is not on that repo’s `main` branch. Pick **KMDF** today and the tray reports exactly that, then stops — it never installs a different driver instead, and it never rebinds your mouse silently.

When that entrypoint does land, the tray will take a snapshot of the driver repo’s `main` branch and run the installer after you confirm UAC. That snapshot is the current branch tip rather than a pinned, checksum-verified release, which is a further reason KMDF is labelled experimental. Signing the driver properly — which would also retire Test Mode — is tracked in [#89](https://github.com/LesleyMurfin/magic-tray/issues/89).

**Stock Windows** hands the mouse back to Windows’ own Bluetooth HID driver. The pointer keeps working, the wheel does not, and no Test Mode is needed. Battery percent usually still reads, but Magic Tray’s dedicated **Battery reads** path needs KMDF.

**Patched Apple** is a leftover binary patch of Apple's old filter driver. It is self-signed too, so it needs the same Test Mode setup as KMDF, and on it scroll and battery are mutually exclusive - you get one or the other, never both. The tray never swaps between these driver choices on its own: if the driver you picked is missing, it says so and stops. The one thing it can do on this driver, and only when you ask, is the brief [Mode A/B flip](#battery-on-the-patched-apple-driver-the-mode-ab-flip) that fetches a battery reading and puts the mouse straight back - that is a mode within this driver, not a change of driver.

If the wheel still does nothing after a KMDF install succeeds: Bluetooth settings → remove the mouse → pair it again. Windows sometimes keeps the old binding.

### Which driver you are on, and what the tray recommends

Magic Tray reads the live Bluetooth HID stack and tells you which driver the device is actually bound to right now.

| Device | What the tray tells apart |
|---|---|
| Magic Mouse v3 (2024, USB-C) `0323` | Three ways: **KMDF**, **Patched Apple**, or **Stock Windows** - plus the case where our driver is on the PC but not bound to this mouse. |
| Magic Mouse v1 `030D`, v2 `0269`, Apple Wireless Mouse `0310` | Two ways: Apple's **Boot Camp** filter bound, or not bound / not installed. There is no third driver for these. |
| Magic Keyboard, Magic Trackpad | One story: no scroll driver exists for them and none is needed. For keyboards the tray reports instead whether the [battery SDP patch](#keyboard-battery) has been applied. |

In **every** one of those states the tray shows a one-line recommendation - not only when nothing is bound. If you are already on the driver it would recommend, the line confirms that instead of nagging you.

For each driver you could pick it also states what to expect from the three things people actually notice: the **pointer**, the **scroll wheel** and the **battery percent**. On the patched Apple driver, scroll and battery are shown as one-or-the-other rather than as working, because that is the truth of that driver. Those are expectations for a choice, not a live measurement of your PC - what is working right now is reported separately per device ([docs/ENABLE-DISABLE.md](docs/ENABLE-DISABLE.md)).

Alongside that, the tray reports the machine-level conditions the driver you are on depends on, each marked as fine, worth knowing, or blocking, with the action that fixes it where there is one.

Seeing a recommendation is not the tray acting on it. Nothing here installs, removes or rebinds a driver.

### Battery on the patched Apple driver: the Mode A/B flip

This applies only to a Magic Mouse v3 (2024, USB-C) that is bound to the **Patched Apple** driver. On KMDF and on stock Windows, none of it happens.

That driver has two modes and cannot be in both at once. In its scroll mode the wheel works and the battery percent cannot be read at all. In its battery mode the percent can be read and the wheel is dead. So a reading costs scroll for as long as the mouse stays in the battery mode, which is exactly why Magic Tray never leaves it there.

When you ask for a battery reading, the tray flips the mouse into its battery mode, reads the percent, and flips it straight back to the scroll mode. Plainly: **the pointer and the scroll wheel stop for a few seconds** while Windows rebuilds the mouse, the whole cycle needs **one administrator approval** - one for the cycle, not one per step - and the tray **always** puts the mouse back. The restore runs even when the reading itself fails, and it is not reported as done until the tray has re-read the mouse and confirmed the scroll mode is really back.

If that confirmation fails, the tray says so instead of claiming success, and tells you scroll may still be dead. It also records that a cycle was started and not confirmed finished, so if the app is killed or the PC restarts mid-flip, the next start offers you a one-click restore of the scroll mode. Failure modes in full: [docs/ENABLE-DISABLE.md](docs/ENABLE-DISABLE.md).

This flip happens only when you ask for it, and it never changes which driver your mouse is bound to.

### Test Mode (self-signed 0323 drivers)

Both 0323 driver choices — **KMDF** and **Patched Apple** — are **self-signed**, not WHQL. Windows will not load either of them until Test Mode is on and Memory integrity is off. The “Test Mode” desktop watermark is expected. Magic Utilities’ paid drivers are WHQL and skip all of this; ours cannot yet ([#89](https://github.com/LesleyMurfin/magic-tray/issues/89)).

You do **not** need the F7 “Disable driver signature enforcement” boot.

**How the tray decides whether any of this applies to your mouse.** Not from the PID, and not from the route you installed by. It reads the Authenticode signers of the driver file the device actually depends on, and only raises a Test Mode or Memory integrity fact when **every** signature on that file was issued by the file itself (`MagicMouseTray/SystemConfigChecker.cs:36-56`, `:760-794`). Measured on the reference PC, 2026-09-15:

- `MagicMouseDriver-kmdf-204-scroll.sys` (v3 KMDF) carries one signature, `CN=MagicMouseFix`, self-issued. Self-signed, so Test Mode on and Memory integrity off really are its prerequisites and the tray says so.
- `applewirelessmouse.sys` (v1/v2, on either install route) carries a `WDKTestCert` self-issued primary signature **plus two valid `CN=Microsoft Windows Hardware Compatibility Publisher` signatures**. Windows loads a file when **any** of its signatures is trusted, so that binary loads with nothing switched off and the tray raises no signing fact for it. The first signature alone would have given the wrong answer here, which is why every signature on the file is read.
- A signature the tray cannot read - no embedded signature at all (stock `HidBth.sys` is catalog-signed only), no file, or a failed read - is reported as **unknown**. Unknown never claims a driver is self-signed and never produces a blocking fact.

**Read this before you change anything.** These are real, machine-wide trade-offs:

- **Secure Boot blocks Test Mode.** With Secure Boot on, `bcdedit /set testsigning on` is refused. Turning Secure Boot off in firmware is the only way round it, and some games with anti-cheat and some dual-boot setups stop working afterwards.
- **BitLocker.** Changing boot settings can trigger a BitLocker recovery prompt on the next start. Suspend BitLocker first (Control Panel → **BitLocker Drive Encryption** → Suspend protection) and have your recovery key to hand.
- **Memory integrity off lowers your security** for as long as it stays off, not just while you install.

Do this **before** you pick **KMDF** or **Patched Apple**:

1. Suspend BitLocker and note your recovery key.
2. Elevated Command Prompt: `bcdedit /set testsigning on`
3. Windows Security → Device security → Core isolation → **Memory integrity** = Off
4. Reboot. The “Test Mode” desktop watermark is expected.
5. Now pick the driver in the tray and confirm UAC.

**Putting the PC back to normal.** First switch the mouse to **Stock Windows** (or otherwise remove the self-signed driver) — turning Test Mode off while a self-signed 0323 driver is still bound just kills scroll, because Windows refuses to load it. Then:

1. Elevated Command Prompt: `bcdedit /set testsigning off`
2. Windows Security → Device security → Core isolation → **Memory integrity** = **On**
3. Re-enable Secure Boot in firmware if you turned it off.
4. Reboot so all of that takes effect, then resume BitLocker protection if it is still suspended.

v1 and v2 skip this section entirely, on **both** install routes: the binary is Apple's own and keeps its Microsoft countersignature, so nothing here applies to it.

---

## Why Magic Mouse v3 scroll breaks on Windows

The Magic Mouse v3 (2024, USB-C, PID `0323`) is not in Apple’s old Boot Camp INF. On Windows the HID stack can collapse collections (often called Mode A vs Mode B): scroll and battery fight over the same device. Stock Windows often has battery and no scroll; a patched Apple filter can restore scroll and drop battery.

**Research and the KMDF fix live in a separate repo** (this tray repo does not vendor kernel sources):

- [LesleyMurfin/magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) — the KMDF driver and the patched Apple path
- [HID research (RID 0x90, collections, DSM)](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/blob/main/v1-binary-patch/docs/hid-research.md)
- [Bug analysis (Mode A/B)](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/blob/main/v1-binary-patch/docs/bug-analysis.md)
- [Mode A/B diagram](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/blob/main/v1-binary-patch/docs/diagrams/diagram-mode-ab.md)

Magic Tray’s recommended path is KMDF from that repo: scroll **and** battery (Input `0x90` on COL02), once its installer entrypoint reaches `main`.

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

**Does the Magic Mouse v3 (2024, USB-C) work on Windows 10 and Windows 11?**  
Battery, yes — Magic Tray 1.1.0 shows the percent with no driver at all. Scroll needs the **KMDF** driver from [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix), which is self-signed and so needs [Test Mode](#test-mode-self-signed-0323-drivers) with Memory integrity off — and its tray install step is [not available yet](#just-want-a-2024-magic-mouse-driver). Battery is HID Input `0x90` on COL02.

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
| `DRIVER_CHECK status=... scm=running\|stopped` | Per-device driver health. `scm=stopped` means the filter .sys is not loaded even if the INF name still says KMDF. |
| `DEVICE_ENABLE pid=… sidecar=ok\|failed\|no-instances` | Tray enable/disable result |
| `TOAST_SENT` | Low-battery notification |
| `CRITICAL_ALERT_SHOWN` | 1% persistent window |

After a dead wheel, a failed enable, or a charge/unplug: tray **Diagnostics → Run diagnose-and-recover.ps1**, or elevated `.\scripts\diagnose-and-recover.ps1 -Repair` to restart a stopped-but-bound Bluetooth filter. Do not unpair the v3 for that. Workflow: [docs/ENABLE-DISABLE.md](docs/ENABLE-DISABLE.md). Hardware checklist: [docs/TEST-PLAN.md](docs/TEST-PLAN.md).

`capture-state.ps1` is still a **0323-only** pre/post-reboot compare. Multi-device capture is `diagnose-and-recover.ps1`.

---

## Releases

CI builds on a `v*` tag: test, publish win-x64, optional Authenticode (`SIGN_PFX_*`), then attach `MagicMouseTray.exe`, `kbd-patch-cachedservices.ps1`, `Install-KeyboardBattery.cmd`, `capture-state.ps1`, `diagnose-driver.ps1`, `diagnose-and-recover.ps1`, `mm-bt-stack-snapshot.ps1`, and `SHA256SUMS`. `scripts/verify-release.ps1` must pass before the GitHub Release is created.

`FileVersion` / `AssemblyVersion` are `1.1.0.0`.

---

## Credits

The LowerFilter sandwich (app → Windows HID → **filter** → Bluetooth → mouse) is from [sbagirici/apple-magic-mouse-scroll-fix-windows](https://github.com/sbagirici/apple-magic-mouse-scroll-fix-windows). That Architecture diagram is why the v1/v2 scroll path is understandable. **Please star their repo.** We redraw it on [v3.html](https://magictray.app/v3.html#stack). Their installer is for v1/v2 with Apple’s signed `applewirelessmouse.sys`. Magic Mouse 2024 (`0323`) still uses KMDF from [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix).

v1/v2 Boot Camp INF packaging: [tealtadpole/MagicMouse2DriversWin11x64](https://github.com/tealtadpole/MagicMouse2DriversWin11x64).

---

## License

[MIT](LICENSE) · Copyright (c) 2026 Lesley Murfin.
