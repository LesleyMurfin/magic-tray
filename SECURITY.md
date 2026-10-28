# Security policy

Magic Tray is a free MIT-licensed Windows tray app maintained by one person. It writes to
`HKLM`, it can patch a Bluetooth SDP `CachedServices` record, it ships PowerShell scripts
that are meant to be run elevated, and — on your explicit confirmation — it turns a Windows
security feature off. Those are exactly the places a real bug hurts, so please report anything
you find.

## The app can lower this PC's security, and here is exactly when

State this plainly because it is the most sensitive thing the app does. The self-signed driver a
Magic Mouse 2024 needs will not load on a stock Windows install, so the tray offers **Fix driver
signing**. Accepting that one dialog lets the app, in a single elevated pass:

- suspend BitLocker for one reboot (`manage-bde -protectors -disable <SystemDrive> -rebootcount 1`,
  the drive Windows booted from, which is not `C:` on every PC), so the boot-configuration change
  does not trip a recovery-key prompt. The suspend is attempted only when that drive really is
  protected; if it is protected and the suspend fails, the app stops there and changes nothing
  rather than rewriting boot settings;
- turn **Memory integrity (HVCI)** off by writing
  `HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity\Enabled=0`
  — this disables a real kernel security feature;
- run `bcdedit /set testsigning on`, which makes Windows load kernel drivers nobody has checked;
- reboot the PC.

All of that is **one** confirmation dialog listing every step, and **one** UAC prompt. There is
no second question in the middle of the sequence, by design: splitting it would mean several UAC
prompts for one decision the user already made. Nothing here runs from a poll, from a menu open,
or from reading the facts the tray displays. The one thing that may begin at launch is the single
"continue the pending fix?" question after the firmware trip, and only because the user's own
earlier confirmation wrote the marker that reaches it.

**Secure Boot is firmware and the app cannot change it.** There is no Windows API for it, and
`bcdedit /set testsigning on` is refused while it is on ("The value is protected by Secure Boot
policy"). The only thing the app does about it is `shutdown /r /fw`, which restarts the PC into
its UEFI firmware setup screen so you can switch it off there yourself. The app persists a
pending-fix marker, and after that reboot asks once whether to continue with the remaining steps;
declining clears the marker. Any claim that Magic Tray disables Secure Boot is false.

**Everything is revertible, and the revert is offered whenever there is something to undo** — test
signing on, or Memory integrity configured off, which includes the half-applied state where HVCI
is already off and `bcdedit` was refused and the revert is the only way back: `bcdedit /set
testsigning off`, Memory integrity written back on. The revert suspends BitLocker for its own one
restart exactly as the fix does, because it rewrites the BCD too and an active TPM seal turns that
restart into a 48-digit recovery prompt; protectors are active again when Windows comes back.
Secure Boot you turn back on in the same firmware screen.

**Reaching any of that without the confirmation dialog is a reportable bug.** A build that
writes the HVCI value, runs either `bcdedit` command, suspends BitLocker, or reboots the PC
without the user having accepted that dialog — or that asks for a second UAC prompt it did not
name — is in scope for a security advisory, and so is a dialog that fails to list a step the
sequence then performs.

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
- **The driver-signing fix** — the elevated script behind **Fix driver signing**, its revert, the
  pending-fix marker it stores through the firmware reboot, and the confirmation dialog that
  gates all of it. Anything that performs a step the dialog did not name, performs any step
  without the dialog, or lets a stale marker resume a sequence the user declined.
- **The registry / SDP patch script** `scripts/kbd-patch-cachedservices.ps1`, which edits the
  cached Bluetooth service record for a paired Magic Keyboard.
- **The elevated diagnostic scripts** `scripts/capture-state.ps1`,
  `scripts/mm-bt-stack-snapshot.ps1` and `diagnose-driver.ps1` — including anything they leak
  into a snapshot that was supposed to be redacted.
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
- **The security downgrade itself** — test signing on, Memory integrity off, BitLocker suspended
  for one reboot — when it followed the **Fix driver signing** confirmation described above. That
  is a documented cost you chose, it is listed step by step before anything runs, and the revert
  is offered whenever there is anything to undo. What *is* in scope is any path that reaches one
  of those changes, or a reboot, **without** that confirmation.

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
