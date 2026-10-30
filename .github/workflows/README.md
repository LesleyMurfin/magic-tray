# Workflows

Every check that runs on this repository, what it protects, and how to run it
yourself before pushing. The two tables below are the list: one row per check
run, with a `Required on main?` column. Read the gating set off that column
rather than off a number in a sentence - a count written in prose is the one
thing here that goes stale without anyone noticing.

## What gates a merge

`main` is protected by one ruleset, `main protection`: active, no bypass actors
(the maintainer goes through a PR too), no branch deletion, no force push, a
pull request required, and required status checks. There is no classic branch
protection on `main`, so that ruleset is the whole gate. Every row marked
**yes** below is one of its required contexts, and nothing else blocks the
merge button. Re-derive that list at any time:

```bash
gh api repos/LesleyMurfin/magic-tray/rulesets/23525492 \
  --jq '.rules[] | select(.type=="required_status_checks")
                 | .parameters.required_status_checks[].context'
```

Because a required context has to report on every pull request, no required
check filters its `pull_request` trigger by path: a check skipped by a path
filter never reports a conclusion, and a required check that never reports
blocks the PR forever. The `push` triggers do keep their path filters, so main
is not re-linted for nothing.

## What runs on a pull request

Every row here appears on a pull request. Only the rows marked yes gate it.

| Check run | Source | Trigger | Runner | Required on `main`? |
|---|---|---|---|---|
| `Build and test` | `ci.yml` | every PR, push `main`, dispatch | windows-latest | yes |
| `Verify publish` | `ci.yml` | every PR, push `main`, dispatch | windows-latest | yes |
| `PowerShell lint` | `ps-lint.yml` | every PR; push `main` on `**.ps1`, `PSScriptAnalyzerSettings.psd1`, `.github/workflows/**`, `scripts/check-workflow-pwsh.py`; dispatch | ubuntu-latest | yes |
| `Workflow lint` | `actionlint.yml` | every PR; push `main` on `.github/workflows/**`; dispatch | ubuntu-latest | yes |
| `CodeQL (csharp)` | `codeql.yml` | every PR, push `main`, Mondays 07:23 UTC, dispatch | ubuntu-latest | yes |
| `Site checks` | `site.yml` | every PR; push `main` on `docs/**`, `scripts/check-site.ps1`, the workflow; dispatch | ubuntu-latest | yes |
| `Version sync` | `packaging.yml` | every PR; push `main` on `packaging/**`, csproj, `docs/index.html`, `docs/install.txt`, the checkers, the workflow; dispatch | ubuntu-latest | yes |
| `Winget manifest` | `packaging.yml` | same as `Version sync` | ubuntu-latest | yes |
| `DCO sign-off` | `dco.yml` | PR opened/reopened/synchronize/ready_for_review | ubuntu-latest | yes |
| `aeo-checks` | `ci.yml` | every PR, push `main`, dispatch | ubuntu-latest | no |
| `no-draft-prs` | `no-draft-prs.yml` | PR opened/reopened/ready_for_review/converted_to_draft/synchronize | ubuntu-latest | no |
| `CodeQL` | GitHub Advanced Security app, no file in this repo | posted per PR beside `CodeQL (csharp)` | n/a | no |
| `CodeRabbit` | `coderabbitai` app, no file in this repo | posted per PR; currently reports `skipped` | n/a | no |

### More runs than gates

A pull request shows more runs than the ruleset gates, and the checks tab does
not say which is which. These are all of the extras:

- `aeo-checks` - the `aeo-checks` job in `ci.yml`, which parses the JSON-LD
  graph in `docs/`. It declares no `name:`, so GitHub falls back to the job id,
  which is why it is the one lowercase run in the list. Not required: a broken
  `@id` is worth seeing, not worth blocking a merge on.
- `no-draft-prs` - `no-draft-prs.yml`. Fails while the pull request is a draft.
  Not in the ruleset, so it does not block the merge button either.
- `CodeQL` - the summary the GitHub Advanced Security app posts ("No new alerts
  in code changed by this pull request"). The gating context is the
  similarly-named `CodeQL (csharp)`, the `analyze` job in `codeql.yml`; this
  second run belongs to a different app and is not in the ruleset.
- `CodeRabbit` - a third-party review app. No file in this repository
  configures it, it reports `skipped` here, and it never blocks a merge.

To reconcile the two lists on a specific commit:

```bash
gh api repos/LesleyMurfin/magic-tray/commits/<sha>/check-runs \
  --jq '.check_runs[] | "\(.name)\t\(.conclusion)\t\(.app.name)"'
```

## What never runs on a pull request

| Workflow | File | Trigger | Runner |
|---|---|---|---|
| `Submit URLs to IndexNow` | `indexnow.yml` | push `main` on `docs/**`; dispatch | ubuntu-latest |
| `Build and publish release` | `release.yml` | push tag `v*` | windows-latest |
| `Submit to winget-pkgs` | `winget-submit.yml` | manual dispatch only | windows-latest |

Everything in both tables runs on GitHub-hosted runners; nothing here needs a
self-hosted runner, and only the two release-side workflows take a secret.

## Run the checks locally

The commands live in [`CONTRIBUTING.md`](../../CONTRIBUTING.md#pull-requests),
next to the rest of what a contributor has to do before opening a PR, and they
are not repeated here: two copies of one list is how this file went stale in
the first place. Where a check has a local equivalent it is the same script CI
runs, so a green local run is a green check. `dotnet test` is the one command
that needs Windows - the test assembly targets `net8.0-windows10.0.17763.0`;
every other checker is cross-platform PowerShell 7 or Python.

---

## ci.yml — `Build and test`, `Verify publish`, `aeo-checks`

- **Purpose**: the two gates that have to be green for any code change, plus
  one advisory job. `Build and test` restores, builds and runs the xunit suite.
  `Verify publish` proves the release path still works on an ordinary PR: it
  checks every `Content Include` in the csproj still exists on disk (the
  MSB3030 class of break), publishes the single-file exe, stages the
  keyboard-unlock and diagnostic scripts, packages the portable ZIP with
  `scripts/package-release.ps1`, then gates the result with
  `scripts/verify-release.ps1`.
- **`aeo-checks`**: `scripts/check-aeo.ps1` parses the JSON-LD graph and the
  head and footer metadata in `docs/`. Pages are served from `main` with no
  build step, so CI is the only place that graph is parsed before a crawler
  reads it, and a dangling `@id` costs entity resolution silently. It is the
  one job in this file with no `name:`, so it reports under its job id, and it
  is not in the ruleset. Link resolution and sitemap membership belong to
  `Site checks`; the two scripts do not overlap.
- **Runner**: windows-latest for `Build and test` and `Verify publish` - the
  TFM is `net8.0-windows10.0.17763.0`, so the tests cannot execute anywhere
  else. `aeo-checks` is ubuntu-latest; it only reads files.
- **Permissions**: `contents: read`. **Concurrency**: `ci-<ref>`,
  cancel-in-progress. **Timeout**: 20 min for each Windows job, 10 min for
  `aeo-checks`.
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
  release notes, create the GitHub release with the ZIP first, then re-verify
  that release as published.
- **Two verification steps, and they are not redundant**:
  `scripts/verify-release.ps1` gates the artefacts on the runner's disk before
  anything is uploaded; `scripts/verify-published-release.ps1` runs after
  `gh release create` and re-reads the release through the GitHub API and the
  public asset URLs. Only the second one can notice that the wrong asset set
  was published, which is how the v1.1.0 run went green while shipping no ZIP
  and no checksum. It is a hard gate — no `continue-on-error`. The whole
  release procedure is in [`RELEASING.md`](../../RELEASING.md).
- **Permissions**: `contents: write` (needed by `gh release create`).
  **Timeout**: 30 min. **No concurrency group** — a tag build must never be
  cancelled by a later one.
- **Secrets**: `SIGN_PFX_BASE64`, `SIGN_PFX_PASSWORD`, both optional. The
  signing step is gated on `env.SIGN_PFX_BASE64 != ''`, and packaging runs after
  it so the exe inside the ZIP is the signed one.

## ps-lint.yml — `PowerShell lint`

- **Purpose**: the shipped scripts are the half of this product that is not
  compiled. A syntax error in `package-release.ps1` breaks a tagged release; one
  in `diagnose-driver.ps1` breaks a user's support session. The workflow parses
  every `.ps1` with the real PowerShell parser, parses the PowerShell embedded
  in workflow `run:` blocks (`scripts/check-workflow-pwsh.py`), runs the
  redaction corpus (`scripts/check-redaction.ps1`), then runs PSScriptAnalyzer
  at Error+Warning with `PSScriptAnalyzerSettings.psd1`. Named, not numbered,
  on purpose: step ordinals are what rotted here last time.
- **Why the workflow `run:` parser is here**: the `.ps1` glob never sees
  PowerShell written inline in a workflow `run:` block. That gap shipped -
  `indexnow.yml` carried a bare `$maxAttempts:` inside a string, PowerShell
  read it as a scope qualifier, the whole step was a ParserError, and every
  IndexNow submission had never once run with nothing red anywhere.
- **Why the redaction corpus is here**: `ConvertTo-MacSafeText` in
  `mm-hid-report.ps1` is the only control between a reporter's hardware
  identifiers and a public issue. This workflow already owns the `.ps1` files
  and carries no `pull_request` paths filter, so the corpus runs on every pull
  request instead of being skipped by exactly the diffs that do not look like
  they touch redaction.
- **Runner**: ubuntu-latest. The parser and the analyzer are cross-platform and
  neither loads the Windows-only cmdlets these scripts call. No script is
  executed as a whole either: `check-redaction.ps1` does run PowerShell, but it
  slices one pure string function out of `mm-hid-report.ps1` with the parser,
  so that script's `Add-Type` P/Invoke block never compiles here. A Windows
  runner would buy nothing.
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

## indexnow.yml — `Submit URLs to IndexNow`

- **Purpose**: tells Bing, and through it the answer engines that reuse the
  Bing index, that `docs/` changed, instead of waiting for the next natural
  recrawl. Pages serves `docs/` verbatim from `main`, so a push that touches
  `docs/` is the same event as "the published site changed".
- **Never runs on a pull request**: push to `main` on `docs/**`, or manual
  dispatch. It submits the live site, so there is nothing for it to do on a
  branch, and it is not a check anyone can gate on.
- **No hardcoded key or URL list**: the key is the one `docs/*.txt` whose base
  name equals its own contents, and the URLs come from `docs/sitemap.xml`, so a
  new page is submitted without anyone editing this workflow. HTTP 200 and 202
  are success; everything else fails loudly with the response body, because a
  403 (key not reachable) or 422 (URL off-host) otherwise looks like a working
  pipeline.
- **Permissions**: `contents: read`. **Timeout**: 5 min. **Concurrency**:
  `indexnow-<ref>`, deliberately not cancel-in-progress - a submission is one
  cheap request, and cancelling the older run could drop the ping for content
  that is already live.

## packaging.yml — `Version sync`, `Winget manifest`

- **Purpose**: the version appears in five independent places. `Version sync`
  (`scripts/check-version-sync.ps1`) enforces the real rule — published metadata
  (the three winget manifests, the three version strings in `docs/index.html`,
  and the illustrative release tag in the agent install guide
  `docs/install.txt`) tracks the **latest release**, while the csproj may run
  ahead of it as the next version in development. It also derives the expected
  ZIP asset name from `scripts/package-release.ps1` rather than hardcoding it.
  `Winget manifest`
  (`scripts/check-winget-manifest.py`) parses the manifest set offline and
  asserts the keys winget-pkgs will require of every `Installers` entry,
  accepting the deliberate 64-zero installer digest.
- **Permissions**: `contents: read`. **Timeout**: 10 min per job.

## dco.yml — `DCO sign-off`

- **Purpose**: `CONTRIBUTING.md` requires `git commit -s`; this enforces it.
  Every non-merge, non-bot commit in the PR needs a real `Signed-off-by:` Git
  trailer in the message's final trailer block. `git interpret-trailers --parse`
  decides what that block is, so a sign-off buried in an earlier paragraph with
  prose after it is text, not a trailer, and is rejected — while the shapes Git
  itself writes (`git cherry-pick -x` provenance lines, folded trailers) are
  accepted. Trailer tokens are matched case-insensitively, as Git matches them.
  Merge commits and `dependabot[bot]`/`github-actions[bot]` commits are skipped —
  they are unsigned by design and would otherwise block dependency PRs forever.
- **Fix a red run**: `git commit -s` for new commits, or
  `git rebase --signoff origin/main && git push --force-with-lease`.
- **Permissions**: `contents: read`, `pull-requests: read`. **Timeout**: 5 min.

## no-draft-prs.yml — `no-draft-prs`

- **Purpose**: exit 1 while `github.event.pull_request.draft` is true, including
  after `converted_to_draft`. A ready-for-review pull request passes. The job
  does not check out the repository.
- **Runner**: ubuntu-latest, the same GitHub-hosted runner as the other checks
  that are not the Windows tray app. `windows-latest` is only used where the
  .NET TFM or winget tooling requires Windows. No workflow in this repo uses a
  self-hosted runner.
- **Permissions**: `{}`. **Timeout**: 5 min. **Concurrency**:
  `no-draft-prs-<ref>`, cancel-in-progress.
- **Not a ruleset check**: branch protection was not changed, so a red run does
  not block merge until this check is added to the `main` ruleset.

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
   installed from a version-pinned release download with a verified checksum -
   with one exception, `pip install PyYAML==6.0.3` in `packaging.yml` and
   `ps-lint.yml`, which is version-pinned but not hash-pinned: a wheel hash is
   specific to the runner image's Python minor version, so pinning one would
   turn a required check red the next time the image moves.
3. Every `uses:` is a 40-character commit SHA with a trailing `# vX.Y.Z`
   comment. Dependabot (`.github/dependabot.yml`, weekly, 5-day cooldown,
   `chore(ci)` prefix) proposes the bumps.
4. Every workflow declares an explicit minimal `permissions:` block; every job
   declares `timeout-minutes`; every workflow but `release.yml` declares a
   `concurrency:` group, with `cancel-in-progress: true` everywhere except
   `indexnow.yml`, where cancelling the older run could drop the ping for
   content that is already live.
5. Repo-owned checks live in `scripts/check-*`, emit `::error
   file=…,line=…::` annotations, print a one-line summary, and exit non-zero.
   That way the same script is the CI gate and the local pre-push check.
6. Tools that Dependabot cannot see are pinned in the workflow that uses them
   and named in this file: PSScriptAnalyzer `1.25.0`, actionlint `1.7.12`,
   PyYAML `6.0.3`.
7. A new workflow, or a new job that reports its own check run, gets a row in
   one of the two tables at the top of this file on the same pull request that
   adds it - with its `Required on main?` answer filled in, and the ruleset
   actually updated if that answer is yes. The tables are what `CONTRIBUTING.md`
   sends contributors to, so a check that reports but is not listed there is
   indistinguishable from a check nobody can account for.
