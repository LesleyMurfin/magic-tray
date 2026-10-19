<!-- ≤80 lines. One bullet per record (≤~3 lines), tagged DEC-/FAIL-/HANDOFF-. Merge duplicates; collapse superseded handoffs. Look up: rg -n "DEC-|FAIL-|HANDOFF-" decisions.md (+ keyword), read only matches. No transcripts or secrets. -->

# Decisions

- DEC-001: Product name is Magic Tray; published exe is `MagicMouseTray.exe`. Free, MIT, Windows 10 1809+ (build 17763) and Windows 11, x64. Source: `README.md`, `AGENTS.md`, `LICENSE`, `llms.txt`.
- DEC-002: 0323 battery is HID Input report `0x90` on COL02 only. Never Feature `0x47`, never WMI as a mouse battery source. Source: `AGENTS.md` Hard product constraints; `llms.txt`.
- DEC-003: The tray never runs `bcdedit` and never silently enables Test Mode or disables HVCI. Those stay manual. Source: `AGENTS.md`; `README.md` Building from source.
- DEC-004: Driver changes are user-confirmed. A missing KMDF package returns Unavailable and must not fall back to patching `applewirelessmouse.sys` or to PATH-A. Source: `AGENTS.md`; `CONTRIBUTING.md`; `README.md`; `llms.txt`.
- DEC-005: Device catalog is `MouseBatteryDevice.KnownMice` and `KeyboardBatteryDevice.KnownKeyboards`, not README prose. Unknown PIDs use the missing-device issue, not a `docs/TESTED.md` row. Source: `AGENTS.md`; `llms.txt`; `CONTRIBUTING.md`; `docs/TESTED.md`.
- DEC-006: KMDF sources stay in `LesleyMurfin/magic-mouse-v3-windows-fix`. This repo does not vendor that driver. As of 2026-09-16, `v2-kmdf-driver/` on that repo's `main` is a README, so install reports the missing script. Source: `README.md` Building from source; `AGENTS.md`.
- DEC-007: v1/v2 and trackpad `030E` Boot Camp paths open the documented download page and do not call `pnputil`. `OfferV1V2StockRestore` unbinds `applewirelessmouse` on that PID only. Source: `README.md` Building from source.
- DEC-008: Trackpads get battery percent and warnings only. No scroll or gesture driver ships from this project. `0265` and `0324` refuse any driver change. Source: `README.md` Trackpads; `llms.txt` Scope.
- DEC-009: Keyboards keep the standard Windows Bluetooth driver. Battery needs the one-time SDP unlock (`scripts/kbd-patch-cachedservices.ps1` or Fix battery reads). Re-pairing erases it. Source: `README.md` Keyboard battery; `llms.txt`.
- DEC-010: Testers have confirmed only `0323`, `030D`, and `0239`. Other catalog PIDs are "should work, nobody has confirmed it yet." Source: `README.md` Help us test; `docs/TESTED.md`; `llms.txt`.
- DEC-011: Primary release asset is `MagicTray-<tag>-win-x64.zip`. Scripts ship under `scripts/` so both resolvers find them. A loose same-named file beside the exe wins for diagnostics. Source: `README.md` Releases.
- DEC-012: Shipped release is v1.1.0. csproj `<Version>` is 1.1.1 (unreleased). `verify-release.ps1` gates the tag against that version, so the next tag is `v1.1.1` or the csproj changes. Source: `README.md` Releases; `MagicMouseTray/MagicMouseTray.csproj`.
- DEC-013: `main` takes no direct pushes and no force pushes. Nine checks are required on every PR: build and test, verify publish, PowerShell lint, workflow lint, CodeQL, site checks, version sync, winget manifest, DCO sign-off. Source: `.github/workflows/README.md`; `CONTRIBUTING.md`.
- DEC-014: Every commit needs a `Signed-off-by` trailer. Merge commits and bot commits are skipped by the DCO check. Merge is Lesley-only. Source: `CONTRIBUTING.md`; `AGENTS.md` PR hygiene.
- DEC-015: Do not commit `.aco/graph.db`. Jev routing is optional and fail-open when `JEV_URL` is unset; `JEV_DISABLED=1` forces offline. No localhost graph daemons. Source: `AGENTS.md`; `.aco/README.md`.
- DEC-016: Do not vendor Magic Utilities binaries. Gestures, trackpad tap, and media-key remaps are not shipped. Source: `CONTRIBUTING.md`; `README.md` Magic Tray vs Magic Utilities; `llms.txt`.
- DEC-017: v1.1.0 shipped unsigned because signing secrets are empty. `scripts/sign-app.ps1` runs on `v*` tags only when a real CA PFX is present. A self-signed PFX is not enough. Source: `README.md` Why the app is unsigned.
- DEC-018: `capture-state.ps1` is 0323-only. Multi-device capture and stopped-filter repair is `scripts/diagnose-and-recover.ps1`. Source: `README.md` Diagnostics.
- DEC-019: Driver URLs and package names belong in `DriverPackageCatalog.cs` only. Source: `CONTRIBUTING.md`.
- DEC-020: Supported security line is v1.1.0 only. Report vulnerabilities via GitHub private advisories, not a public issue. Source: `SECURITY.md`.
- DEC-021: Current state outranks history for agents. Charter: `.ai/CORE-RULES.md` (referenced from `AGENTS.md`; that path is named there and is not a file in this checkout). Source: `AGENTS.md` CORE-RULES, Docs decade.
- HANDOFF-001: 2026-10-19 seeded `.context/` and the `AGENTS.md` pointer on draft branch `ai/dot-context-seed-2026-10-18` into `main`. Merge stays Lesley-only. Source: `AGENTS.md` PR hygiene.
