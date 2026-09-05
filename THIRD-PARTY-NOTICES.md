# Third-Party Notices

Magic Tray is licensed under the MIT License (see `LICENSE`). This file credits
third-party work whose *techniques* were studied and independently reimplemented.
No third-party source code was copied; only the documented protocol/technique was
reused, and device IDs were used as numeric facts only.

## HID++ 2.0 battery negotiation technique — Ithilias/logitray (MIT)

The HID++ 2.0 feature-negotiation technique used by `LogitechBatteryDevice.cs` (Root
feature lookup `0x0000`, then `BatteryStatus 0x1000` / `UnifiedBattery 0x1004` queries)
was reimplemented in C# from the MIT-licensed project **Ithilias/logitray**. No source
was copied. Upstream license confirmed MIT.

## Device VID/PID identifiers — Linux kernel `hid-ids.h` (GPL-2.0)

Apple Magic Mouse / Trackpad / Keyboard USB vendor/product IDs were taken as **numeric
facts only** from the Linux kernel `hid-ids.h` recon catalog. Numeric identifiers are
uncopyrightable facts (Feist v. Rural). **No kernel code, comments, macro names, or
table selection/arrangement was copied.** All descriptor/read logic is original work or
pre-existing in-repo code.

## Excluded source

- **gozaltech/mkBatteryChecker** — no license / all-rights-reserved. Not reusable;
  excluded. No code or technique was taken from it.
- **Magic Utilities** — proprietary. Not reused. Magic Tray does not ship, link, or
  rebind Magic Utilities binaries.

## MIT technique credits (battery read methods, host project remains MIT)

- fixtan/MagicKeyBattery (MIT), hank1101444/WinMagicBattery (MIT) — Apple keyboard
  battery-read approach informed the existing in-repo keyboard reader.
- tealtadpole/MagicMouse2DriversWin11x64 — Boot Camp INF package used as the v1/v2
  scroll-driver install source (not copied into this repo).
- LesleyMurfin/magic-mouse-v3-windows-fix — KMDF installer cloned at runtime for
  Magic Mouse 2024 (`v2-kmdf-driver/Install-KMDF.cmd`).

## Device photographs (site images)

The photographs shipped in `docs/img/` and `docs/magic-mouse-v2-lightning.jpg` are **not**
MIT-licensed and are **not** covered by this project's `LICENSE`. Each one keeps the licence
listed below, and each carries the credit it requires.

On the pages themselves the credits are consolidated: one `.credit` line under each photo grid
names every author and licence in that grid, which satisfies the same obligation with less
clutter than a caption under every image. This file is the authoritative per-file record.

Licence texts:

- CC BY-SA 4.0 — <https://creativecommons.org/licenses/by-sa/4.0>
- CC BY 4.0 — <https://creativecommons.org/licenses/by/4.0>
- CC0 1.0 — <https://creativecommons.org/publicdomain/zero/1.0/>

### `docs/img/magic-mouse-v1-underside-aa-door.jpg`

- Author: FASTILY
- Licence: CC BY-SA 4.0 — <https://creativecommons.org/licenses/by-sa/4.0>
- Source: <https://commons.wikimedia.org/wiki/File:Magic_Mouse_3_2019-02-16.jpg>
- Required credit: `Photo: FASTILY, via Wikimedia Commons, CC BY-SA 4.0`
- Shows: underside of a Magic Mouse v1 (PID `030D`), AA battery door, no charging port.

### `docs/img/magic-mouse-v1-cover-off-aa.jpg`

- Author: FASTILY
- Licence: CC BY-SA 4.0 — <https://creativecommons.org/licenses/by-sa/4.0>
- Source: <https://commons.wikimedia.org/wiki/File:Magic_Mouse_2_2019-05-06.jpg>
- Required credit: `Photo: FASTILY, via Wikimedia Commons, CC BY-SA 4.0`
- Shows: a Magic Mouse v1 with the battery door lifted off, exposing the AA bay. (The Commons
  filename says "Magic Mouse 2"; the pixels show the AA-battery v1, so the filename in this
  repo follows the pixels.)

### `docs/img/magic-mouse-v2-underside-lightning-alt.jpg`

- Author: Syced
- Licence: CC0 1.0 (public domain dedication) — <https://creativecommons.org/publicdomain/zero/1.0/>
- Source: <https://commons.wikimedia.org/wiki/File:Bad_design_-_Apple_Magic_Mouse_2,_unusable_when_charging_4.jpg>
- Required credit (optional under CC0, given anyway):
  `Photo: Syced, via Wikimedia Commons, CC0`
- Shows: underside of a Magic Mouse v2 (PID `0269`) — no battery door, a cable plugged in, and
  the etched `Model A1657`. The plug body covers the port, so this photo does **not** show the
  port's shape; identification rests on the model number.

### `docs/img/magic-mouse-v2-model-number-a1657.jpg`

- Author: Syced
- Licence: CC0 1.0 (public domain dedication) — <https://creativecommons.org/publicdomain/zero/1.0/>
- Source: <https://commons.wikimedia.org/wiki/File:Bad_design_-_Apple_Magic_Mouse_2,_unusable_when_charging_4.jpg>
- Required credit (optional under CC0, given anyway):
  `Photo: Syced, via Wikimedia Commons, CC0`
- **Modification:** a crop of the file above, made by this project so the etched `Model: A1657`
  text is readable. CC0 carries no share-alike obligation, so cropping a CC0 file creates no
  downstream licence duty. No CC BY-SA file in this repo has been cropped.

### `docs/magic-mouse-v2-lightning.jpg`

- Author: Syced
- Licence: CC0 1.0 (public domain dedication) — <https://creativecommons.org/publicdomain/zero/1.0/>
- Source: <https://commons.wikimedia.org/wiki/File:Bad_design_-_Apple_Magic_Mouse_2,_unusable_when_charging_3.jpg>
- Required credit (optional under CC0, given anyway):
  `Photo: Syced, via Wikimedia Commons, CC0`
- Same CC0 series as the two files above (`..._3.jpg` rather than `..._4.jpg`). Pre-existing
  image; it lives at the `docs/` root rather than in `docs/img/`.

### `docs/img/magic-keyboard-2011-aa-top.jpg`

- Author: Karl432
- Licence: CC BY-SA 4.0 — <https://creativecommons.org/licenses/by-sa/4.0>
- Source: <https://commons.wikimedia.org/wiki/File:Apple_Wireless_Keyboard_(German_QWERTZ_layout)_2012.jpg>
- Required credit: `Photo: Karl432, via Wikimedia Commons, CC BY-SA 4.0`
- Shows: an Apple Wireless Keyboard (AA batteries, PID `0239`), battery tube along the top edge.

### `docs/img/magic-keyboard-2011-aa-battery-tube.jpg`

- Author: Roadmr
- Licence: CC BY-SA 4.0 — <https://creativecommons.org/licenses/by-sa/4.0>
- Source: <https://commons.wikimedia.org/wiki/File:Apple-wireless-keyboard-aluminum-2007-side-view.jpg>
- Required credit: `Photo: Roadmr, via Wikimedia Commons, CC BY-SA 4.0`
- Shows: the end of the keyboard, where a round slotted cap unscrews to reach the AA batteries.

### `docs/img/magic-keyboard-rechargeable-top.jpg`

- Author: Fletcher
- Licence: CC BY 4.0 — <https://creativecommons.org/licenses/by/4.0>
- Source: <https://commons.wikimedia.org/wiki/File:Apple_Magic_Keyboard_-_US.jpg>
- Required credit: `Photo: Fletcher, via Wikimedia Commons, CC BY 4.0`
- Shows: a rechargeable Magic Keyboard from above.

### `docs/img/magic-trackpad-2010-aa-battery-tube.jpg`

- Author: Raimond Spekking
- Licence: CC BY-SA 4.0 — <https://creativecommons.org/licenses/by-sa/4.0>
- Source: <https://commons.wikimedia.org/wiki/File:Apple_Magic_Trackpad-3881.jpg>
- **The author mandates this exact credit string. Reproduce it character for character:**
  `© Raimond Spekking / CC BY-SA 4.0 (via Wikimedia Commons)`
- Shows: a Magic Trackpad from 2010 (PID `030E`); a round cap on the back edge unscrews to
  reach the AA batteries.

### `docs/img/magic-trackpad-2-rear-lightning.jpg`

- Author: **`Josh Kehn at English Wikipedia`** — this exact author string is mandated by the
  file's attribution requirement and must be reproduced character for character. (Commons
  records the upload under the name Joshua Kehn; the required credit string is the one above.)
- Licence: CC BY-SA 4.0 — <https://creativecommons.org/licenses/by-sa/4.0>
- Source: <https://commons.wikimedia.org/wiki/File:Magic_Trackpad_2.jpg>
- Required credit — the author string verbatim, plus the licence:
  `Josh Kehn at English Wikipedia, CC BY-SA 4.0`
- Shows: a rechargeable Magic Trackpad seen from the front, with a small port in the middle of
  the near edge. No cable is in frame. The connector type is **not** identifiable — Lightning
  and USB-C are not distinguishable here — so neither this file nor the site ever names it, and
  "rechargeable" is the only generation claim the pixels support. (The `-lightning` in the
  filename is inherited from the upstream file name and is **not** evidence.)

### Modification status, and why the site itself is not CC BY-SA

Every photograph above is included **unmodified apart from resizing and EXIF stripping**, with
the one exception noted in its own entry: `magic-mouse-v2-model-number-a1657.jpg` is a crop of a
**CC0** original, which carries no share-alike obligation. Nothing under CC BY-SA has been
cropped, recoloured, or composited.

All visible cropping on the site happens at display time, through CSS `object-fit` /
`object-position` in `docs/site.css`. The stored pixels are untouched, so that is presentation,
not adaptation.

Publishing these photographs alongside the site's own text is a **Collection** in the sense of
CC BY-SA 4.0 §1(f), not an **Adapted Work**. Share-alike therefore attaches to each photograph
on its own and does **not** reach the pages, the stylesheet, or the application source. Magic
Tray stays MIT.

**If you later crop, recolour, annotate, or composite one of the CC BY-SA files**, that new
image *is* an adaptation: you must release it under CC BY-SA 4.0 (or a later compatible
version), keep the author credit, and state what you changed. The safe alternatives are to
reframe in CSS as above, or to start from one of the CC0 files.

### Known gap: no free photo of the Magic Mouse v3 (2024, USB-C)

No CC0 or CC BY photograph of the 2024 USB-C Magic Mouse (PID `0323`, model A3204) underside
exists on Wikimedia Commons — `Category:Magic Mouse` was enumerated in full and every file was
checked. The site therefore shows a labelled placeholder in that slot instead of a photo of a
different mouse. Apple's own product renders are **not** usable; they are all-rights-reserved.
A v1 or v2 photo must never be captioned as a v3.

## Trademarks

Apple, Magic Mouse, Magic Keyboard, Magic Trackpad, Boot Camp, and macOS are trademarks of
Apple Inc., registered in the U.S. and other countries. Windows is a trademark of the Microsoft
group of companies. Those names are used here only to identify the hardware and the operating
systems this software works with. **Magic Tray is an independent project. It is not affiliated
with, authorised, sponsored, or endorsed by Apple Inc. or by Microsoft.** No Apple software,
driver binary, artwork, or product photograph is redistributed by this project.
