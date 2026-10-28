# Build environment (this workstation)

Where the build, test, signing and release toolchain actually lives on the WSL/Windows
workstation this repo is developed on, and which of it you need. Measured 2026-10-28; every
command below was run as written.

**You do not need any of this to change code.** A plain `dotnet build` / `dotnet test` needs
only the .NET 8 SDK on a Windows machine. The rest of this file exists because the WSL side has
three .NET installs, only one of which builds the app, none of which can run the tests.

## Why this file is at the repo root

`docs/` is the published GitHub Pages site (<https://magictray.app>). `scripts/check-site.ps1`
fails any Markdown file under `docs/` that is neither listed in `sitemap.xml` nor disallowed in
`robots.txt`:

```
docs/BUILD-ENVIRONMENT.md is published and not in sitemap.xml, and robots.txt does not
disallow it for user-agent(s): * (none); it would be indexed
```

A workstation note is not a site page, so it lives at the root next to `CONTRIBUTING.md`.

## .NET SDKs

| Path | SDK | Builds the WPF app? |
| --- | --- | --- |
| `/data/opt/revive/orca_serve/.dotnet/dotnet` | 8.0.427 | **Yes** — use this one in WSL |
| `/usr/bin/dotnet` (Ubuntu) | 8.0.132 | No |
| `C:\Program Files\dotnet\dotnet.exe` | 8.0.427 | Yes, but not against this worktree |

Build in WSL with the absolute path — do not rely on `PATH`, which finds Ubuntu's:

```bash
/data/opt/revive/orca_serve/.dotnet/dotnet build MagicMouseTray/MagicMouseTray.csproj -c Release
```

**Ubuntu's 8.0.132 fails at project evaluation.** It ships no
`Sdks/Microsoft.NET.Sdk.WindowsDesktop`, so `<UseWPF>true</UseWPF>` cannot import its targets:

```
/usr/lib/dotnet/sdk/8.0.132/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.targets(1355,3):
error MSB4019: The imported project ".../Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.NET.Sdk.WindowsDesktop.targets" was not found.
```

`EnableWindowsTargeting` does not fix this. It supplies the Windows *reference packs*; the
missing thing here is an SDK directory inside the SDK itself.
`ls /data/opt/revive/orca_serve/.dotnet/sdk/8.0.427/Sdks/ | grep WindowsDesktop` lists it;
the same `ls` against `/usr/lib/dotnet/sdk/8.0.132/Sdks/` prints nothing.

**Windows `dotnet.exe` cannot build this worktree in place.** The worktree is
`svc_orca:revive drwxr-sr-x`; Windows interop runs as `lesleys-pc\lesley`, which has no write
bit, and NuGet writes a temp file into `obj/` before anything else:

```
C:\Program Files\dotnet\sdk\8.0.427\NuGet.targets(174,5): error : Access to the path
'\\wsl.localhost\Ubuntu\...\MagicMouseTray\obj\df0cfdee-86ef-4a06-ab38-c85dbf992431.tmp' is denied.
```

## Build here, test on Windows

The WSL SDK builds `MagicMouseTray.Tests` cleanly (`0 Warning(s) 0 Error(s)`) but cannot
**run** it. The test host targets `net8.0-windows10.0.17763.0`, so it asks for a framework that
does not exist on Linux:

```
Testhost process for source(s) '.../MagicMouseTray.Tests.dll' exited with error:
You must install or update .NET to run this application.
Framework: 'Microsoft.WindowsDesktop.App', version '8.0.0' (x64)
No frameworks were found.
```

`/data/opt/revive/orca_serve/.dotnet/dotnet --list-runtimes` shows only
`Microsoft.AspNetCore.App` and `Microsoft.NETCore.App`. This is a host limitation, not a defect
in the csproj — do not "fix" it there. Windows has both halves: SDK 8.0.427 and
`Microsoft.WindowsDesktop.App 8.0.33` under `C:\Program Files\dotnet\shared`.

Running the suite therefore means copying the tree to `C:\` and testing there. The copy must be
driven *from the Windows side*: we run as `svc_orca` while `/mnt/c` is drvfs mounted
`uid=1000` (lesley), so WSL itself cannot write to `C:`.

```bash
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command "robocopy '\\wsl.localhost\Ubuntu\home\lesley\orca\workspaces\magic-tray\<worktree>' C:\temp\mmt /E /XD .git bin obj publish dist; cd C:\temp\mmt; & 'C:\Program Files\dotnet\dotnet.exe' test MagicMouseTray.Tests\MagicMouseTray.Tests.csproj -c Release"
```

Notes that cost time if you learn them the hard way:

- Delete the copy's `.git` **file** first. In a worktree it is a pointer
  (`gitdir: ...`) to the main repository, not a repository, and it is meaningless on `C:`.
  `/XD .git` skips the directory case; a worktree's `.git` is a file.
- Remove `C:\temp\mmt` afterwards. It is a full second copy of the tree.
- `cmd` and `powershell` refuse to start with a `\\wsl.localhost\...` working directory
  ("UNC paths are not supported"), which is why the command above `cd`s only after the copy.
- PowerShell's `Test-Path '\\wsl.localhost\Ubuntu\...'` returns `False` even when the path is
  there; `cmd /c dir` on the same path lists it. Use `robocopy`/`cmd`, not `Test-Path`, to probe
  the WSL share from Windows.

## Windows SDK (signing)

Installed, nothing to set up:

- `C:\Program Files (x86)\Windows Kits\10` with `Include\10.0.26100.0` and `bin\10.0.26100.0`
- `C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe`,
  version `4.00 (WinBuild.160101.0800)`

That is exactly what `scripts/sign-app.ps1:21` resolves —
`"${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe"` — so release signing needs no
extra install. The installer is cached at
`C:\ProgramData\Package Cache\{b5ab6bbd-62a1-45bd-af43-2ea6ac4c3486}\winsdksetup.exe` if it ever
has to be repaired.

## EWDK ISO (SDK + WDK + MSVC, registry-free)

Only needed for packaging, driver-side work or a from-scratch native toolchain. The app itself
needs none of it.

- ISO: `D:\Users\Lesley\Downloads\EWDK_ge_release_svc_prod1_26100_250904-1728.iso` (20,002,537,472 bytes)
- Mounted as `H:`; `H:\Version.txt` = `Version ge_release_svc_prod1.26100.6584`; SDK/WDK `10.0.26100.0`

Layout:

| Path | What |
| --- | --- |
| `H:\LaunchBuildEnv.cmd` | entry point; `LaunchBuildEnv.cmd x64` opens a configured shell |
| `H:\BuildEnv\SetupBuildEnv.cmd` | the real setup |
| `H:\Program Files\Windows Kits\10\` | Include/Lib/bin/Debuggers/Redist/Testing/Tools/ADK |
| `H:\Program Files\Windows Kits\NETFXSDK\4.8.1\` | .NET Framework 4.8.1 SDK |
| `H:\Program Files\Microsoft Visual Studio\2022\BuildTools\` | MSBuild 17.14.10.27608 |

Verified present: `H:\Program Files\Windows Kits\10\bin\10.0.26100.0\x64\{signtool,makeappx,mc}.exe`.

It installs nothing and touches no registry — `SetupBuildEnv.cmd` sets `DisableRegistryUse=True`,
points `WindowsSdkDir` and `WDKContentRoot` into the ISO, and calls
`vsdevcmd.bat -winsdk=none` so Visual Studio's own SDK lookup stays out of it.

`H:` is **not** auto-mounted into WSL, and `sudo` prompts for a password here. Two ways in:

```bash
# via Windows (works today, no password)
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command "cd C:\; Get-Content H:\Version.txt"

# or mount it, if you have sudo
sudo mkdir -p /mnt/h && sudo mount -t drvfs H: /mnt/h
```

The ISO file itself is readable from WSL at `/mnt/d/Users/Lesley/Downloads/EWDK_...iso`, and
`/usr/bin/7z` can extract individual members from it with no mount at all.

## What the app actually needs

`MagicMouseTray.csproj` is `net8.0-windows10.0.17763.0` with `UseWPF` and `UseWindowsForms`, built
and shipped by `dotnet publish`. There is no native compilation anywhere in the repo. The Windows
SDK and the EWDK matter only for signing, packaging and driver-side work.

## CI — releases are already automated

**Do not add a release workflow.** `.github/workflows/release.yml` fires on a `v*` tag and runs on
`windows-latest`:

`actions/setup-dotnet@v6` (`8.0.x`) → `dotnet restore` → `dotnet build` → `dotnet test` (trx
uploaded as an artifact) → single-file self-contained win-x64 `dotnet publish` →
`scripts/stage-publish.ps1` → `scripts/sign-app.ps1`, gated on the `SIGN_PFX_BASE64` /
`SIGN_PFX_PASSWORD` secrets → `scripts/package-release.ps1` → `scripts/verify-release.ps1` →
`gh release create` with the ZIP listed first, the loose exe and scripts, and `SHA256SUMS`, with
notes quoting the exe and ZIP SHA-256 digests and byte lengths.

`ci.yml` builds and tests every PR. `packaging.yml` and `winget-submit.yml` cover the winget
manifest. All nine PR checks are described in `.github/workflows/README.md`.

One gate cannot be reproduced in WSL: `scripts/verify-release.ps1`'s `FileVersionInfo` check reads
Win32 PE version resources, which .NET on Linux returns empty. Run the publish/package/verify chain
on Windows; CI runs it on `windows-latest`.

## Doc and lint gates (these do run in WSL)

`pwsh` is on `PATH`:

```bash
pwsh -File scripts/check-site.ps1
pwsh -File scripts/check-aeo.ps1
pwsh -File scripts/check-version-sync.ps1
python3 scripts/check-winget-manifest.py
pwsh -Command "Invoke-ScriptAnalyzer -Path . -Recurse -Settings ./PSScriptAnalyzerSettings.psd1 -Severity Error,Warning"
```

The analyzer must stay at zero findings.
