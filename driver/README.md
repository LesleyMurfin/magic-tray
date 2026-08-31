# Drivers do not live here

`LesleyMurfin/magic-tray` is the **free tray client** (battery, alerts, driver SELECT). It does **not** vendor `MagicMouseDriver` source, INF, vcxproj, or `.sys`.

The tray **pulls** a public package when the user selects one:

| Device | Upstream |
|--------|----------|
| Magic Mouse 0323 | https://github.com/sbagirici/apple-magic-mouse-scroll-fix-windows (`driver/applewirelessmouse.sys`) |
| Magic Mouse 030D / 0310 / 0269 | https://github.com/tealtadpole/MagicMouse2DriversWin11x64 (`AppleWirelessMouse/`) |
| Magic Keyboard | Apple Boot Camp `Keymagic2.inf` via https://github.com/timsutton/brigadier |

Do not copy leftover WSL `install-driver.ps1` / `mm-dev.ps1` into this tree. Do not pull `LesleyMurfin/magic-mouse-v3-windows-fix` as the package source.
