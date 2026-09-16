## What this changes, and why

<!-- One or two sentences. If it changes what a user sees in the tray, say so. -->

## How it was tested

Tested on real hardware: <!-- device model + Windows build, e.g. Magic Mouse 2024 (PID 0323) on Windows 11 26100 -->

<!-- CONTRIBUTING.md requires a manual run on Windows 10 1809+ or Windows 11 with a paired
     Apple device. If you genuinely could not test on hardware, say which part is untested. -->

## Issue

Closes #

## Checklist

- [ ] Commits are signed off (`git commit -s`) — the `DCO sign-off` check enforces this
- [ ] `dotnet test MagicMouseTray.Tests/MagicMouseTray.Tests.csproj -c Release` passes locally
- [ ] Scripts changed? `PowerShell lint` passes (PSScriptAnalyzer, no new warnings)
- [ ] Device tables changed? The PID is in `KnownMice` / `KnownKeyboards`, not only in `docs/TESTED.md`
- [ ] Docs/site updated? Anything that changes behaviour, version, or a download link needs the
      matching page under `docs/` (it is magictray.app — merging to `main` publishes it)
