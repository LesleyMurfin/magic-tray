# DESIGN — Magic Utilities free parity

Status: Docs + tap vertical implementing (`docs/DESIGN-trackpad-tap.md`). Sources: [magicutilities.net](https://magicutilities.net/), [mouse/features](https://magicutilities.net/magic-mouse/features), [keyboard/features](https://magicutilities.net/magic-keyboard/features), [trackpad/features](https://magicutilities.net/magic-trackpad/features) (fetched 2026-09-01).

Magic Tray is a **free MIT alternative** for battery %, time alerts, and a user-confirmed mouse scroll-driver install. It is **not** a clone of Magic Utilities' proprietary WHQL drivers, gesture suite, trackpad suite, or media/fn/modifier remaps. Do not vendor Magic Utilities binaries or `MagicMouse.sys`.

Official MU sells three paid apps (mouse, keyboard, trackpad) plus shared Bluetooth, USB recharge-and-continue, Boot Camp compatibility, WHQL, and battery alerts. This vertical maps each advertised MU feature to In / Out for the free tray.

## Rule

**In** = HID battery, time alerts, user-initiated mouse driver, keyboard SDP, Bluetooth Settings, USB HID battery when Windows already exposes it, Magic Trackpad tap 1/2/3 and 3-finger tap as middle (our KMDF PTP, not MU).

**Out** = mouse gestures / middle-click modes, desktop swipes, trackpad custom areas / silent click / pressure / 3-finger drag / desktop swipes, media / fn / modifier remaps, MU WHQL drivers, MU binaries.

Trackpads are battery rows (enable + threshold + time alerts). No KMDF / Boot Camp radios on the tray. Tap is the KMDF PTP vertical (`docs/DESIGN-trackpad-tap.md`).

## Matrix

| MU feature (official pages) | Magic Tray | Why |
|---|---|---|
| Mouse battery indicator + customizable alerts (BT + USB) | **In** | Tray % for every known Magic mouse PID. Time alerts (no 10% floor, no evening reminder): AA 48h + death; rechargeable 24h night-before + connected 0–1% plug-now. |
| Keyboard battery indicator + alerts (BT + USB) | **In** | Tray % after the user-run SDP-cache patch on Bluetooth. USB HID battery when Windows exposes `VID_05AC` + that PID. Same time-alert policy (keyboards are AA or rechargeable by model). |
| Trackpad battery indicator + alerts (BT + USB) | **In** | Battery row for Trackpad v1 (`0x030E`, AA), Trackpad 2 (`0x0265`, Lightning), Trackpad 2024 (`0x0324`, USB-C). Enable + threshold + time alerts. No mouse driver radios. No Unknown-mouse warning. |
| Bluetooth for all external Apple input devices | **In** | Discover known BT VID/PID patterns. Tray **Bluetooth** menu opens Windows Bluetooth Settings (pair / radio / rename). The tray does not pair or flip the radio itself. |
| Seamless USB: plug in, recharge, continue to work | **Partial** | **In:** USB HID discovery `VID_05AC` + known PID; battery % if Windows exposes a HID battery collection. **Out:** MU's USB filter that keeps the device working as a wired input device while charging. Magic Mouse USB in MU is "recharge only"; Lightning Mouse is still unusable as a mouse while the cable is in the bottom port. We do not ship a USB continue-to-work driver. |
| User-initiated mouse scroll driver (v1/v2 Boot Camp INF; 0323 KMDF / Patched Apple / Stock) | **In** (mice only) | Confirm-first install. Not MU's driver. Trackpads and keyboards never get these radios. |
| Keyboard SDP patch (PATH-C) so Windows can poll battery | **In** | User-initiated; MAC required. No kernel keyboard driver. |
| Start with Windows | **In** | Tray feature; MU may also offer an auto-start app. |
| Windows 10 + 11 desktop | **In** | 1809+ (build 17763), x64. MU also dropped 32-bit; we never shipped 32-bit or Windows on ARM. |
| Smooth / pixel touch scrolling (mouse + trackpad) | **Out** | Gesture processing. v1/v2 Boot Camp and 0323 KMDF restore ordinary wheel scroll on **mice** only; that is not MU smooth-scroll. |
| Mouse middle-click modes (1-finger middle, 2-finger, wide 2-finger, 3-finger) | **Out** | Proprietary click map. |
| Mouse back / forward horizontal swipe | **Out** | Gesture. |
| Mouse virtual desktop / Task View swipes | **Out** | Gesture. |
| Trackpad tap-to-click (1 / 2 / 3 finger) | **In** | Our KMDF PTP injection for `030E` / `0265` / `0324`. Windows Precision Touchpad: 1-finger primary, 2-finger secondary, 3-finger tap. Not MU. |
| Trackpad 3-finger tap as middle button | **In** | Same PTP vertical. Windows Touchpad three-finger tap = Middle mouse button. 3-finger **drag** stays Out. |
| Trackpad custom button areas | **Out** | Trackpad suite. |
| Trackpad 3-finger dragging | **Out** | Trackpad suite. |
| Trackpad scroll/swipe sensitivity, silent click, click pressure | **Out** | Trackpad suite. |
| Trackpad smooth scroll + back/forward + desktop swipes | **Out** | Same as mouse gestures. |
| Keyboard media keys | **Out** | Remap suite. |
| Keyboard volume keys | **Out** | Remap suite. |
| Keyboard fn lock | **Out** | Remap suite. |
| Keyboard missing standard keys (Ins/Del/Home/End/…) | **Out** | Remap suite. |
| Keyboard Revive Eject | **Out** | Remap suite. |
| Keyboard customizable modifiers (shift/ctrl/win/alt/fn/caps) | **Out** | Remap suite. |
| MU WHQL / Microsoft cross-signed drivers, Secure Boot + Memory Integrity | **Out** | 0323 KMDF and Patched Apple are self-signed (Test Mode + HVCI off). v1/v2 tealtadpole INF is catalog-signed Boot Camp, not MU WHQL. |
| MU binaries / `MagicMouse.sys` / trial that disables scroll | **Out** | CONTRIBUTING.md. Never vendor, link, or rebind. |
| Boot Camp compatible MU stack | **Out** | MU replaces Boot Camp device features until uninstalled. We offer the tealtadpole Boot Camp INF for v1/v2 **mice** only; that is not MU's Boot Camp product. |
| High-DPI / Retina MU app UI | **Out** | Tray icon + WinForms menu. Not MU's retina settings app. |

## Devices (free battery)

USB column is HID `VID_05AC` + the same product ID when Windows enumerates a USB path. Bluetooth stays the existing `000205AC` / `0001004C` rows already in `KnownMice` / `KnownKeyboards`. Do not invent BLE `0001004C` PIDs that are not already in those tables.

| Class | Models | BT PID | USB HID | Tray does |
|---|---|---|---|---|
| Mouse | Magic Mouse 2024 | `0x0323` | `VID_05AC` `PID_0323` | Battery + time alerts + driver radios |
| Mouse | Magic Mouse v1 | `0x030D` | `VID_05AC` `PID_030D` | Battery + AA alerts + Boot Camp / Stock radios |
| Mouse | Magic Mouse v2 | `0x0269` | `VID_05AC` `PID_0269` | Battery + Lightning alerts + Boot Camp / Stock radios |
| Mouse | Apple Wireless Mouse | `0x0310` | `VID_05AC` `PID_0310` | Battery + AA alerts + Boot Camp / Stock radios |
| Keyboard | Every `KnownKeyboards` PID (`0239`/`023A`/`023B`, `024F`/`0250`, `0267`/`026C`, `029A`/`029C`/`029F`, `0320`/`0321`/`0322`, `0255`/`0256`/`0257`) | existing BT rows | `VID_05AC` `PID_<same>` | Battery (SDP on BT) + time alerts. No mouse radios. |
| Trackpad | Magic Trackpad v1 | `0x030E` | `VID_05AC` `PID_030E` | Battery + AA alerts. KMDF PTP tap. No tray radios. |
| Trackpad | Magic Trackpad 2 | `0x0265` | `VID_05AC` `PID_0265` | Battery + Lightning alerts. KMDF PTP tap. No tray radios. |
| Trackpad | Magic Trackpad 2024 | `0x0324` | `VID_05AC` `PID_0324` | Battery + USB-C alerts. KMDF PTP tap. No tray radios. |

Live PC must not regress: `0323` KMDF, `030D` Boot Camp, `0239` keyboard.

## Files

| File | Role |
|---|---|
| `docs/DESIGN-mu-free-parity.md` | This design. |
| `README.md` | Features (no 10% floor, no evening reminder), USB column, Supported trackpads, MIT / not-a-clone. |

USB VID rows live in `MouseBatteryDevice` / `KeyboardBatteryDevice` (UsbVidCoverage). Trackpad menu behavior lives in TrayMenu tests (TrackpadProduct). This vertical does not edit those.

## Non-goals

- C#, tray UI, push, publish.
- Mouse gestures / middle-click modes, trackpad custom areas / silent click / pressure / 3-finger drag / desktop swipes, media / fn / modifier remaps.
- Vendoring Magic Utilities binaries, `MagicMouse.sys`, or `MagicTrackpad.sys`.
- Inventing BLE `0001004C` PIDs absent from the known tables.
- Claiming MU USB "recharge and continue" or WHQL Secure Boot.
