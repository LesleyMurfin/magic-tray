# Vendored PR gates (copies — upstream is canonical)

Every file in this directory, plus `scripts/pr_diagram.py` and
`templates/diagrams/*`, is a **byte-for-byte copy** of upstream gate tooling.
Do **not** edit them here. Fix upstream, then re-vendor (see *Refreshing* below).

## What is vendored, and from where

| Vendored path in this repo            | Canonical source                                  |
| ------------------------------------- | ------------------------------------------------- |
| `.ai/validators/pr-body-check.py`     | `~/projects/RILEY/.ai/validators/pr-body-check.py`     |
| `.ai/validators/pr-admission-check.py`| `~/projects/RILEY/.ai/validators/pr-admission-check.py`|
| `.ai/validators/adr-number-check.py`  | `~/projects/RILEY/.ai/validators/adr-number-check.py`  |
| `scripts/pr_diagram.py`               | `~/projects/RILEY/scripts/pr_diagram.py`               |
| `templates/diagrams/*`                | `~/projects/RILEY/templates/diagrams/*`                |

- Copied: **2026-09-15**
- Source mirror: `~/projects/RILEY` @ **`c5cbcd63f`** (canonical origin: `LesleyMurfin/revive_ai-dev` per RILEY's `.ai/governance/sync-manifest.yml:13`)
- All four Python files are standard-library only (`argparse`, `re`, `sys`,
  `subprocess`, `pathlib`, `collections`). None import an internal module. Both
  paths they resolve are repo-relative, so they work unmodified here:
  `scripts/pr_diagram.py` reads `<repo>/templates/diagrams/`, and
  `adr-number-check.py` reads `<repo>/design/adr/` (absent in this repo, so
  `github.py` skips that gate entirely; the validator is pre-provisioned).
- `templates/diagrams/` carries 6 archetype skeletons (`.txt`), plus `none`
  which intentionally has no skeleton file. Note: path references in
  `templates/diagrams/README.md` (`design/`, `scripts/tests/`, `.ai/skills/`)
  resolve in upstream RILEY, not in this consumer repo.
## Why this repo needs its own copy

`~/projects/scripts/github.py` resolves each gate **relative to the repo being
published**, not relative to RILEY:

- `_validate_pr_body_standard` → `<repo>/.ai/validators/pr-body-check.py`
- `_validate_pr_admissions` → `<repo>/.ai/validators/pr-admission-check.py`
- `_validate_pr_diagram_set` → `<repo>/scripts/pr_diagram.py`
  (which in turn loads `<repo>/templates/diagrams/<archetype>.txt`)
- `_validate_adr_numbering` → `<repo>/.ai/validators/adr-number-check.py`

Each one **fails closed**: a missing file is not "skip", it is
`… verifier missing at <path> — cannot verify, blocking`. So without these
files, `github.py create-pr` cannot open a PR from this repo at all.

That is by design. `~/projects/AGENTS.md`: *"the `ai/`-branch rule and
validators apply uniformly to every repo, and a repo without its own `.ai/`
folder (with `.ai/validators/`) should hard-block wrapper writes until one
exists."* The fix is therefore to give this repo its own `.ai/validators/`, not
to change the gating logic in `git.py`/`github.py` — that is a shared-infra
change to RILEY-canonical scripts and must go through `/change-management`
(AGENTS.md RULE #20/#21).

`.gitignore` had to be reworked for this to be possible: a blanket `.ai/` entry
excludes the parent directory, and git cannot re-include a path whose parent is
excluded, so `!.ai/validators/` alone does nothing. The rules are now
`.ai/*` + `!.ai/validators/` + `__pycache__/`; the rest of
`.ai/` (`learning/`, `telemetry/`, `test-runs/`, snapshots, scratch) stays
ignored, while `__pycache__/` covers bytecode under `.ai/validators/` AND
`scripts/` at any depth.

## Refreshing

```sh
cp -p ~/projects/RILEY/.ai/validators/pr-body-check.py      .ai/validators/
cp -p ~/projects/RILEY/.ai/validators/pr-admission-check.py .ai/validators/
cp -p ~/projects/RILEY/.ai/validators/adr-number-check.py   .ai/validators/
cp -p ~/projects/RILEY/scripts/pr_diagram.py                scripts/
cp -p ~/projects/RILEY/templates/diagrams/*                 templates/diagrams/
git -C ~/projects/RILEY rev-parse --short HEAD   # record the new SHA above
```

Then update the date and SHA in this file. Verify with:

```sh
python3 .ai/validators/pr-body-check.py --help
python3 .ai/validators/pr-admission-check.py --help
python3 scripts/pr_diagram.py classify
```

Never diverge from upstream. If a gate is wrong, it is wrong upstream.

## Known upstream issues (tracked, not forked)

Three defects were identified and reproduced during PR #142 review, then filed
upstream at `LesleyMurfin/revive_ai-dev#1112` rather than patched locally:

1. `scripts/pr_diagram.py`: `_git` converts non-zero exits to `""`, causing
   an unresolvable `--base` to silently classify as `generic` with exit 0
   (fails open instead of closed).
2. `scripts/pr_diagram.py`: `changed_paths` drops source paths on rename rows
   (`R100\told\tnew`), so moving a validator to a docs path drops the
   enforcement diagram requirement and drops the file from `_snapshot`.
3. `.ai/validators/pr-admission-check.py`: `_strip_fences` only strips
   backtick fences, so `~~~` CommonMark blocks leak text (e.g. `TODO`) into
   the admission detector and cause false-positive blocks.

These will be incorporated on the next upstream re-vendor once merged there.
