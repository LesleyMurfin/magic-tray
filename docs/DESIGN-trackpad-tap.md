# DESIGN — Magic Trackpad tap 1/2/3 and 3-finger middle

Status: Implementing. First vertical is **PTP descriptor injection + Apple→PTP report translation** in the existing KMDF lower filter. Tray install menu is later. Not Magic Utilities.

A paired Magic Trackpad should tap like MU’s feature page, implemented as **our** driver: 1-finger tap = primary, 2-finger tap = secondary, 3-finger tap = middle. Prefer Windows Precision Touchpad so those three taps come from Windows. Fallback (not this vertical): HID mouse-button synthesizer from contact count.

## First vertical (this ship)

Make PIDs `030E` / `0265` / `0324` look like a Windows Precision Touchpad.

1. INF binds those Bluetooth HID hardware IDs to the same `MagicMouseDriver` lower filter as `0323`.
2. SDP `0x0206` rewrite injects a compact Digitizer / Touch Pad descriptor (**not** the 0323 mouse blob).
3. ACL (and IRP_MJ_READ backup) rewrite Apple touch frames into PTP input RID `0x01`.
4. Windows Precision Touchpad then supplies:
   - 1-finger tap → primary
   - 2-finger tap → secondary
   - 3-finger tap → middle, via Windows Touchpad settings **Three-finger gestures → Taps → Middle mouse button** (Win10 1809+ / Win11). 1- and 2-finger taps are the Windows defaults.

Do not vendor MU, `MagicMouse.sys`, or `MagicTrackpad.sys`. Do not touch `GestureEngine.c` / `.h` (0323 mouse surface scroll).

## Why PTP, not a click map

MU’s trackpad page advertises tap-to-click 1/2/3 and 3-finger as middle. A filter that synthesizes mouse buttons can fake that, but it fights Windows (no Precision settings, no two-finger scroll from the OS). Injecting a PTP collection lets `hidclass` bind a Touch Pad and do taps itself.

Certified PTP’s 256-byte vendor `0xC5` blob will **not** fit. Live SDP patch is 1-byte TEXT_STRING: descriptor length `N` must satisfy `6+N ≤ 255` → **N ≤ 249**. This vertical ships an uncertified compact PTP (3 contacts, Contact Count Maximum as **Feature Constant** so hidclass does not GET_FEATURE a report the Apple radio does not have). Same rule as 0323: do not inject Feature `0x47`.

Fallback vertical (named, not built here): keep a mouse descriptor and synthesize Button 1/2/3 from 1/2/3-finger tap (down+up, little movement). Use that only if Windows does not treat the compact PTP as a Touch Pad.

## Linux hid-magicmouse — numeric facts only

No GPL code copy. Facts from `drivers/hid/hid-magicmouse.c` (torvalds/linux):

| Item | Value |
| --- | --- |
| Trackpad v1 PID | `0x030E` |
| Trackpad 2 PID | `0x0265` |
| Trackpad 2024 PID | `0x0324` |
| BT v1 report ID | `0x28` |
| BT v2/v3 report ID | `0x31` |
| USB v2/v3 report ID | `0x02` (USB not bound this vertical) |
| Double-wrap report ID | `0xF7` |
| BT prefix | 4 bytes, then `N×9` touch bytes; `N = (size-4)/9`, `N≤15` |
| USB prefix | 12 bytes, then `N×9` |
| Clicks | `data[1] & 1` (physical click) |
| v1 id | `(tdata[7]<<2 \| tdata[6]>>6) & 0xF` |
| v1 down | `(tdata[8] & 0xF0) != 0` (`TOUCH_STATE_NONE=0x00`, `START=0x30`, `DRAG=0x40`) |
| v2/v3 id | `tdata[8] & 0xF` |
| v2/v3 down | `(tdata[3] & 0xC0) == 0x80` |
| x (v1 and v2) | `(INT)((tdata[1]<<27 \| tdata[0]<<19)) >> 19` (signed 13-bit) |
| y (v1 and v2) | `-((INT)((tdata[3]<<30 \| tdata[2]<<22 \| tdata[1]<<14)) >> 19)` |
| v1 surface | X `[-2909,3167]`, Y `[-2456,2565]`, 130.00×110.00 mm |
| v2/v3 surface | X `[-3678,3934]`, Y `[-2478,2587]`, 160.00×114.90 mm |
| MT enable v1 | Feature `{0xD7,0x01}` |
| MT enable v2/v3 BT | Feature `{0xF1,0x02,0x01}` |

Battery Input RID `0x90` is passed through (same live 0323 fact).

## Injected PTP descriptor

One blob for all three trackpad PIDs. Logical range is the **v2** span (larger); v1 coords are offset+clamped into it.

| Field | Value |
| --- | --- |
| COL01 | Digitizer `0x0D` / Touch Pad `0x05`, Report ID `0x01` |
| Contacts | 3× Finger logical collections: Tip Switch, Confidence, Contact ID, X, Y |
| X logical | `0 .. 7612` (`3934-(-3678)`) |
| Y logical | `0 .. 5065` (`2587-(-2478)`), origin top-left (`yPtp = MAX_Y - y`) |
| Contact Count | Input, max 3 |
| Scan Time | Input, 16-bit, +10 per rewritten frame |
| Button 1 | physical click (`data[1]&1`) |
| Contact Count Maximum | Feature **Constant** 3 (`B1 03`) — not a device GET |
| COL02 | Battery RID `0x90` Input, percent at byte[2] — same as 0323 |

No Input Mode Feature. No `0xC5` checksum. Size **must be ≤ 249**.

Rewritten PTP input (23 bytes):

```
[0]     RID 0x01
[1..6]  finger0  flags(tip,conf) id Xlo Xhi Ylo Yhi
[7..12] finger1
[13..18] finger2
[19]    contact count
[20..21] scan time LE
[22]    buttons (bit0 click)
```

Apple `0x28` / `0x31` / `0xF7` (last inner) / USB `0x02` become this RID `0x01` on the ACL path **before** HidBth parses. Empty 4-byte frames grow to 23; ACL allocations are larger than the received length (same assumption as 0323 6→8).

## KMDF routing (do not break 0323)

`ProductId` from the hardware ID string.

| PID | SDP blob | ACL translate |
| --- | --- | --- |
| `0x0323` or `0` | mouse `g_HidDescriptor` RID `0x12`+`0x90` | `TranslateMouse2ToHid` (GestureEngine) |
| `0x030E` / `0x0265` / `0x0324` | `g_PtpHidDescriptor` | `TranslateTrackpadToPtp` |
| anything else | passthrough | passthrough |

`OnSdpQueryComplete` today refuses every PID except `0323`. Extend the allow-list to the three trackpad PIDs; keep refusing `030D` / `0269` / `0310`.

INF still must **not** bind `030D` / `0269` / `0310`. Adding trackpad HWIDs to the existing install section is the bind. Tray radios stay mouse-only (`DESIGN-trackpad.md`).

## MT enable

Linux sends Feature `{0xD7,0x01}` (v1) or `{0xF1,0x02,0x01}` (v2/v3 BT) so the radio streams `0x28`/`0x31`. Bytes live in `TrackpadPtp.c` (`TrackpadFillMtEnable`). This filter sits **below** HidBth, so HID SET_FEATURE from hidclass never arrives here. Sending a Bluetooth HID SET_REPORT ourselves needs a stashed L2CAP control handle — **not this vertical**. If a live pad only emits mouse frames until that feature, the follow-up is an outbound SET_REPORT, not a new descriptor.

## Paired Magic Trackpad after this vertical

Bluetooth Trackpad v1 / 2 / 2024, KMDF installed (same `Install-KMDF.cmd` as 0323):

- Device Manager: HID-compliant **touch pad** (or Precision Touchpad) on COL01, battery collection still RID `0x90`.
- 1-finger tap: left click (Windows tap-to-click).
- 2-finger tap: right click.
- 3-finger tap: middle click once Windows three-finger tap is **Middle mouse button**.
- Physical click still Button 1.
- 0323 mouse: unchanged RID `0x12` wheel/AC Pan path.

No hardware on this agent: slice tests + descriptor size proof.

## Files

| File | Role |
| --- | --- |
| `docs/DESIGN-trackpad-tap.md` | This design (tray worktree). |
| `docs/DESIGN-trackpad.md` | Tap rows In; tray UI still battery-only this vertical. |
| `docs/DESIGN-mu-free-parity.md` | Tap 1/2/3 and 3-finger tap-as-middle In. |
| `v2-kmdf-driver/TrackpadPtp.h` / `.c` | PTP blob, PID predicate, Apple→PTP translate, MT-enable bytes. |
| `v2-kmdf-driver/InputHandler.c` / `.h` | `SdpRewrite_ProcessEx` so SDP can inject either blob. |
| `v2-kmdf-driver/AclTranslate.c` | Trackpad PID early-return; mouse `0x12` untouched. |
| `v2-kmdf-driver/Driver.c` / `.h` | Allow-list trackpad PIDs; branch SDP/ACL/Read. |
| `v2-kmdf-driver/MagicMouseDriver.inf` | HWIDs `030E` / `0265` / `0324`. |
| `v2-kmdf-driver/MagicMouseDriver.vcxproj` | Compile `TrackpadPtp.c`. |
| `v2-kmdf-driver/tests/test-trackpad-ptp.c` | Userland slice tests. |

Do not edit `GestureEngine.c` / `.h`, `TrayApp.cs`, `BatteryAlertPolicy.cs`, `DriverInstaller.cs`.

## Tests

Linux `gcc` userland, no `.sys`, no full suite:

- `g_PtpHidDescriptorSize ≤ 249` and contains `0x09,0x05` (Touch Pad) and `0x85,0x01`.
- Mouse `g_HidDescriptor` still RID `0x12` + Wheel `0x38` + `0x90` (0323 regression).
- `MmIsTrackpadPid` true for `030E`/`0265`/`0324`, false for `0323`/`030D`.
- v1 `0x28` one down contact → PTP count 1, tip set, X/Y 0-based.
- v2 `0x31` two down contacts → count 2.
- v1 three down contacts → count 3 (Windows can 3-finger tap).
- `data[1]&1` → PTP button bit 0.
- RID `0x90` and mouse `0x12` are not translated as PTP.
- MT-enable bytes: v1 `D7 01`, v2 `F1 02 01`.

## Non-goals

- Custom button areas, silent click, click pressure, 3-finger drag, desktop swipes, smooth-scroll sensitivity.
- Mouse 2/3-finger click (MouseMiddleClick / GestureEngine).
- USB continue-to-work; USB `0x02` is decoded in the translator for tests only; INF is BTHENUM.
- Tray Driver submenu / install click for trackpads.
- PATH-A, `pnputil` from poll/startup, vendoring MU.
- Push. Killing the live tray.
