# CodeRabbit Eval Harness

## Current State

There is no automated way to measure whether CodeRabbit's review comments actually catch the
security and correctness bugs this repo cares about. Today, trust in CodeRabbit as a review
gate is anecdotal — someone reads a PR, sees a comment, and assumes coverage is adequate. There
is no fixture corpus of known-bad code, no scoring against a labeled ground truth, and no
repeatable command that tells us precision/recall for a given CodeRabbit configuration or model
version. This spec defines a throwaway evaluation package to close that gap. Nothing in this
spec implies CodeRabbit is misconfigured today; it establishes the measurement tool needed to
find out.

## Summary

Build a standalone, disposable Python package `tests/coderabbit_eval/` containing:

- A set of small, self-contained source fixtures split into two groups:
  - `security/` and `correctness/` fixtures — each contains exactly one known, intentionally
    planted bug that CodeRabbit is expected to flag ("must-catch").
  - `controls/` fixtures — clean, idiomatic code with no planted bug, used to measure false
    positives (CodeRabbit must NOT flag these).
- `gold/labels.json` — the ground-truth manifest mapping each fixture file to its expected
  finding (fixture path, bug category, expected line, short description, and a boolean
  `must_flag`).
- `scripts/score.py` — a CLI that takes a CodeRabbit PR-comments export (JSON) and the
  `gold/labels.json` manifest, matches comments to fixtures by file/line proximity and keyword
  heuristics, and emits precision, recall, and a per-fixture hit/miss table, plus an overall
  `verdict: buy | skip` recommendation based on a recall threshold (default: recall >= 0.8 on
  must-catch fixtures and zero false positives on controls => `buy`; otherwise `skip`).

This spec covers fixture design, labeling schema, and the scoring script's contract only. It
does NOT implement the fixtures or the script — that is follow-up work for the next agent.

## Files to Touch

New files only (nothing existing is modified):

- `tests/coderabbit_eval/__init__.py` — empty, marks the package.
- `tests/coderabbit_eval/security/sql_injection.py` — planted SQLi via string concatenation.
- `tests/coderabbit_eval/security/command_injection.py` — planted `os.system` call with
  unsanitized user input.
- `tests/coderabbit_eval/security/hardcoded_secret.py` — planted fake hardcoded secret
  (`API_KEY = "sk-fake-1234567890abcdef"` — clearly non-functional, never a real credential).
- `tests/coderabbit_eval/correctness/broad_except.py` — planted `except Exception: pass`
  swallowing an error silently.
- `tests/coderabbit_eval/correctness/toctou_race.py` — planted TOCTOU bug (e.g. `os.path.exists`
  check followed by a separate open/write with no atomic guard).
- `tests/coderabbit_eval/controls/clean_query.py` — parameterized DB query, no injection risk.
- `tests/coderabbit_eval/controls/clean_subprocess.py` — `subprocess.run` with an argument list
  (no shell=True), no injection risk.
- `tests/coderabbit_eval/controls/clean_error_handling.py` — narrow exception handling with
  logging, no silent swallow.
- `tests/coderabbit_eval/gold/labels.json` — ground-truth manifest for all fixtures above.
- `tests/coderabbit_eval/scripts/score.py` — scoring CLI.

## Step-by-Step

1. Create the five must-catch fixtures under `security/` and `correctness/`. Each file is a
   small, realistic snippet (10–30 lines) with exactly one planted bug and a comment marking the
   exact line (`# GOLD-BUG: <category>`) for human traceability (the scorer uses
   `gold/labels.json`, not the comment, as its source of truth).
   - `sql_injection.py`: build a SQL string via f-string/`+` concatenation of a user-supplied
     variable, then execute it.
   - `command_injection.py`: pass a user-supplied string into `os.system(...)` without
     sanitization or `shlex.quote`.
   - `hardcoded_secret.py`: a fake, obviously non-functional secret constant assigned directly in
     source (label the fixture itself with a comment stating it is fake and never a real key).
   - `broad_except.py`: a `try/except Exception: pass` block that discards an error that should
     propagate or be logged.
   - `toctou_race.py`: a check-then-act pattern (e.g., check file doesn't exist, then create it
     in a separate step) vulnerable to a race condition.
2. Create the three control fixtures under `controls/` — each is the "fixed" idiomatic version of
   a similar operation (parameterized query, argument-list subprocess call, narrow exception
   handling with logging) so CodeRabbit has no legitimate reason to flag them.
3. Write `gold/labels.json` as a JSON array of objects, one per fixture file:
   ```json
   [
     {
       "file": "tests/coderabbit_eval/security/sql_injection.py",
       "category": "sql_injection",
       "line": 12,
       "description": "SQL query built via string concatenation of user input",
       "must_flag": true
     },
     {
       "file": "tests/coderabbit_eval/controls/clean_query.py",
       "category": "control",
       "line": null,
       "description": "Parameterized query, no injection risk",
       "must_flag": false
     }
   ]
   ```
   Include all 8 fixtures (5 must-catch + 3 controls).
4. Write `scripts/score.py`:
   - Argparse CLI: `score.py --comments <path-to-json-or-/dev/null> --labels
     tests/coderabbit_eval/gold/labels.json [--threshold 0.8]`.
   - `--comments` accepts a JSON file shaped like a CodeRabbit PR review comments export: a list
     of objects each with at least `path`, `line`, and `body` (adapt to CodeRabbit's actual export
     shape if available; otherwise document the assumed shape inline in a module docstring).
   - Reading `/dev/null` (empty input) MUST parse as zero comments, not error.
   - Matching logic: a `must_flag` fixture counts as a true positive if any comment's `path`
     matches the fixture's `file` (suffix match is fine) AND either the comment's `line` is
     within ±3 of the gold `line`, or the comment `body` contains a category keyword (e.g. "sql
     injection", "command injection", "secret", "except", "race condition" — keyword list per
     category, defined as a constant dict in the script).
   - A `must_flag: false` (control) fixture counts as a false positive if any comment matches its
     `path` at all.
   - Compute: `recall = true_positives / count(must_flag fixtures)`,
     `precision = true_positives / (true_positives + false_positives)` (define precision as 1.0
     when there are zero flagged comments on any fixture, to avoid divide-by-zero).
   - Print a per-fixture table (file, expected category, hit/miss) and a summary line:
     `recall=<r> precision=<p> verdict=<buy|skip>`.
   - `verdict = "buy"` iff `recall >= threshold` (default 0.8) and `false_positives == 0`, else
     `"skip"`.
   - Exit code 0 always (this is a reporting tool, not a test-suite gate); no exceptions on
     empty/malformed comment lists beyond a clear error message and non-zero exit for genuinely
     invalid JSON.
5. Do not wire this into CI, PR templates, or any existing test runner in this pass — it is a
   standalone package meant to be run manually against an exported CodeRabbit comments dump.

## Verification

Run from the worktree root once `scripts/score.py` exists:

```
python -m py_compile tests/coderabbit_eval/scripts/score.py
```
Expected: exits 0, no output (syntax-valid).

```
python tests/coderabbit_eval/scripts/score.py \
  --comments /dev/null \
  --labels tests/coderabbit_eval/gold/labels.json
```
Expected: runs without raising, treats `/dev/null` as zero comments, prints a summary line with
`recall=0.0` (or `recall=0.00`) and `verdict=skip` (zero comments cannot satisfy the recall
threshold on any must-catch fixture).

No linters, formatters, or project-wide test suites should be run for this spec's scope.

## Notes for Next Agent

- This spec intentionally stops short of writing the fixtures and `score.py` — that is the next
  agent's job, following the Step-by-Step section exactly.
- The `hardcoded_secret.py` fixture's secret MUST be obviously fake (e.g. prefixed `sk-fake-` or
  similar) and MUST be documented as fake in a comment directly above it, so no secret-scanning
  tool or human reviewer mistakes it for a live credential. Do not reuse any real-looking key
  format tied to a real vendor's production key format if avoidable; if a realistic-looking
  format is needed to trigger CodeRabbit's own secret detector, keep the value clearly
  non-functional (e.g. all-same-character suffix) and call this out again in the fixture file's
  docstring.
- The exact shape of a CodeRabbit PR-comments JSON export was not confirmed against a live
  export in this pass — the next agent should pull one real export (via GitHub API or CodeRabbit
  dashboard) before finalizing `score.py`'s parsing logic, and adjust the assumed schema in the
  module docstring if it differs from `{path, line, body}`.
- Keyword-per-category matching in `score.py` is a heuristic; if it produces too many
  false-negative matches against a real export, consider matching on file+line proximity alone
  (drop the keyword requirement) as a fallback strategy, and note the change in the script's
  docstring.
- No commit was made beyond what `git worktree add` produces; the next agent should commit the
  fixtures/script/manifest together as a single logical change.
