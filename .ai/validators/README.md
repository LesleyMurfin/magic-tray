# Vendored PR gates (copies — RILEY is canonical)

Every file in this directory is a **byte-for-byte copy** of a RILEY-canonical
validator. Do **not** edit them here. Fix the canonical copy in RILEY, then
re-vendor (see *Refreshing* below).

## What is vendored, and from where

| Vendored path in this repo            | Canonical source                                  |
| ------------------------------------- | ------------------------------------------------- |
| `.ai/validators/pr-body-check.py`     | `~/projects/RILEY/.ai/validators/pr-body-check.py`     |
| `.ai/validators/pr-admission-check.py`| `~/projects/RILEY/.ai/validators/pr-admission-check.py`|
| `.ai/validators/adr-number-check.py`  | `~/projects/RILEY/.ai/validators/adr-number-check.py`  |
| `scripts/pr_diagram.py`               | `~/projects/RILEY/scripts/pr_diagram.py`               |
| `templates/diagrams/*`                | `~/projects/RILEY/templates/diagrams/*`                |

- Copied: **2026-09-15**
- RILEY `HEAD` at copy time: **`c5cbcd63f`**
- All five are standard-library only (`argparse`, `re`, `sys`, `subprocess`,
  `pathlib`, `collections`). None import a RILEY-internal module. Both paths
  they resolve are repo-relative, so they work unmodified here:
  `scripts/pr_diagram.py` reads `<repo>/templates/diagrams/`, and
  `adr-number-check.py` reads `<repo>/design/adr/` (absent in this repo, so
  `github.py` skips that gate entirely).

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
`.ai/*` + `!.ai/validators/` + `.ai/validators/__pycache__/`; the rest of
`.ai/` (`learning/`, `telemetry/`, `test-runs/`, snapshots, scratch) stays
ignored.

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

Never diverge from RILEY. If a gate is wrong, it is wrong in RILEY.
