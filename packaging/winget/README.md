# Submitting Magic Tray to winget

Notes for a maintainer. This folder holds the Windows Package Manager manifests
for Magic Tray and these maintainer notes - nothing else. Nothing here is wired
into the release build; a winget submission is a pull request against a
repository we do not own, and a person on the other side reads it.

## Verified state, 2026-09-16

The set is submittable and points at the exe the v1.1.0 release actually
attaches. `gh release view v1.1.0` lists three assets; each was downloaded and
hashed:

| asset | bytes | sha256 |
|---|---|---|
| `MagicMouseTray.exe` | 186,528,764 | `696DB5FF96360A125C83AF7C3C5D752626B9DE728EA42240D0ECAB81B9F4DCD7` |
| `kbd-patch-cachedservices.ps1` | 7,154 | `E0815E90BC83104D462914F47367EC82450C0AF6F2CE0B2DC64AFF64907AB375` |
| `Install-KeyboardBattery.cmd` | 1,534 | `708F290711C98C6152554C73A1E766181303744F9822EFD342BA28022025DB04` |

All three match the digests the GitHub release API reports. The first is the
`InstallerUrl` and its digest is in the installer manifest.

Also checked: `LesleyMurfin.MagicTray` does not exist in `microsoft/winget-pkgs`
and has no PR in flight, schema `1.12.0` still matches the PR template's
checklist, every URL in the manifests returns 200, and all three files validate
against the published 1.12.0 JSON schemas.

There is no `MagicTray-<tag>-win-x64.zip` on any release yet. The `v1.1.0` tag
is commit `f655203` (2026-09-02) and `scripts/package-release.ps1` landed later,
in `9d9a553` (2026-09-07), so the archive packaging exists but no published
release uses it. That is why this submission is a portable exe.


## What is in here

```
packaging/winget/
  README.md                 this file
  manifests/l/LesleyMurfin/MagicTray/1.1.0/
    LesleyMurfin.MagicTray.yaml               version manifest
    LesleyMurfin.MagicTray.installer.yaml     installer manifest
    LesleyMurfin.MagicTray.locale.en-US.yaml  default locale manifest
```

There is no workflow in this folder: it holds the manifests and these
maintainer notes, nothing else. The submission workflow lives at
`.github/workflows/winget-submit.yml`, because GitHub only runs workflows from
that folder. It is still `workflow_dispatch` only and still defaults to a dry
run; see "Fork and PR flow" below.

The folder path is not decorative. `microsoft/winget-pkgs` requires
`manifests/<first letter of publisher, lowercased>/<Publisher>/<PackageName>/<Version>/`,
and the file names must be `<PackageIdentifier>.yaml`,
`<PackageIdentifier>.installer.yaml` and `<PackageIdentifier>.locale.<locale>.yaml`.
Keep the same shape when you copy this tree into your fork.

Singleton manifests are not accepted in the community repository, which is why
this is a three-file set rather than one file.

## Schema version: 1.12.0

Not the newest. `microsoft/winget-cli` ships schemas up to **1.28.0**, but the
community repository deliberately lags so that people on older WinGet clients
can still install packages:

> The community repository will often delay support for new schema versions
> until enough devices have been updated so customers can benefit from the newly
> added manifest keys. Please use the recommended schema version mentioned in the
> PR template.
>
> — <https://github.com/microsoft/winget-pkgs/blob/master/doc/manifest/README.md>

The PR template's own checklist says:

> Manifest conforms to the [1.12 schema](https://github.com/microsoft/winget-pkgs/tree/master/doc/manifest/schema/1.12.0)
>
> — <https://github.com/microsoft/winget-pkgs/blob/master/.github/PULL_REQUEST_TEMPLATE.md>

So all three files carry `ManifestVersion: 1.12.0` and the matching
`# yaml-language-server: $schema=` header. Before a future submission, re-read
that PR template. When Microsoft moves the recommendation, bump all three files
together; a mixed set fails validation.

## Why it installs as a portable exe

`InstallerType: portable`. No archive keys: no `NestedInstallerType`, no
`NestedInstallerFiles`, no `ArchiveBinariesDependOnPath`.

Magic Tray is not an installer. `MagicMouseTray.exe` is a self-contained
single-file publish — `PublishSingleFile`, `SelfContained`, `RuntimeIdentifier
win-x64` in `MagicMouseTray.csproj` — so the exe is the whole app, with the .NET
runtime inside it. It is also the asset every download link on
<https://magictray.app/> already points at, and the one the site's own install
steps name. WinGet installing the same file is the same app the site ships.

| | value |
|---|---|
| Asset | `MagicMouseTray.exe`, attached to every release |
| `InstallerUrl` | `https://github.com/LesleyMurfin/magic-tray/releases/download/v1.1.0/MagicMouseTray.exe` |
| `Commands` | `magictray` — this is the command alias |
| Install location | `%LOCALAPPDATA%\Microsoft\WinGet\Packages\LesleyMurfin.MagicTray_<hash>\` |

`Commands` is doing real work here, not decoration. For a non-archive portable,
WinGet takes the symlink name from `Commands[0]` and falls back to the file name
when the list is empty — the non-nested branch of `winget-cli`'s
`PortableFlow.cpp` reads `Installer->Commands`, uses `commands[0]` as
`commandAlias`, and appends `.exe`. So `magictray` is what a person types.

`PortableCommandAlias` is deliberately not in the manifest. The 1.12 schema only
defines it inside `NestedInstallerFiles`, and `PortableFlow.cpp` only reads it on
the nested path, so on a bare portable it is an unknown key that does nothing.
`Commands` is the field that works.

### What a portable install does not carry

Know this before answering a moderator, because it is the one honest weakness of
this shape. The keyboard battery unlock and the Diagnostics menu entries resolve
their scripts from disk and nothing else: `DriverInstaller
.FindKeyboardPatchScript` probes `<exe dir>/scripts/<name>` then `<exe
dir>/<name>` then six parents, and `DiagnosticScripts.Find` does the same. There
is no embedded copy — the csproj has no `EmbeddedResource`, and nothing in the
app calls `GetManifestResourceStream`.

So a portable install of the lone exe gives a tray where:

- battery percent for every device works, and that is the app's primary function;
- the 2024 Magic Mouse driver route works — it downloads what it needs at run
  time (`DriverInstaller.DownloadV3DefaultBranchAsync`);
- the Magic Mouse v1/v2 and Magic Trackpad v1 routes work — they only open a
  download page;
- "Fix battery reads" shows an error toast, `kbd-patch-cachedservices.ps1 not
  found next to the tray.`, until the script sits beside the exe;
- the Diagnostics script entries are simply absent.

This is not a regression the winget listing introduces. The site's own install
steps are "download `MagicMouseTray.exe` and double-click it", which lands in
exactly the same state, and `docs/keyboard.html` already documents the way out:
run `Install-KeyboardBattery.cmd` from the release page as administrator. The
locale manifest's `Description` says so too, in as few words as it can.

If a release ever attaches `MagicTray-<tag>-win-x64.zip` from
`scripts/package-release.ps1`, switching to `InstallerType: zip` with
`NestedInstallerType: portable`, `RelativeFilePath: MagicMouseTray.exe` and
`ArchiveBinariesDependOnPath: true` removes that last caveat, because the
archive keeps `scripts/` beside the exe. Until then this is the accurate
manifest.

## Compute the SHA256

`InstallerSha256` is the digest of the published `MagicMouseTray.exe`, upper
case, and it is never guessed or hand-written: WinGet's validation downloads the
asset and compares, so a wrong hash fails the PR. Recompute it whenever
`InstallerUrl` changes.

PowerShell, on Windows:

```powershell
$tag   = 'v1.1.0'
$asset = 'MagicMouseTray.exe'
$base  = "https://github.com/LesleyMurfin/magic-tray/releases/download/$tag"

Invoke-WebRequest "$base/$asset" -OutFile $asset
(Get-FileHash $asset -Algorithm SHA256).Hash.ToUpper()
```

Same thing on Linux or macOS:

```sh
tag=v1.1.0
curl -fLO "https://github.com/LesleyMurfin/magic-tray/releases/download/$tag/MagicMouseTray.exe"
sha256sum MagicMouseTray.exe | cut -d' ' -f1 | tr 'a-f' 'A-F'
```

Cross-check it against the digest GitHub records for the asset itself, rather
than trusting one download:

```sh
gh release view v1.1.0 --json assets \
  --jq '.assets[] | select(.name=="MagicMouseTray.exe") | .digest'
```

The release also attaches a `SHA256SUMS` file on releases cut by the current
workflow. There is no `.sha256` sidecar for the exe.

## Validate and test before opening the PR

These need Windows. `winget` does not exist on Linux, so nothing in CI here can
stand in for them.

```powershell
$dir = 'packaging\winget\manifests\l\LesleyMurfin\MagicTray\1.1.0'

winget validate --manifest $dir

winget settings --enable LocalManifestFiles   # needs an elevated terminal
winget install --manifest $dir
```

Then open a new terminal, run `magictray`, and check the icon appears beside the
clock with a battery percent on it. Confirm the alias resolves: the symlink
should be `magictray.exe` in WinGet's `Links` folder, which is what `Commands`
buys. "Fix battery reads" will toast that
`kbd-patch-cachedservices.ps1` is missing, and the Diagnostics script entries
will be absent — that is expected for a portable install, see "What a portable
install does not carry". A moderator will run something similar.

What cannot be done here: `winget` does not exist on Linux, so these two steps
were not run before the first submission. The manifests were validated against
the published 1.12.0 JSON schemas instead, and the PR checklist says exactly
that rather than ticking a box nobody tested.

If Group Policy blocks `--enable LocalManifestFiles`, use Windows Sandbox
instead: `Tools\SandboxTest.ps1` from a clone of `microsoft/winget-pkgs`.

## Fork and PR flow

One-time setup:

1. Sign the Microsoft Contributor License Agreement at
   <https://cla.opensource.microsoft.com>. Until this is signed against the
   account that opens the PR, the PR cannot merge.
2. Fork <https://github.com/microsoft/winget-pkgs> to your account.

Per submission:

```sh
git clone https://github.com/<you>/winget-pkgs.git
cd winget-pkgs
git remote add upstream https://github.com/microsoft/winget-pkgs.git
git fetch upstream
git checkout -b LesleyMurfin.MagicTray-1.1.0 upstream/master

mkdir -p manifests/l/LesleyMurfin/MagicTray/1.1.0
cp /path/to/Magic-Tray/packaging/winget/manifests/l/LesleyMurfin/MagicTray/1.1.0/*.yaml \
   manifests/l/LesleyMurfin/MagicTray/1.1.0/

git add manifests/l/LesleyMurfin/MagicTray/1.1.0
git commit -s -m "New package: LesleyMurfin.MagicTray version 1.1.0"
git push origin LesleyMurfin.MagicTray-1.1.0
```

Open the PR against `microsoft/winget-pkgs` `master`. Title it
`New package: LesleyMurfin.MagicTray version 1.1.0` for a first submission, or
`Update: LesleyMurfin.MagicTray to X.Y.Z` afterwards. Then tick the template
checklist honestly.

Three repository rules that reject PRs quietly if you miss them:

- one package **and** one version per PR;
- manifest files only, no README, no doc, no tooling, no spelling-dictionary
  edits — those go in a separate PR;
- no open PR for the same package version already in flight.

`wingetcreate` can do the fork, branch, commit and PR in one step, from the
manifests in this folder:

```powershell
wingetcreate submit --prtitle 'New package: LesleyMurfin.MagicTray version 1.1.0' `
  packaging\winget\manifests\l\LesleyMurfin\MagicTray\1.1.0
```

Install it with `winget install wingetcreate`. It authenticates through a
browser OAuth flow, or reads a classic personal access token with the
`public_repo` scope from `WINGET_CREATE_GITHUB_TOKEN`. Fine-grained tokens do
not work (<https://github.com/microsoft/winget-create/issues/595>). Do not pass
`--token` on a command line you do not control; it can end up in a log.

.github/workflows/winget-submit.yml automates exactly the mechanical half:
download the asset, verify it against its sidecar, patch the hash, upload the
patched manifests, and optionally run `wingetcreate submit`. It is
`workflow_dispatch` only and defaults to a dry run. A real submission also
needs the `WINGET_CREATE_GITHUB_TOKEN` secret, which this repository does not
currently have, so until it is added every run must keep dry_run enabled - the
workflow's first step fails fast otherwise. Its header documents the scope.

The offline half of manifest checking runs on every pull request - the path
filter in `.github/workflows/packaging.yml` applies to pushes to `main` only,
because a required check that a path filter skips never reports and would block
the PR forever. It checks that all three files parse, the required keys are
present on every `Installers` entry, and the version agrees with the folder it
sits in (`scripts/check-winget-manifest.py`).
`scripts/check-version-sync.ps1` then holds the published
metadata - these manifests, the installer URL and the version strings on
magictray.app - to the LATEST RELEASED version, while allowing the csproj
`<Version>` to run ahead of it. Ahead is the normal "next version in
development" state; behind the released version is an error, because a release
must never be cut from a tree with a stale version.

## It cannot be fully automated

Say this plainly, because it is easy to assume a green pipeline means shipped.

After the PR opens, a bot runs the validation pipeline. If every step passes, the
`Validation-Completed` label appears, and then:

> A community moderator reviews the PR. Moderators check manifest quality,
> verify the package installs as expected, and confirm the metadata is accurate.
>
> — <https://github.com/microsoft/winget-pkgs/blob/master/doc/Validation.md>

Only after a human applies `Moderator-Approved` does the PR merge automatically,
and the package appears in the `winget` source up to an hour later.

So a first submission needs, irreducibly: a signed CLA on a real person's
account, and a moderator's manual approval. Neither is something this repo's CI
can do unattended, on any schedule, with any secret. Expect review cycles and be
ready to answer questions on the PR. Two things about Magic Tray that a
moderator may reasonably ask about:

- the exe is unsigned, so the security scan and SmartScreen are worth expecting;
  releases cut by the current workflow attach a `SHA256SUMS` file, and the
  GitHub release API records a digest per asset, so anyone can verify what they
  got;
- it is a portable exe, which the repository accepts and which is the shape most
  single-binary tools use;
- it is a 178 MB download, because the .NET runtime is inside the single file.
  That is large for a portable and worth explaining before it is asked about.

## What an end user runs

Install:

```powershell
winget install --id LesleyMurfin.MagicTray --exact
```

`winget install magictray` also resolves, via the `Moniker`.

Upgrade:

```powershell
winget upgrade --id LesleyMurfin.MagicTray --exact
```

Or, along with everything else on the machine, `winget upgrade --all`.

Remove:

```powershell
winget uninstall --id LesleyMurfin.MagicTray --exact
```

Worth knowing before you point anyone at those commands: a portable install
lands under `%LOCALAPPDATA%\Microsoft\WinGet\Packages\` in a folder named after
the package, and creates no Start menu entry and no desktop icon. WinGet puts a
`magictray.exe` symlink in its `Links` folder, which is on `PATH`, so in a
**new** terminal `magictray` starts the tray. Anyone who wants it on their
taskbar or at sign-in should pin or shortcut it. The keyboard unlock and the
Diagnostics script entries need their scripts beside the exe, which a portable
install does not provide — see "What a portable install does not carry".

## Bumping to a new version

1. Copy `manifests/l/LesleyMurfin/MagicTray/1.1.0/` to the new version number.
2. Change `PackageVersion` in **all three** files, and `InstallerUrl`,
   `ReleaseDate` and `ReleaseNotesUrl` to match the new tag.
3. Recompute `InstallerSha256` from the new release's `MagicMouseTray.exe`. The
   committed copy carries the real digest, so it must never lag the URL above
   it.
4. Re-read the `winget-pkgs` PR template in case `ManifestVersion` moved.
5. Keep `ShortDescription` in step with the `<meta name="description">` on
   <https://magictray.app/>, so the listing and the site say the same thing.
