# Map

Path index of this repo. Device catalog is code, not this table (`AGENTS.md`, `llms.txt`).

| Path | What |
|---|---|
| `MagicMouseTray/` | WPF tray app, C# / .NET 8 (`MagicMouseTray.csproj`, TFM `net8.0-windows10.0.17763.0`) |
| `MagicMouseTray/MouseBatteryDevice.cs` | `KnownMice` catalog (mice and trackpads) |
| `MagicMouseTray/KeyboardBatteryDevice.cs` | `KnownKeyboards` catalog |
| `MagicMouseTray/DriverInstaller.cs` | User-confirmed driver offers; KMDF miss returns Unavailable |
| `MagicMouseTray/DriverPackageCatalog.cs` | Driver URLs and package names |
| `MagicMouseTray/SystemConfigChecker.cs` | Bound-driver signature read; unknown never claims an unchecked driver |
| `MagicMouseTray.Tests/` | Unit tests (Windows-only TFM) |
| `docs/` | magictray.app pages plus operator docs (`ALERTS.md`, `DRIVER-MATRIX.md`, `TESTED.md`, `ENABLE-DISABLE.md`, `TEST-PLAN.md`, `SEARCH.md`) |
| `design/` | Design notes (parity, tray UI, drivers, trackpad, alerts). Not the shipped catalog |
| `specs/` | `github-drafts.md`, `outreach-drafts.md`, `webmaster-setup.md` |
| `scripts/` | Release verify/package, site and version checks, keyboard SDP patch, diagnose/recover |
| `packaging/winget/` | Winget manifests for portable `MagicTray` 1.1.0 |
| `.github/workflows/` | Required PR checks plus release and winget-submit (`README.md` in that folder names the current set) |
| `.github/ISSUE_TEMPLATE/` | Bug, feature, missing-device, test-report |
| `diagnose-driver.ps1` | Repo-root diagnostic script |
| `PSScriptAnalyzerSettings.psd1` | PowerShell lint rules |
| `templates/` | Issue and report body templates |
| `.context/` | This context pack: `current.md`, `decisions.md`, `history.md`, `map.md` |
| `.aco/` | ACO maintainer kit (triage prompt, cost and graph notes, probe sample). Never commit `graph.db` |
| `.ai/skills/aco-triage/` | Portable ACO triage skill and graceful-degrade verifier |
| `.ai/hooks/`, `.ai/validators/` | Optional fail-open session probe and doc validators |
| `AGENTS.md` | Agent charter (inlined, not a pointer), product constraints, ACO contract, PR hygiene |
| `llms.txt` | Agent-oriented product notes |
| `README.md` | User manual in the repo: install, devices, drivers, releases |
| `CONTRIBUTING.md` | DCO, PR checks, hardware-report rules |
| `SECURITY.md` | Supported version v1.1.0; private advisories |
| `LICENSE` | MIT, Copyright (c) 2026 Lesley Murfin |
| `THIRD-PARTY-NOTICES.md` | Technique credits and photograph licences |
