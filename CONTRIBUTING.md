# Contributing to Magic Tray

Magic Tray is a free MIT-licensed Windows tray app for Apple Magic Mouse and Magic Keyboard: battery in the tray, user-initiated scroll-driver install, and a keyboard SDP patch. No subscription.

## Developer Certificate of Origin

All contributions must be signed off with the DCO. Add a `Signed-off-by` line:

```
git commit -s -m "your commit message"
```

This certifies that you wrote the code or have the right to contribute it under the MIT license. Full text: https://developercertificate.org

## Pull requests

1. Fork the repo and branch from `main`.
2. Test on Windows 10 1809+ or Windows 11 with a paired Apple Magic Mouse or Magic Keyboard.
3. Sign your commits (`git commit -s`).
4. Open a PR that says what changed and why.

Do not vendor Magic Utilities binaries. Do not add a silent driver rebind. PATH-A (`Install-MagicMousePatch.ps1` / `applewirelessmouse.sys` binary patch) must not be silent and must not be a KMDF fallback; a user-initiated Patched Apple offer is allowed. KMDF install is `v2-kmdf-driver/Install-KMDF.cmd` from [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) `main` and never falls back to PATH-A.

Driver URLs and package names belong in `DriverPackageCatalog.cs` — do not triplicate them.

## Hardware reports

If Magic Tray works (or fails) on a mouse, keyboard, or trackpad you own, add a row to [docs/TESTED.md](docs/TESTED.md) and open a PR. Include PID, Bluetooth vs USB, tray version, battery (`ok` / `no reading`), and driver badge. Sign the commit (`git commit -s`).

## Bug reports

Include `%APPDATA%\MagicMouseTray\debug.log` and the device Hardware Ids from Device Manager (look for `VID_004C&PID_xxxx` or `VID_05AC&PID_xxxx`).

## License

By contributing you agree your work is licensed under the [MIT License](LICENSE).
