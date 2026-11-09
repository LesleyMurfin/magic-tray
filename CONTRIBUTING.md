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

`main` takes no direct pushes: every change lands through a pull request, and
the checks marked required in
[`.github/workflows/README.md`](.github/workflows/README.md) have to be green
before it can merge. That table is the list; counting them here would only go
stale the next time a workflow is added. A pull request shows more runs than
it gates - anything not marked required there, a non-gating job or a
third-party review bot, does not block the merge button. No required check
filters by path, so every one of them runs on every pull request, whatever
you touched. Where a check has a local equivalent it is the same script CI
runs:

```powershell
dotnet test MagicMouseTray.Tests/MagicMouseTray.Tests.csproj -c Release   # Build and test (needs Windows)
pwsh -File scripts/check-site.ps1                                         # Site checks, if you touched docs/
pwsh -File scripts/check-aeo.ps1                                          # aeo-checks, if you touched docs/ JSON-LD or head metadata
pwsh -File scripts/check-version-sync.ps1                                 # Version sync, if you touched a version string
python3 scripts/check-winget-manifest.py                                  # Winget manifest, if you touched packaging/winget (needs: pip install PyYAML)
python3 scripts/check-workflow-pwsh.py                                    # PowerShell lint: pwsh inside .github/workflows/*.yml (needs: pip install PyYAML)
pwsh -File scripts/check-redaction.ps1                                    # PowerShell lint: the ConvertTo-MacSafeText corpus, if you touched mm-hid-report.ps1
Invoke-ScriptAnalyzer -Path . -Recurse -Settings ./PSScriptAnalyzerSettings.psd1 -Severity Error,Warning
```

`DCO sign-off` fails if any commit you wrote lacks a `Signed-off-by` trailer
(merge commits and bot commits — `dependabot[bot]`, `github-actions[bot]` — are
skipped). Fix it with
`git rebase --signoff origin/main && git push --force-with-lease`.

Do not vendor Magic Utilities binaries. Do not add a silent driver rebind. PATH-A (`Install-MagicMousePatch.ps1` / `applewirelessmouse.sys` binary patch) must not be silent and must not be a KMDF fallback; a user-initiated Patched Apple offer is allowed. KMDF install is `v2-kmdf-driver/Install-KMDF.cmd` from [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) `main` and never falls back to PATH-A.

Driver URLs and package names belong in `DriverPackageCatalog.cs` — do not triplicate them.

## Hardware reports

The device list the app and CI use is `MouseBatteryDevice.KnownMice` and `KeyboardBatteryDevice.KnownKeyboards`.

- **PID already in those tables:** add a row to [docs/TESTED.md](docs/TESTED.md) and open a PR (`git commit -s`).
- **PID not in those tables:** [open a missing-device issue](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md). Do not PR `TESTED.md` for an unknown PID.


## Bug reports

In the tray: **Help → Report a bug**. That copies a MAC-redacted snapshot and opens a GitHub draft; you submit it while logged in. **Help → Request a feature** does the same without diagnostics.

Or include `%APPDATA%\MagicMouseTray\debug.log` and Hardware Ids (`VID_05AC&PID_xxxx`) by hand.

## License

By contributing you agree your work is licensed under the [MIT License](LICENSE).
