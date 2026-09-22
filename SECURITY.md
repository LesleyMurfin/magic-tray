# Security policy

Magic Tray is a free MIT-licensed Windows tray app maintained by one person. It writes to
`HKLM`, it can patch a Bluetooth SDP `CachedServices` record, and it ships PowerShell scripts
that are meant to be run elevated. Those are exactly the places a real bug hurts, so please
report anything you find.

## Supported versions

| Version | Supported |
|---|---|
| v1.1.0 (latest release) | Yes |
| Anything older | No |

Only the latest release line gets fixes, and a fix ships as a new release rather than a patch
to an old tag. If you are on an older build, update before reporting:
[Releases](https://github.com/LesleyMurfin/magic-tray/releases/latest).

## Reporting a vulnerability

Use GitHub private vulnerability reporting:

**https://github.com/LesleyMurfin/magic-tray/security/advisories/new**

That opens a private advisory visible only to you and the maintainer. Please do **not** open a
public issue for anything exploitable, and do not put it in a bug report from the tray's
**Help → Report a bug**, which creates a public draft.

Helpful to include: Magic Tray version (tray footer), Windows build (`winver`), which script or
menu item is involved, and the smallest reproduction you have. There is no security email
address and no PGP key for this project — the advisory form is the channel.

What to expect from a solo maintainer:

- **Acknowledgement within 7 days.**
- An assessment, and a fix plan or a reasoned decline, in the advisory thread.
- Credit in the advisory and release notes if you want it.

No bounty is offered. Please give a fix a reasonable window before disclosing publicly.

## In scope

- **The tray app** (`MagicMouseTray`) — privilege handling, the user-initiated driver install
  paths, anything it writes under `HKLM`, and the local log at `%APPDATA%\MagicMouseTray`.
- **The registry / SDP patch script** `scripts/kbd-patch-cachedservices.ps1`, which edits the
  cached Bluetooth service record for a paired Magic Keyboard.
- **The elevated diagnostic scripts** `scripts/capture-state.ps1`,
  `scripts/mm-bt-stack-snapshot.ps1`, `diagnose-driver.ps1` and
  `scripts/repair-magicmouse-channel.ps1` — including anything they leak into a snapshot that
  was supposed to be redacted, and the one elevated `pnputil /restart-device` the last of those
  performs on the live Bluetooth instance.
- **Release artifacts** — `MagicTray-<tag>-win-x64.zip`, the `SHA256SUMS` file inside it, the
  `.zip.sha256` sidecar, the winget manifests under `packaging/winget/`, and the workflows that
  produce them.

## Out of scope

- **Apple's Boot Camp mouse driver** and other third-party driver packages the app only links
  to (`tealtadpole/MagicMouse2DriversWin11x64`, `sbagirici/apple-magic-mouse-scroll-fix-windows`,
  `LesleyMurfin/magic-mouse-v3-windows-fix`). Report those to their own maintainers. Magic Tray
  never installs a driver without you choosing the menu item and confirming the dialog.
- **Magic Utilities** and any other vendor software. No Magic Utilities files ship here.
- **The SmartScreen warning on an unsigned build.** Known and documented — see
  [README](README.md#why-the-app-is-unsigned-and-what-fixes-it).
- **Needing test signing on and Memory integrity off** for the patched-Apple scroll route. That
  is a documented cost you choose and change yourself; the app never touches either setting.

## Verifying what you downloaded

Every release ships a `SHA256SUMS` file covering each staged file inside the archive, plus a
`MagicTray-<tag>-win-x64.zip.sha256` sidecar next to the archive on the release page. Check the
archive hash before you run anything:

```powershell
Get-FileHash .\MagicTray-v1.1.0-win-x64.zip -Algorithm SHA256
```

Compare it with the `.sha256` sidecar. Authenticode signing is wired into the release workflow
and runs when the signing certificate secrets are present; it is not guaranteed for every
release, and v1.1.0 shipped unsigned. The signature lives on the exe inside the archive, not on
the ZIP, so extract it first and check the extracted `MagicMouseTray.exe`:

```powershell
Expand-Archive .\MagicTray-v1.1.0-win-x64.zip -DestinationPath .\MagicTray-v1.1.0
Get-AuthenticodeSignature .\MagicTray-v1.1.0\MagicMouseTray.exe | Format-List Status, SignerCertificate
```

A hash that does not match the sidecar, or a signature from a name other than the maintainer's,
is worth a private advisory.
