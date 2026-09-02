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
