# Workflows

Every check that runs on this repository, what it protects, and how to run it
yourself before pushing. Nine workflow files; the first nine rows below are the
checks that gate a pull request, and each runs on its own trigger - several are
path-filtered, so a given PR sees only the subset its changes touch. All of them
run on GitHub-hosted runners; none needs a self-hosted runner, and only the two
release-side workflows need a secret.

`main` is protected by a ruleset: no direct pushes, no force pushes, and all
nine checks below must pass on a pull request before it can merge. There are no
bypass actors — the maintainer goes through a PR too.

Because every one of the nine is a *required* check, none of them filters the
`pull_request` trigger by path: a check skipped by a path filter never reports a
conclusion, and a required check that never reports blocks the PR forever. The
`push` triggers do keep their path filters, so main is not re-linted for
nothing.

| Check run | File | Trigger | Runner | Required on `main`? |
|---|---|---|---|---|
| `Build and test` | `ci.yml` | every PR, push `main`, dispatch | windows-latest | yes |
| `Verify publish` | `ci.yml` | every PR, push `main`, dispatch | windows-latest | yes |
| `PowerShell lint` | `ps-lint.yml` | every PR; push `main` on `**.ps1`; dispatch | ubuntu-latest | yes |
| `Workflow lint` | `actionlint.yml` | every PR; push `main` on `.github/workflows/**`; dispatch | ubuntu-latest | yes |
| `CodeQL (csharp)` | `codeql.yml` | every PR, push `main`, Mondays 07:23 UTC, dispatch | ubuntu-latest | yes |
| `Site checks` | `site.yml` | every PR; push `main` on `docs/**`; dispatch | ubuntu-latest | yes |
| `Version sync` | `packaging.yml` | every PR; push `main` on `packaging/**`, csproj, `docs/index.html`; dispatch | ubuntu-latest | yes |
| `Winget manifest` | `packaging.yml` | same as `Version sync` | ubuntu-latest | yes |
| `DCO sign-off` | `dco.yml` | PR opened/reopened/synchronize/ready | ubuntu-latest | yes |
| `Build and publish release` | `release.yml` | tag `v*` | windows-latest | n/a (release) |
| `Submit to winget-pkgs` | `winget-submit.yml` | manual dispatch only | windows-latest | n/a (manual) |

## Run the checks locally

```powershell
dotnet test MagicMouseTray.Tests/MagicMouseTray.Tests.csproj -c Release   # Build and test (Windows only)
pwsh -File scripts/check-site.ps1                                         # Site checks
pwsh -File scripts/check-version-sync.ps1                                 # Version sync
Invoke-ScriptAnalyzer -Path . -Recurse -Settings ./PSScriptAnalyzerSettings.psd1 -Severity Error,Warning
```

`dotnet test` needs Windows: the test assembly targets
`net8.0-windows10.0.17763.0`. Everything else is cross-platform.

---

## ci.yml — `Build and test`, `Verify publish`

- **Purpose**: the two gates that have to be green for any code change. `Build
  and test` restores, builds and runs the xunit suite. `Verify publish` proves
  the release path still works on an ordinary PR: it checks every `Content
  Include` in the csproj still exists on disk (the MSB3030 class of break),
  publishes the single-file exe, stages the keyboard-unlock and diagnostic
  scripts, packages the portable ZIP with `scripts/package-release.ps1`, then
  gates the result with `scripts/verify-release.ps1`.
- **Runner**: windows-latest — the TFM is `net8.0-windows10.0.17763.0`, so the
  tests cannot execute anywhere else.
- **Permissions**: `contents: read`. **Concurrency**: `ci-<ref>`,
  cancel-in-progress. **Timeout**: 20 min per job.
- **Artifacts**: `test-results` (TRX, `if: always()`) and `portable-zip` (the
  packaged ZIP plus its `.sha256`), 14-day retention — reviewers can download a
  PR build instead of rebuilding it.
- **Secrets**: none.
- **No NuGet cache**: the repo has no lock file, so `setup-dotnet`'s `cache:
  true` would fail, and `actions/cache` is not on the allowed-actions list. A
  restore of three packages is not worth either.

## release.yml — `Build and publish release`

- **Purpose**: on a `v*` tag: test, publish single-file self-contained win-x64,
  optionally Authenticode-sign, package the portable ZIP, verify it, write the
  release notes, and create the GitHub release with the ZIP first.
- **Permissions**: `contents: write` (needed by `gh release create`).
  **Timeout**: 30 min. **No concurrency group** — a tag build must never be
  cancelled by a later one.
- **Secrets**: `SIGN_PFX_BASE64`, `SIGN_PFX_PASSWORD`, both optional. The
  signing step is gated on `env.SIGN_PFX_BASE64 != ''`, and packaging runs after
  it so the exe inside the ZIP is the signed one.

## ps-lint.yml — `PowerShell lint`

- **Purpose**: the shipped scripts are the half of this product that is not
  compiled. A syntax error in `package-release.ps1` breaks a tagged release; one
  in `diagnose-driver.ps1` breaks a user's support session. Step 1 parses every
  `.ps1` with the real PowerShell parser; step 2 runs PSScriptAnalyzer at
  Error+Warning with `PSScriptAnalyzerSettings.psd1`.
- **Runner**: ubuntu-latest. Both the parser and the analyzer are
  cross-platform and neither loads the Windows-only cmdlets these scripts call,
  so a Windows runner would buy nothing.
- **Pinned**: PSScriptAnalyzer `1.25.0`. Bump it deliberately — Dependabot does
  not see PowerShell Gallery modules.
- **Permissions**: `contents: read`. **Timeout**: 10 min.

## actionlint.yml — `Workflow lint`

- **Purpose**: catches expression typos, bad action inputs, invalid runner
  labels and broken embedded shell before a workflow run does.
- **Pinned**: actionlint `1.7.12`, installed from the release tarball with its
  SHA256 verified in-workflow. No third-party action is used, so there is no
  action to audit.
- **Permissions**: `contents: read`. **Timeout**: 10 min.

## codeql.yml — `CodeQL (csharp)`

- **Purpose**: C# code scanning; results land in the repository's Security tab.
- **Build mode**: `none` (buildless extraction) on ubuntu-latest. A build-based
  run would need windows-latest plus the Windows SDK reference packs — much
  slower and more fragile for a WinForms/WPF target.
- **Permissions**: `security-events: write`, `actions: read`, `contents: read`.
  **Schedule**: Mondays 07:23 UTC, so a new query pack finds old code.
  **Timeout**: 30 min.

## site.yml — `Site checks`

- **Purpose**: `docs/` is published by GitHub Pages straight from `main`
  (deploy-from-branch, folder `/docs`, custom domain magictray.app). There is no
  build step, so a renamed page or image would break the live site silently.
  `scripts/check-site.ps1` checks dead internal links and assets in the HTML and
  in `site.css`, sitemap integrity both ways, `docs/CNAME` and `docs/.nojekyll`,
  `robots.txt` syntax plus its coverage of the unpublished Markdown, and stale
  `localhost`/`github.io` links.
- **Deliberately not a Pages deploy workflow**: adding one would switch the
  Pages source away from deploy-from-branch and take the custom domain with it.
- **Permissions**: `contents: read`. **Timeout**: 10 min.

## packaging.yml — `Version sync`, `Winget manifest`

- **Purpose**: the version appears in four independent places. `Version sync`
  (`scripts/check-version-sync.ps1`) enforces the real rule — published metadata
  (the three winget manifests, the three version strings in `docs/index.html`)
  tracks the **latest release**, while the csproj may run ahead of it as the next
  version in development. It also derives the expected ZIP asset name from
  `scripts/package-release.ps1` rather than hardcoding it. `Winget manifest`
  parses the manifest set offline and asserts the keys winget-pkgs will require,
  accepting the deliberate 64-zero installer digest.
- **Permissions**: `contents: read`. **Timeout**: 10 min per job.

## dco.yml — `DCO sign-off`

- **Purpose**: `CONTRIBUTING.md` requires `git commit -s`; this enforces it.
  Every non-merge, non-bot commit in the PR needs a real `Signed-off-by:` Git
  trailer: it has to sit in the message's final trailer block, and that block
  must hold nothing but trailers. A sign-off buried in the body with prose after
  it is text, not a trailer, and is rejected.
  Merge commits and `dependabot[bot]`/`github-actions[bot]` commits are skipped —
  they are unsigned by design and would otherwise block dependency PRs forever.
- **Fix a red run**: `git commit -s` for new commits, or
  `git rebase --signoff origin/main && git push --force-with-lease`.
- **Permissions**: `contents: read`, `pull-requests: read`. **Timeout**: 5 min.

## winget-submit.yml — `Submit to winget-pkgs`

- **Purpose**: the mechanical half of a winget submission — download the
  published asset, verify it against its `.sha256` sidecar, patch the real digest
  into the installer manifest, upload the patched set, and run `wingetcreate
  submit` when `dry_run` is off. `wingetcreate.exe` is pinned to an immutable
  release asset and executed only after both its published SHA256 and its
  Microsoft Authenticode signature verify; `winget validate` failures are
  tolerated on a dry run only.
- **Manual only**, `dry_run` defaults to `true`, and it fails fast when
  `WINGET_CREATE_GITHUB_TOKEN` is missing and `dry_run` is off. That secret does
  not exist on this repository yet; see `packaging/winget/README.md`.
- **Runner**: windows-latest (`winget validate` and `wingetcreate` are
  Windows-only). **Permissions**: `contents: read`.

---

## Conventions

Anything added here follows the same rules:

1. GitHub-hosted runners only; `windows-latest` only when Windows is genuinely
   required.
2. Only `actions/*` and `github/codeql-action/*` actions. Any other tool is
   installed from a version-pinned release download with a verified checksum.
3. Every `uses:` is a 40-character commit SHA with a trailing `# vX.Y.Z`
   comment. Dependabot (`.github/dependabot.yml`, weekly, 5-day cooldown,
   `chore(ci)` prefix) proposes the bumps.
4. Every workflow declares an explicit minimal `permissions:` block; every job
   declares `timeout-minutes`; everything except `release.yml` declares a
   `concurrency:` group with `cancel-in-progress`.
5. Repo-owned checks live in `scripts/check-*.ps1`, emit `::error
   file=…,line=…::` annotations, print a one-line summary, and exit non-zero.
   That way the same script is the CI gate and the local pre-push check.
6. Tools that Dependabot cannot see are pinned in the workflow that uses them
   and named in this file: PSScriptAnalyzer `1.25.0`, actionlint `1.7.12`.
