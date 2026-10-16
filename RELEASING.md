# Releasing Magic Tray

You are most likely an AI agent that has been asked to cut the next release,
**v1.1.1**. This file is written for you rather than for someone who already
knows the repository: follow it top to bottom and you will not need to read
anything else. Two documents are referenced instead of copied, because
duplicating them is how they rot:

- [`CLOUDFLARE.md`](CLOUDFLARE.md) — the Cloudflare edge configuration for
  `magictray.app`. **None of it is in git.** You need it only if the site does
  not show what you just merged.
- [`packaging/winget/README.md`](packaging/winget/README.md) — the winget
  submission in detail: folder layout, schema version, the manual fork-and-PR
  flow, and what a moderator will ask.

> **Read [§7](#7-what-went-wrong-with-v110) before you tag anything.** The
> v1.1.0 release workflow finished green and still published a broken release.
> A green workflow run does not mean a correct release, and §7 is the specific,
> still-unrepaired damage that assumption caused.

Two rules that shape everything below:

1. **CI builds the release; you do not.** `.github/workflows/release.yml`
   triggers on `push:` of a `v*` tag and does the whole job — build, test,
   optional signing, packaging, checksums, notes, upload. You never compile an
   exe, zip an archive, compute a digest or attach an asset by hand.
2. **GitHub runs the workflow file that exists at the tagged commit.** Not the
   one on `main`, not the one you just wrote. This single fact is why v1.1.0
   shipped without a ZIP or a checksum.

---

## 1. Preconditions

Everything in this list must be true before the tag exists. After the tag
exists, most of them are expensive to fix.

**1.1 You are working on a branch, through a pull request.** `main` is
protected by a ruleset: no direct pushes, no force pushes, no bypass actors —
the maintainer goes through a PR too. Nine checks are *required* and all of
them run on every pull request (`.github/workflows/README.md` is the full
table):

`Build and test`, `Verify publish`, `PowerShell lint`, `Workflow lint`,
`CodeQL (csharp)`, `Site checks`, `Version sync`, `Winget manifest`,
`DCO sign-off`.

**1.2 Every commit is signed off.** `DCO sign-off` (`.github/workflows/dco.yml`)
requires a real `Signed-off-by:` trailer on every non-merge, non-bot commit.
Use `git commit -s`; repair an existing branch with
`git rebase --signoff origin/main && git push --force-with-lease`.

**1.3 The csproj already carries the version you are about to tag.**
`MagicMouseTray/MagicMouseTray.csproj` holds three properties that must agree
with each other:

```xml
<Version>1.1.1</Version>
<AssemblyVersion>1.1.1.0</AssemblyVersion>
<FileVersion>1.1.1.0</FileVersion>
```

That is the state in the tree **right now**, so the next tag is `v1.1.1` and
you have nothing to bump before tagging. If you intend to ship some other
number, change all three first, in a PR of their own.

This is not advisory. `scripts/verify-release.ps1` runs inside the tag build
and compares the tag against the csproj `<Version>` and against the built exe's
`FileVersion` (its `FileVersionInfo` check). Tagging `v1.2.0` against a `1.1.1`
csproj fails the release build — after the tag already exists.

**1.4 The tag is `vMAJOR.MINOR.PATCH`.** The workflow trigger is the broader
`v*`, but `scripts/package-release.ps1`, `scripts/verify-release.ps1` and
`.github/workflows/winget-submit.yml` all match `^v\d+\.\d+\.\d+$` when they
derive a version. A tag like `v1.1.1-rc1` will build something, name it
confusingly, and be rejected by the winget workflow.

**1.5 The commit you tag contains the current `release.yml`.** Confirm it, do
not assume it:

```sh
git show v1.1.1^{commit}:.github/workflows/release.yml | grep -n 'package-release\|verify-published-release'
```

or, before the tag exists, against the commit you are about to tag:

```sh
git show <sha>:.github/workflows/release.yml | grep -n 'Package portable ZIP\|Verify the published release'
```

If those steps are not in the file at that commit, the run will not produce a
ZIP, a sidecar, a `SHA256SUMS` or a digest table, and nothing will re-check the
published release. That is exactly the v1.1.0 failure.

**1.6 `Version sync` is green locally.**

```sh
pwsh -File scripts/check-version-sync.ps1
```

Expected output today:

```
::notice file=MagicMouseTray/MagicMouseTray.csproj,line=19::<Version> 1.1.1 is ahead of the released version 1.1.0. …
version sync: OK (released 1.1.0, csproj 1.1.1).
```

The `::notice` is **not** a warning you need to silence. See [§2](#2-the-version-locations-and-what-each-one-means).

**1.7 The release does not already exist.** The workflow's own
`Create GitHub release` step runs `gh release create`, which fails if a release
for that tag is already there. Do not pre-create a release in the GitHub UI,
and do not create the tag from the Releases page — push the tag from git and
let the workflow create the release.

**1.8 Know which secrets are missing, and what that means.**

| Secret | Present? | Consequence |
|---|---|---|
| `SIGN_PFX_BASE64`, `SIGN_PFX_PASSWORD` | No | The `Sign (optional)` step is skipped (`if: env.SIGN_PFX_BASE64 != ''`). The exe ships **unsigned** and Windows shows a SmartScreen warning. Everything else runs normally. This is the expected state, and `docs/install.txt` already tells agents to expect it and never to disable SmartScreen. |
| `WINGET_CREATE_GITHUB_TOKEN` | No | No winget submission can be opened. See [§6](#6-the-winget-submission). |

**1.9 The Windows-only tests have passed on the PR.** `dotnet test` needs
Windows — the test assembly targets `net8.0-windows10.0.17763.0`, so it cannot
run on a Linux machine:

```powershell
dotnet test MagicMouseTray.Tests/MagicMouseTray.Tests.csproj -c Release
```

You do not have to run it yourself if the PR is green: `ci.yml`'s
`Build and test` and `Verify publish` jobs do it on `windows-latest`, and
`Verify publish` additionally publishes, stages, packages the portable ZIP with
`scripts/package-release.ps1` and gates it with `scripts/verify-release.ps1`.
A packaging break therefore surfaces on an ordinary pull request, before any
tag exists.

---

## 2. The version locations, and what each one means

Magic Tray names its version in five places. **They do not all mean the same
thing**, and the checker does not require them to be equal. The full reasoning
is in the `.DESCRIPTION` block at the top of `scripts/check-version-sync.ps1`;
the short version:

| # | Location | What it means |
|---|---|---|
| 1 | `MagicMouseTray/MagicMouseTray.csproj` | The version **in development**. May run AHEAD of the released one. Never behind. |
| 2 | `packaging/winget/manifests/l/LesleyMurfin/MagicTray/<ver>/` | The **latest RELEASED** version. The newest folder name *is* the released version, as far as CI is concerned. |
| 3 | `docs/index.html` | What `magictray.app` **advertises** — must be the latest RELEASED version. |
| 4 | `scripts/package-release.ps1` | The asset **name pattern**, `MagicTray-$Tag-win-x64.zip`. Not a version at all; the checker derives the expected asset name from it so a rename cannot pass silently. |
| 5 | `docs/install.txt` | What `magictray.app/install.txt` **tells an AI agent**. Must name the latest RELEASED version in its one illustrative reference. |

**The csproj is allowed to be ahead. That is the normal state.** A csproj that
is *behind* the released version is an error, because it means a release was
cut from a tree whose version was never bumped. The site and the manifests
describe a build that people can actually download, so they track the latest
*released* version, not the one being developed.

**Right now:**

- csproj: `1.1.1` / `1.1.1.0` / `1.1.1.0`
- newest winget manifest folder: `1.1.0`
- `docs/index.html`: `1.1.0` (JSON-LD `softwareVersion`, the brand `<small>`
  badge, and two `Download v1.1.0` links)
- `docs/install.txt`: `current release is v1.1.0`

**That is correct, not drift.** 1.1.1 is in development; 1.1.0 is what is
published. `check-version-sync.ps1` reports it as a `::notice` and exits 0.

### What the checker does and does not cover

`scripts/check-version-sync.ps1` (the `Version sync` check, run by
`.github/workflows/packaging.yml` on every PR) enforces locations 1–5:
csproj-internal agreement, every manifest against its own folder name,
`InstallerUrl` tag *and* asset file name, `ReleaseNotesUrl` tag, the three
version strings in `docs/index.html`, the `current release is vX.Y.Z`
illustration in `docs/install.txt`, and the csproj-ahead-never-behind rule.

It deliberately does **not** check these, and no other CI job does either, so
they are your manual work in [§3](#3-promoting-a-release-which-files-in-what-order):

| File | What it states |
|---|---|
| `README.md` | `Released 2 September 2026 · Download v1.1.0` near the top; the screenshot alt text; the `## Tray menu (1.1.0)` heading and its table-of-contents link; `Footer: **Magic Tray 1.1.0**`; and a paragraph naming the shipped release and the unreleased csproj version. |
| `llms.txt` and `docs/llms.txt` | `Current release is 1.1.0 (2026-09-02).` |
| `SECURITY.md` | The supported-versions table (`v1.1.0 (latest release)`) and the `Get-FileHash` / `Expand-Archive` examples, which name `MagicTray-v1.1.0-win-x64.zip`. |
| `docs/index.html` `datePublished` | The JSON-LD `"datePublished": "2026-09-02"` is not version-checked; it should be the new release date. |
| `packaging/winget/.../installer.yaml` `ReleaseDate` | Also not checked. Should be the date of the new tag. |

---

## 3. Promoting a release: which files, in what order

There are **two pull requests**, with the tag between them. Do not merge them
into one, and do not move phase C earlier.

### Phase A — before the tag (PR 1)

Touch **only** the csproj, and only if the version you are tagging is not
already there ([§1.3](#1-preconditions)). Nothing else. The manifests, the
site and `install.txt` still describe 1.1.0 at this point, because 1.1.0 is
still the newest thing anyone can download.

Today, for v1.1.1, phase A is empty.

### Phase B — the tag

[§4](#4-tagging-and-what-releaseyml-does-for-you). Then verify the published
release ([§5](#5-post-release-verification)) and **stop until it is green**.
Do not start phase C against a release you have not verified.

### Phase C — after the release is published and verified (PR 2)

In this order. The order matters: `check-version-sync.ps1` derives "the
released version" from the newest winget manifest folder, so if you edit the
site first you will get errors that only disappear once the manifests catch
up.

1. **The winget manifests.** Copy
   `packaging/winget/manifests/l/LesleyMurfin/MagicTray/1.1.0/` to a sibling
   folder named for the new version, then in the copy:
   - `PackageVersion` in **all three** files;
   - `InstallerUrl` → the new tag and the new asset name, i.e.
     `https://github.com/LesleyMurfin/magic-tray/releases/download/v1.1.1/MagicTray-v1.1.1-win-x64.zip`;
   - `ReleaseDate` (in the installer manifest) → the date of the new tag;
   - `ReleaseNotesUrl` (in the locale manifest) →
     `https://github.com/LesleyMurfin/magic-tray/releases/tag/v1.1.1`;
   - **leave `InstallerSha256` as 64 zeros.** That placeholder is deliberate in
     this repository: the real digest belongs only in the winget-pkgs fork, and
     `.github/workflows/winget-submit.yml` patches it there. A committed real
     hash makes `main` look like it describes a build it does not.
   - keep `ShortDescription` in step with the `<meta name="description">` on
     the home page.

   Whether to keep the old version folder or replace it is up to you; the
   checker validates every folder against its own name and treats the
   highest-numbered one as released.

2. **`docs/index.html`** — the JSON-LD `softwareVersion`, the brand link's
   `<small>` badge, every `Download vX.Y.Z` link text (two today), and the
   JSON-LD `datePublished`.

3. **`docs/install.txt`** — the single `At the time of writing the current
   release is vX.Y.Z` line in STEP 1. Do not add version claims anywhere else
   in that file: STEP 1 tells the agent to read `tag_name` from
   `api.github.com/repos/LesleyMurfin/magic-tray/releases/latest` and says "Do
   not assume those values", which is what keeps the file safe between
   releases. Keep the wording `current release is vX.Y.Z` — that is the phrase
   `check-version-sync.ps1` looks for.

4. **The prose CI does not check** — `README.md`, `llms.txt`,
   `docs/llms.txt`, `SECURITY.md`. See the table at the end of
   [§2](#2-the-version-locations-and-what-each-one-means) for exactly what each
   one asserts. If v1.1.1 fixes the missing-assets problem, the paragraphs in
   `README.md` and `SECURITY.md` that describe v1.1.0 as having shipped with no
   archive, no sidecar and no signature need rewriting rather than
   renumbering.

5. **Bump the csproj to the next version in development** — all three
   properties, e.g. `1.1.2` / `1.1.2.0` / `1.1.2.0`. Equal to the released
   version is legal; ahead is the convention, and it restores the `::notice`
   state described in [§2](#2-the-version-locations-and-what-each-one-means).

6. **Re-run the checker before you open the PR:**

   ```sh
   pwsh -File scripts/check-version-sync.ps1
   ```

   Exit 0 is required. Any `::error` line names the file and line to fix.

Merging phase C touches `docs/**`, which also triggers
`.github/workflows/indexnow.yml` — Bing and the answer engines that reuse its
index get pinged automatically. You do not need to do anything for that.

If the live site still shows the old version some minutes after Pages deploys,
that is an edge-cache question, not a repository one: see
[`CLOUDFLARE.md`](CLOUDFLARE.md) §2, which documents the `site.css` cache rule
and the one-time purge.

---

## 4. Tagging, and what `release.yml` does for you

```sh
git switch main
git pull --ff-only
git log -1 --oneline                     # this is the commit you are tagging
git show HEAD:.github/workflows/release.yml | grep -n 'Package portable ZIP\|Verify the published release'
git tag -a v1.1.1 -m "Magic Tray v1.1.1"
git push origin v1.1.1
```

That push is the whole release. `.github/workflows/release.yml` (`release` /
`Build and publish release`, `windows-latest`, `contents: write`, 30-minute
timeout, and deliberately **no** concurrency group so a tag build can never be
cancelled by a later one) then runs, in order:

| Step | What it guarantees |
|---|---|
| `actions/checkout` (`persist-credentials: false`) | The tagged tree. `gh` authenticates through `GH_TOKEN`, not the git credential helper. |
| `Setup .NET` (8.0.x) → `Restore` → `Build` | It compiles at the tagged commit. |
| `Test` | The xunit suite runs; the TRX is uploaded as the `test-results` artifact with `if: always()`. |
| `Publish (single-file, self-contained win-x64)` | `publish/MagicMouseTray.exe`. |
| `Stage keyboard patch and diagnostic scripts` | `scripts/stage-publish.ps1` copies the five helper scripts next to the exe and asserts every one is present. The ZIP's `scripts/` folder is load-bearing: the keyboard unlock and the whole Diagnostics menu resolve `<exe folder>/scripts/<name>`. |
| `Sign (optional)` | Authenticode, **only** when `SIGN_PFX_BASE64` exists. Skipped today. |
| `Package portable ZIP` | `scripts/package-release.ps1` emits `dist/MagicTray-v1.1.1-win-x64.zip`, its `.sha256` sidecar, and a `SHA256SUMS` covering every uploaded asset, and exports `zip`, `zip_name`, `zip_sha256`, `zip_bytes`, `exe_sha256`, `exe_bytes`, `sums` as step outputs. It runs *after* signing, so the exe inside the archive is the signed one when signing happens. |
| `Verify release artifacts` | `scripts/verify-release.ps1` gates the **local** artifacts: required files, exe size 50–300 MiB, `ProductName`/`FileVersion` against the tag and csproj, PE machine AMD64, the `Install-KeyboardBattery.cmd` guards, the exact ZIP entry list, non-empty payloads, the in-archive `SHA256SUMS` against real entry bytes, the exe inside the archive byte-identical to the one in `publish/` (so a signed exe is the packaged one), the sidecar, and the `SHA256SUMS` that will be uploaded. It never touches the network and never creates a release. |
| `Write release notes` | The notes file: "Recommended download" line, why the ZIP rather than the bare exe, and a table of bytes + SHA-256 for both `MagicMouseTray.exe` and the ZIP. |
| `Create GitHub release` | `gh release create` with the notes file plus GitHub's generated PR list, uploading **nine** assets, ZIP first. |
| `Verify the published release` | `scripts/verify-published-release.ps1` re-reads the release that now exists on GitHub. [§5](#5-post-release-verification). |

The nine assets, in upload order:

1. `MagicTray-v1.1.1-win-x64.zip` ← the primary download
2. `MagicTray-v1.1.1-win-x64.zip.sha256`
3. `MagicMouseTray.exe`
4. `kbd-patch-cachedservices.ps1`
5. `Install-KeyboardBattery.cmd`
6. `capture-state.ps1`
7. `diagnose-driver.ps1`
8. `mm-bt-stack-snapshot.ps1`
9. `SHA256SUMS`

The loose exe and scripts stay attached because links already in the wild point
at them; the ZIP is first because it is the one that works.

**You do not hand-build or hand-upload any of this.** If an asset is missing
after a run, resist the temptation to attach it manually — that hides the
defect and produces a release nobody can reproduce. Go to
[§5](#5-post-release-verification) instead.

---

## 5. Post-release verification

This section exists because it is the only thing that looks at the release the
public can actually see.

`scripts/verify-release.ps1` runs *before* `gh release create` and inspects
local files in `publish/` and `dist/` on the runner. It is a good gate on what
was *built*. It structurally cannot notice what was *published* — and for
v1.1.0 it did not.

### `scripts/verify-published-release.ps1`

`release.yml` runs it as the step `Verify the published release`, immediately
after `Create GitHub release`, with `GH_TOKEN: ${{ github.token }}` and no
`continue-on-error`. It is a hard gate: a bad release fails the run even though
the release is already public — which is the point, because a red run is a
signal and a silent bad release is not.

Run it yourself as well, from anywhere, against the published release:

```sh
pwsh -File scripts/verify-published-release.ps1 -Tag v1.1.1
```

- `-Tag` defaults to `$env:GITHUB_REF_NAME`; it must match
  `^v[0-9A-Za-z][0-9A-Za-z.+-]*$` or the script exits 1 without querying
  anything.
- `-Repo` defaults to `LesleyMurfin/magic-tray` and must be `OWNER/NAME`.
- Needs PowerShell 7 and `gh` on `PATH`. `GH_TOKEN` is used for metadata
  (`gh api repos/<repo>/releases/tags/<tag>`); with it unset the script prints
  a NOTE and lets `gh` use its stored credentials. Asset **bytes** come from
  each asset's public `browser_download_url`, streamed to a temp directory that
  is always cleaned up.

What it asserts — every check runs, then it exits once, so a single run reports
every problem:

1. A release for the tag exists and is not a draft.
2. The assets include at least `MagicTray-<tag>-win-x64.zip`,
   `MagicTray-<tag>-win-x64.zip.sha256`, `SHA256SUMS` and
   `MagicMouseTray.exe`, each in API state `uploaded`. An asset in any other
   state is a half-finished upload — the API row exists but the download
   fails — so it counts as missing rather than passing on the strength of the
   row. On failure this check prints the complete actual asset list with
   names, byte counts and states.
3. The SHA-256 it computes over the downloaded ZIP equals the digest in the
   downloaded `.sha256` sidecar (case-insensitively), and the sidecar names
   that ZIP.
4. `SHA256SUMS` parses as `<64 hex>  <filename>` lines with bare filenames and
   no duplicates, every name listed is actually attached to the release, and
   the ZIP's line matches the bytes it downloaded.
5. The release body contains the ZIP's **computed** digest — computed from the
   downloaded bytes, not copied from the sidecar. So "the notes already have a
   SHA-256 table" is not sufficient: notes quoting a stale but internally
   consistent digest fail here too. **This is the v1.1.0 catch**, where the
   body was a bare pull-request list with no digest at all.

Exit codes are only 0 and 1. 1 means a failed check, a malformed `-Tag`/`-Repo`,
`gh` missing, unobtainable release metadata, or an unexpected exception. On
failure it emits `::error::` annotations, and when `GITHUB_STEP_SUMMARY` is set
it writes a "Published release verification FAILED" block listing each failed
check with its remediation.

### Reading a failure

Every check runs before the script exits, so one run reports every problem and
the failures are not independent. Read them in the order the checks are
numbered: the first failure is usually the cause and the rest are symptoms. A
missing ZIP produces an asset-set failure plus three "cannot check" failures
behind it, and a **draft** release fails check 1 and then fails 3, 4 and 5 as
well, because a draft's assets are not downloadable by anyone.

The asset-set failure is the diagnostic one: it prints the release's complete
actual asset list. If that list is exactly `MagicMouseTray.exe`,
`Install-KeyboardBattery.cmd` and `kbd-patch-cachedservices.ps1`, a stale
`release.yml` ran — that is precisely the v1.1.0 asset set
([§7](#7-what-went-wrong-with-v110)). Do not start attaching files: the tagged
commit did not contain the packaging steps, so re-cut from one that does.

### If it fails

The script's step summary picks one remediation, keyed off which checks
failed. **Which one is correct depends on the failure**, and this file is the
decision — the summary is only the mechanism:

- **The release is a draft (check 1): publish it.** `gh release edit <tag>
  --draft=false`, then re-run the script. Checks 3, 4 and 5 will have failed
  too; they are symptoms of assets nobody can download, not a packaging bug.
  Only re-cut if the re-run still fails.
- **An asset is missing or not `uploaded` (check 2): re-cut.** Delete the
  release and the tag, fix the cause — usually a tagged commit without the
  packaging steps — and tag again from a commit that carries the current
  `release.yml`. Do **not** attach the ZIP by hand: a hand-attached archive
  still leaves notes with no digest table and a `SHA256SUMS` that never
  covered it, so you would be repairing one check and leaving two broken. That
  half-fix is what left v1.1.0 with a winget manifest pointing at an asset
  nobody can download.
- **A sidecar or `SHA256SUMS` mismatch on a complete asset set (checks 3 and
  4): re-cut.** The published bytes and the published digests disagree. That is
  never a notes problem and it cannot be edited away; find out how packaging
  and upload came apart before you tag again.
- **Check 5 is the sole failure, every required asset present: repair it in
  place.** Rewrite the body with the real digest table —
  `gh release edit <tag> --notes-file <file>` — taking the digests from the
  sidecar and confirming them against the bytes you downloaded. This is the
  only case where hand-repair is the right answer.

In every case, re-run `scripts/verify-published-release.ps1` afterwards. A
release is repaired when the script says so, not when the step summary is out
of sight.

### A quick manual cross-check

Useful as a second pair of eyes, and the only option if you have no `pwsh`:

```sh
gh release view v1.1.1 --repo LesleyMurfin/magic-tray --json assets --jq '.assets[] | "\(.name) \(.size) \(.state)"'

base=https://github.com/LesleyMurfin/magic-tray/releases/download/v1.1.1
curl -fLO "$base/MagicTray-v1.1.1-win-x64.zip"
curl -fLO "$base/MagicTray-v1.1.1-win-x64.zip.sha256"
sha256sum -c MagicTray-v1.1.1-win-x64.zip.sha256
```

And read the release notes: they must carry the two-row digest table, not just
a list of merged pull requests.

Only once this is green do you start [§3 phase C](#phase-c--after-the-release-is-published-and-verified-pr-2).

---

## 6. The winget submission

`.github/workflows/winget-submit.yml` exists, works, and **cannot finish the
job**. Know that before you promise anyone a `winget install`.

What it is:

- `workflow_dispatch` **only**. It never runs on push, on tag, or as part of
  the release.
- `dry_run` defaults to **true**. A dry run downloads the published ZIP and its
  sidecar, verifies one against the other, patches the real digest into the
  installer manifest, runs `winget validate` (tolerated as a failure on a dry
  run only, because the runner image sometimes lacks App Installer), and
  uploads the patched manifests as the artifact
  `winget-manifests-<version>`. That is genuinely useful on its own: it proves
  the published asset matches its sidecar and the manifests patch cleanly.
- It requires the manifest folder for that tag to already exist. Its `locate`
  step fails otherwise: the workflow patches the hash, it does not invent
  manifests. So [§3 phase C](#phase-c--after-the-release-is-published-and-verified-pr-2)
  step 1 has to be merged first.
- With `dry_run` off it downloads a version-pinned `wingetcreate.exe` and
  refuses to execute it unless **both** the pinned SHA-256 and a valid
  Microsoft Authenticode signature check out.

What blocks a real submission — neither item is something any automation here
can solve:

1. **The `WINGET_CREATE_GITHUB_TOKEN` secret does not exist on this
   repository.** It has to be a *classic* personal access token with the
   `public_repo` scope; fine-grained tokens are not supported by wingetcreate
   ([winget-create#595](https://github.com/microsoft/winget-create/issues/595)).
   Until it is added, every run must keep `dry_run` enabled — the workflow's
   first step fails fast otherwise. The repo's built-in `GITHUB_TOKEN` can only
   download the release asset; it cannot open a PR against another repository.
2. **A signed Microsoft CLA on the account that opens the PR**
   (<https://cla.opensource.microsoft.com>). Nothing here can sign it. And
   after the PR opens, a human community moderator has to review and approve
   it; only then does it merge, and the package appears in the `winget` source
   up to an hour later.

**Today `LesleyMurfin.MagicTray` is not in `microsoft/winget-pkgs` at all.**
`winget install LesleyMurfin.MagicTray` does not work, and `docs/install.txt`
correctly tells agents not to try it. Do not change that paragraph until the
package is actually in the catalogue.

Everything else about the submission — folder layout, why schema 1.12.0 rather
than the newest, why it installs as a ZIP with
`ArchiveBinariesDependOnPath: true`, the manual fork-and-PR flow, the
one-package-one-version-per-PR rules, and what a moderator tends to ask about
an unsigned portable app — is in
[`packaging/winget/README.md`](packaging/winget/README.md). Read it there
rather than trusting a summary.

---

## 7. What went wrong with v1.1.0

Read this as a description of a failure mode that is still armed, not as
history.

### Timeline

| When | What |
|---|---|
| **2026-09-02** | `v1.1.0` was tagged. `release.yml` ran and reported **SUCCESS**. |
| **2026-09-16** | The *good* version of `release.yml` — the one that builds `MagicTray-<tag>-win-x64.zip`, writes a `.sha256` sidecar, writes `SHA256SUMS`, and puts a digest table in the notes — was committed. |

Two weeks *after* the tag. GitHub Actions runs the workflow file as it exists at
the tagged commit, so the 2 September run used the **old** workflow and
published three assets:

- a bare 186 MB `MagicMouseTray.exe`
- `Install-KeyboardBattery.cmd`
- `kbd-patch-cachedservices.ps1`

No ZIP. No checksum of any kind. The release notes are a bare list of pull
requests.

### What is still broken because of it

- `packaging/winget/manifests/l/LesleyMurfin/MagicTray/1.1.0/LesleyMurfin.MagicTray.installer.yaml`
  has an `InstallerUrl` pointing at `MagicTray-v1.1.0-win-x64.zip` — **an asset
  that does not exist on the release** — and an `InstallerSha256` of 64 zeros.
- `LesleyMurfin.MagicTray` is not in `microsoft/winget-pkgs`; the raw manifest
  URL 404s.
- `https://magictray.app/install.txt` tells an AI agent to verify the SHA-256
  of its download against a published checksum. For v1.1.0 there is none. An
  agent that follows the file correctly reaches STEP 2c, stops, and tells the
  user the release published no checksum — which is the right behaviour and a
  bad look.

### Root cause

`scripts/verify-release.ps1` runs in the `Verify release artifacts` step,
**before** `Create GitHub release`, and it inspects local files in `publish/`
and `dist/`. Nothing re-checked the release after it was published. A workflow
can therefore succeed while publishing the wrong asset set, and did.

### The lesson

**A green workflow run does not mean a correct release.** It means the steps
that were in the workflow file *at that commit* exited zero. Verify the
published artefact, from outside, every time — that is what
`scripts/verify-published-release.ps1` and [§1.5](#1-preconditions) are for.

---

## 8. Checklist

Copy this into your working notes and tick it as you go.

```text
PHASE A — before the tag (PR 1)
[ ] Branch off main; every commit made with `git commit -s`
[ ] csproj <Version>/<AssemblyVersion>/<FileVersion> == the version to tag, and agree
    (already 1.1.1 / 1.1.1.0 / 1.1.1.0 -> for v1.1.1 there is nothing to change)
[ ] Nothing else touched: manifests, docs/index.html, docs/install.txt stay on 1.1.0
[ ] pwsh -File scripts/check-version-sync.ps1  -> exit 0, ::notice that csproj is ahead
[ ] PR green on all nine required checks, merged to main

PHASE B — the tag
[ ] git switch main && git pull --ff-only
[ ] Tagged commit contains the current release.yml:
      git show HEAD:.github/workflows/release.yml | grep -n 'Package portable ZIP\|Verify the published release'
[ ] No GitHub release exists for the tag yet, and it was not created from the Releases UI
[ ] git tag -a v1.1.1 -m "Magic Tray v1.1.1" && git push origin v1.1.1
[ ] Watch the `release` run: build, test, publish, stage, package, verify artifacts,
    notes, create release, verify published release — all green
[ ] Nine assets attached, ZIP first; notes carry the two-row digest table

PHASE B.1 — verify what is actually published
[ ] pwsh -File scripts/verify-published-release.ps1 -Tag v1.1.1   -> exit 0
[ ] Manual cross-check: sha256sum -c MagicTray-v1.1.1-win-x64.zip.sha256
[ ] STOP HERE if either fails. Delete release + tag, fix, re-cut. Do not hand-upload.

PHASE C — promote the released version (PR 2), in this order
[ ] Copy the winget manifest folder to the new version; PackageVersion in all three
    files; InstallerUrl tag + asset name; ReleaseDate; ReleaseNotesUrl;
    InstallerSha256 stays 64 zeros
[ ] docs/index.html: softwareVersion, brand <small> badge, every "Download vX.Y.Z",
    datePublished
[ ] docs/install.txt: the one "current release is vX.Y.Z" line in STEP 1, wording kept
[ ] Unchecked prose: README.md (header line, screenshot alt text, "Tray menu (X.Y.Z)"
    heading + ToC link, footer line, shipped-release paragraph), llms.txt,
    docs/llms.txt, SECURITY.md (table + example commands)
[ ] csproj bumped to the next version in development, all three properties
[ ] pwsh -File scripts/check-version-sync.ps1  -> exit 0
[ ] PR green, merged. IndexNow pings automatically on docs/** changes.
[ ] Site shows the new version (if not: CLOUDFLARE.md section 2, edge cache)

PHASE D — winget, optional and human-gated
[ ] Run `Submit to winget-pkgs` with dry_run ON; it must verify the asset against its
    sidecar and patch the manifests cleanly
[ ] A real submission needs WINGET_CREATE_GITHUB_TOKEN (absent) and a signed Microsoft
    CLA (no automation can sign it). Do not claim winget support until the package is
    in the catalogue. Detail: packaging/winget/README.md
```
