#!/usr/bin/env python3
"""Unfiled-admission gate for PR bodies (issue #38).

Binary, dependency-free gate. Given a PR body (via ``--body-file`` or stdin),
exits non-zero when the body **admits a defect but does not file it**.

The failure this exists to stop
------------------------------
An agent discovers a real defect, writes it down in the PR body — "flagged, not
fixed", "out of scope here", "reconciling the validator's scope is a follow-up"
— and it never becomes a GitHub issue. The finding survives only as long as
someone re-reads that PR body. Measured on this repo's own history, several
genuine defects were disclosed exactly this way and were never tracked.

The rule
--------
An admission phrase must be **accompanied by an issue reference** (``#NNN``)
somewhere in its own markdown section. Section scope — not line scope — is
deliberate: bodies legitimately write the admission as a bullet and the issue
number a line or two away, and a line-scoped rule would cry wolf on every one of
them. A gate that cries wolf gets disabled.

What is deliberately NOT an admission
-------------------------------------
Tuned against the 75 real PR bodies in this repo (see ``## Tuning`` in the PR).
These carve-outs are each an observed false positive, not speculation:

* **Fenced code blocks** — a diff quoting a source ``# TODO`` is not a claim
  about this PR. Line numbering is preserved when they are stripped.
* **Headings** — ``## Follow-ups`` is structure, not a confession.
* **Blockquotes** — quoted issue text or a quoted execution prompt.
* **The plural "follow-ups"** — always the section name. The singular
  ("is a follow-up") is the admission form.
* **Negated forms** — "no known issue", "not prose TODOs", "none applicable".
* **Deictic cross-references** — "filed as a follow-up **below**", "see above".
  The body IS filing it; the number just lives in another section.

Usage:
    python3 .ai/validators/pr-admission-check.py --body-file pr-body.md
    gh pr view N --json body -q .body | python3 .ai/validators/pr-admission-check.py

Importable (hyphenated file — see tests/conftest.py):
    errors = pr_admission_check.check_admissions(body)   # [] == compliant

Exit codes: 0 = compliant, 1 = one or more unfiled admissions, 2 = usage error.
"""

from __future__ import annotations

import argparse
import re
import sys

# The admission vocabulary. Every alternative here is a phrase an agent actually
# uses when conceding a defect it is not fixing in this PR.
#
# ``follow[-\s]?up(?!s)`` — singular only. "Follow-ups" (plural) is the name of
# the mandated PR-body section (pr-body-check.py check 5) and appears in
# virtually every compliant body; matching it would flag nearly the whole repo.
_ADMISSION_RE = re.compile(
    r"""(
        not\s+fixed
      | out\s+of\s+scope
      | left\s+alone
      | known\s+(?:issue|bug|defect|problem)
      | \bTODO\b
      | follow[-\s]?up(?!s)
      | separate\s+issue
      | separately\s+tracked
      | worth\s+a\s+look\s+separately
    )""",
    re.IGNORECASE | re.VERBOSE,
)

_ISSUE_RE = re.compile(r"#\d+")

# A negation immediately preceding the phrase inverts it: "no known issue",
# "none applicable", "not prose TODOs". Bounded to 40 characters and to the same
# sentence (no ``.`` in between) so it cannot swallow an unrelated earlier "not".
_NEGATION_RE = re.compile(
    r"\b(?:no|none|zero|not|never|without|nothing)\b[^.\n]{0,40}$",
    re.IGNORECASE,
)

# "filed as a follow-up below", "see above", "tracked in §4" — the body is
# pointing at where the reference lives, so the section-local rule would
# misjudge it. Bounded to the 60 characters following the phrase.
_DEICTIC_RE = re.compile(
    r"^[^.\n]{0,60}?\b(?:below|above|see\s|§|section\b|listed\s+under)",
    re.IGNORECASE,
)

_HEADING_RE = re.compile(r"\s*#{1,6}\s")


def _strip_fences(body: str) -> str:
    """Blank out fenced code blocks, preserving line count so numbers stay true."""
    return re.sub(
        r"```[^\n]*\n.*?```",
        lambda m: "\n" * m.group(0).count("\n"),
        body,
        flags=re.DOTALL,
    )


def _sections(text: str) -> list[tuple[str, int]]:
    """Split ``text`` at markdown headings. Returns [(section_text, first_lineno)]."""
    out: list[tuple[str, int]] = []
    current: list[str] = []
    start = 1
    for lineno, line in enumerate(text.split("\n"), start=1):
        if _HEADING_RE.match(line) and current:
            out.append(("\n".join(current), start))
            current, start = [], lineno
        current.append(line)
    if current:
        out.append(("\n".join(current), start))
    return out


def find_admissions(body: str) -> list[tuple[int, str, str]]:
    """Every unfiled admission as ``(line_number, matched_phrase, line_text)``."""
    found: list[tuple[int, str, str]] = []
    text = _strip_fences(body)

    for section, start in _sections(text):
        # An issue reference anywhere in the section discharges the whole
        # section: the finding is filed, and which line carries the number is
        # a formatting choice, not a governance one.
        if _ISSUE_RE.search(section):
            continue
        for offset, line in enumerate(section.split("\n")):
            if _HEADING_RE.match(line) or line.lstrip().startswith(">"):
                continue
            for match in _ADMISSION_RE.finditer(line):
                if _NEGATION_RE.search(line[: match.start()]):
                    continue
                if _DEICTIC_RE.match(line[match.end() :]):
                    continue
                found.append((start + offset, match.group(0), line.strip()))
                break  # one report per line is enough to act on
    return found


def check_admissions(body: str) -> list[str]:
    """Validate ``body``. Returns human-readable errors; empty list == compliant."""
    errors: list[str] = []
    for lineno, phrase, line in find_admissions(body):
        snippet = line if len(line) <= 120 else line[:117] + "..."
        errors.append(
            f"line {lineno}: admits a defect (\"{phrase}\") with no issue "
            f"reference in its section — {snippet}\n"
            f"      Fix: file it (`python3 scripts/github.py create-issue "
            f"--title ... --body-file ... --labels ...`) and cite `#NNN` here, "
            f"or reword if it is not a defect."
        )
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Fail a PR body that admits a defect without filing it (#38).",
    )
    parser.add_argument(
        "--body-file",
        help="Path to a file containing the PR body. Reads stdin if omitted.",
    )
    args = parser.parse_args(argv)

    if args.body_file:
        try:
            with open(args.body_file, encoding="utf-8") as handle:
                body = handle.read()
        except OSError as exc:  # pragma: no cover - usage error path
            print(f"ERROR: cannot read --body-file: {exc}", file=sys.stderr)
            return 2
    else:
        body = sys.stdin.read()

    if not body.strip():
        print("ERROR: empty PR body", file=sys.stderr)
        return 2

    errors = check_admissions(body)
    if errors:
        print("PR body admits defects that were never filed (#38):", file=sys.stderr)
        for err in errors:
            print(f"  - {err}", file=sys.stderr)
        return 1

    print("PR body OK — no unfiled admissions (#38).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
