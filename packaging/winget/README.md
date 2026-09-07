# Submitting Magic Tray to winget

Notes for a maintainer. This folder holds the Windows Package Manager manifest
for Magic Tray and the one workflow template that can open the submission pull
request. Nothing here is wired into the release build; a winget submission is a
pull request against a repository we do not own, and a person on the other side
reads it.

## What is in here

```
packaging/winget/
  README.md                 this file
  winget-submit.yml         workflow TEMPLATE, manual only, not active
  manifests/l/LesleyMurfin/MagicTray/1.1.0/
    LesleyMurfin.MagicTray.yaml               version manifest
    LesleyMurfin.MagicTray.installer.yaml     installer manifest
    LesleyMurfin.MagicTray.locale.en-US.yaml  default locale manifest
```

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

## Why it installs as a ZIP and not as a bare exe

`InstallerType: zip`, `NestedInstallerType: portable`.

Magic Tray is not an installer. It is `MagicMouseTray.exe` with a `scripts/`
folder beside it, and the keyboard battery unlock plus every entry in the
Diagnostics menu look for `<folder of the exe>/scripts/<name>`. Ship the loose
exe and you ship a tray whose "Fix battery reads" and whose Diagnostics menu are
both dead. Installing the archive keeps the pair together.

`ArchiveBinariesDependOnPath: true` matters for the same reason. By default
WinGet drops a symlink into its `Links` folder and puts that on `PATH`; with
this flag it puts the extracted folder itself on `PATH` and skips the symlink
(confirmed in `winget-cli`'s `PortableInstaller.cpp`, which logs
"Install directory added to PATH"). The exe therefore always starts from the
folder that holds `scripts/`. Because there is no symlink, there is no
`PortableCommandAlias` in the manifest — the alias only names that symlink.

The asset name and the archive's internal layout were agreed with the release
packaging owner and must not drift:

| | value |
|---|---|
| Asset | `MagicTray-<tag>-win-x64.zip`, tag keeps its leading `v` |
| `InstallerUrl` | `https://github.com/LesleyMurfin/magic-tray/releases/download/v1.1.0/MagicTray-v1.1.0-win-x64.zip` |
| Archive root | `MagicMouseTray.exe`, `scripts/`, `README.txt`, `SHA256SUMS`, no wrapping folder |
| `RelativeFilePath` | `MagicMouseTray.exe` |
| Hash sidecar | `MagicTray-<tag>-win-x64.zip.sha256`, attached to the release |

If the release ever gains a wrapping top-level folder, `RelativeFilePath` has to
gain that prefix or the install fails with a missing nested installer.

## Compute the SHA256

`InstallerSha256` in the checked-in manifest is 64 zeros. That is a placeholder,
on purpose. It is not a hash of anything, and it is never guessed or
hand-written: WinGet's validation downloads the asset and compares, so a wrong
hash fails the PR.

Take the digest from the published asset, on the day you submit.

PowerShell, on Windows:

```powershell
$tag   = 'v1.1.0'
$asset = "MagicTray-$tag-win-x64.zip"
$base  = "https://github.com/LesleyMurfin/magic-tray/releases/download/$tag"

Invoke-WebRequest "$base/$asset" -OutFile $asset
(Get-FileHash $asset -Algorithm SHA256).Hash.ToUpper()
```

Cross-check it against the sidecar the release publishes, rather than trusting a
single download:

```powershell
Invoke-WebRequest "$base/$asset.sha256" -OutFile "$asset.sha256"
$declared = ((Get-Content "$asset.sha256" -Raw).Trim() -split '\s+')[0].ToUpper()
$computed = (Get-FileHash $asset -Algorithm SHA256).Hash.ToUpper()
if ($computed -ne $declared) { throw "Mismatch. Do not submit." }
$computed
```

Same thing on Linux or macOS:

```sh
tag=v1.1.0
base=https://github.com/LesleyMurfin/magic-tray/releases/download/$tag
curl -fLO "$base/MagicTray-$tag-win-x64.zip"
curl -fLO "$base/MagicTray-$tag-win-x64.zip.sha256"
sha256sum -c "MagicTray-$tag-win-x64.zip.sha256"
sha256sum "MagicTray-$tag-win-x64.zip" | cut -d' ' -f1 | tr 'a-f' 'A-F'
```

Paste that upper-case digest over the 64 zeros **in your fork only**. Leave the
placeholder in this repository, so the file in `main` never looks like it
describes a build it does not.

## Validate and test before opening the PR

These need Windows. `winget` does not exist on Linux, so nothing in CI here can
stand in for them.

```powershell
$dir = 'packaging\winget\manifests\l\LesleyMurfin\MagicTray\1.1.0'

winget validate --manifest $dir

winget settings --enable LocalManifestFiles   # needs an elevated terminal
winget install --manifest $dir
```

Then actually open the tray, hover a Magic Keyboard and click "Fix battery
reads", and open the Diagnostics menu. That is the test that catches a broken
`scripts/` path, and it is the whole reason for the ZIP install. A moderator will
run something similar.

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
git checkout -b magictray-1.1.0 upstream/master

mkdir -p manifests/l/LesleyMurfin/MagicTray/1.1.0
cp /path/to/Magic-Tray/packaging/winget/manifests/l/LesleyMurfin/MagicTray/1.1.0/*.yaml \
   manifests/l/LesleyMurfin/MagicTray/1.1.0/
# now paste the real SHA256 into LesleyMurfin.MagicTray.installer.yaml

git add manifests/l/LesleyMurfin/MagicTray/1.1.0
git commit -m "New package: LesleyMurfin.MagicTray version 1.1.0"
git push origin magictray-1.1.0
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
manifests in this folder, once the hash is patched in:

```powershell
wingetcreate submit --prtitle 'New package: LesleyMurfin.MagicTray version 1.1.0' `
  packaging\winget\manifests\l\LesleyMurfin\MagicTray\1.1.0
```

Install it with `winget install wingetcreate`. It authenticates through a
browser OAuth flow, or reads a classic personal access token with the
`public_repo` scope from `WINGET_CREATE_GITHUB_TOKEN`. Fine-grained tokens do
not work (<https://github.com/microsoft/winget-create/issues/595>). Do not pass
`--token` on a command line you do not control; it can end up in a log.

`packaging/winget/winget-submit.yml` automates exactly the mechanical half:
download the asset, verify it against its sidecar, patch the hash, upload the
patched manifests, and optionally run `wingetcreate submit`. It is
`workflow_dispatch` only and defaults to a dry run. It is **not** active — copy
it to `.github/workflows/` first, and add the `WINGET_CREATE_GITHUB_TOKEN`
secret, which this repository does not currently have. Its header documents the
scope.

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
  the archive ships a `SHA256SUMS` listing every file in it, and the release
  attaches a `.sha256` sidecar for the ZIP itself, so anyone can verify what
  they got;
- it is a portable ZIP, which the repository accepts — `sharkdp.fd` and many
  others ship the same `zip` + `portable` shape — but the nested paths get read
  carefully.

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
the package, and creates no Start menu entry and no desktop icon. The folder goes
on `PATH`, so in a **new** terminal `MagicMouseTray` starts the tray. Anyone who
wants it on their taskbar or at sign-in should pin or shortcut the exe from that
folder — and move the whole folder if they move it anywhere, never the exe alone.

## Bumping to a new version

1. Copy `manifests/l/LesleyMurfin/MagicTray/1.1.0/` to the new version number.
2. Change `PackageVersion` in **all three** files, and `InstallerUrl`,
   `ReleaseDate` and `ReleaseNotesUrl` to match the new tag.
3. Reset `InstallerSha256` to the 64-zero placeholder in the committed copy.
4. Re-read the `winget-pkgs` PR template in case `ManifestVersion` moved.
5. Keep `ShortDescription` in step with the `<meta name="description">` on
   <https://magictray.app/>, so the listing and the site say the same thing.
