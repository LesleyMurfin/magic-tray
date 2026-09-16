# CodeRabbit evaluation harness

A standalone, disposable harness that measures whether CodeRabbit's PR review catches known bugs.
It is specified by `specs/coderabbit-eval-harness.md`.

## The fixtures are test data, not code to fix

The five files under `security/` and `correctness/` contain deliberate, planted vulnerabilities.
**Do not "fix" them** — a fix destroys the measurement. `gold/labels.json` is the only record of
what each planted bug is and where it lives; the fixtures deliberately carry no in-file markers, so
the reviewer under test is never told what to look for or that it is being graded. Editing a fixture
means re-checking the line numbers in `gold/labels.json`.

The `sk-fake-` value in `security/hardcoded_secret.py` is a non-functional placeholder. It is never
a real credential, has never been issued by any vendor, and authenticates against nothing.

The three files under `controls/` are clean. A review comment on any of them counts as a false
positive.

## Running it

Export the PR review comments to JSON, then from the repository root:

```bash
python3 tests/coderabbit_eval/scripts/score.py \
  --comments <export.json> \
  --labels tests/coderabbit_eval/gold/labels.json
```

Exit `0` means a report was produced, whatever the verdict; exit `2` means the input was invalid.

## Why it lives here

This repository's test suite is the C# xUnit project `MagicMouseTray.Tests/`, driven by
`.github/workflows/ci.yml`. This Python tree is neither built nor run by any CI job and is not an
importable package — `score.py` is run by path. It sits in a top-level `tests/` root to keep it out
of the shipped solution, and it is expected to be deleted once the CodeRabbit purchasing question is
settled.

The harness records no run output. Results belong on the PR or issue that motivated the run.
