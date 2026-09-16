#!/usr/bin/env python3
"""Factory PR-body standard verifier (riley issue #6788).

Binary, dependency-free gate. Given a PR body (via ``--body-file`` or stdin),
exits non-zero unless the body carries every section the factory PR-body
standard mandates:

  1. At least one fenced ASCII diagram (box-drawing / arrow characters)
  2. A ``Current State`` -> ``Future State`` pair
  3. A ``Provenance`` block (produced by scripts/provenance.py, issue #6786)
  4. A Documentation-confirmation line (names docs updated + which
     doc-maintenance command was run, e.g. ``/prd update-decisions`` /
     ``/prd update-progress``, or the literal ``none applicable``)
  5. A ``Follow-ups`` section (linked issues, each carrying an execution prompt)
  6. Issue-linkage discipline: a ``Closes #NNN`` (fully closing) or
     ``Refs #NNN`` / ``Part of #NNN`` (partial) reference
  7. A RULE-050 governing ticket: ``PRD-####``, ``PRD-NONE``, ``PRB-####``,
     ``INFRA``, or ``HOTFIX``

This validator also carries the RULE-050 governing-ticket gate (item 7) so a
body missing its ticket fails HERE rather than at ``scripts/github.py
create-pr`` — i.e. at validation time, not on a mutating GitHub call. The
pattern is defined once, in ``GOVERNING_TICKET_RE`` below, and imported by
``scripts/github.py::_validate_pr_body``. Placeholder hygiene remains
github.py's alone.

Usage:
    python3 .ai/validators/pr-body-check.py --body-file pr-body.md
    gh pr view N --json body -q .body | python3 .ai/validators/pr-body-check.py

Importable:
    from importlib import ...            # hyphenated file — see tests/conftest.py
    errors = pr_body_check.check_pr_body(body_text)   # [] == compliant

Exit codes: 0 = compliant, 1 = one or more sections missing, 2 = usage error.
"""

from __future__ import annotations

import argparse
import re
import sys

# Characters that mark a block as an ASCII / box-drawing diagram. Unicode
# box-drawing + arrows, plus common pure-ASCII diagram tokens.
_BOX_DRAWING = "─│┌┐└┘├┤┬┴┼╭╮╰╯━┃╱╲╳→←↑↓↔⟶⟵▶◀▲▼"
_ASCII_ARROWS = ("-->", "<--", "->", "<-", "==>", "|--", "+--", "--+", "+---", "___")


def _fenced_blocks(body: str) -> list[str]:
    """Return the inner text of every fenced (```-delimited) code block."""
    # Non-greedy match of ```...``` including an optional info string line.
    return re.findall(r"```[^\n]*\n(.*?)```", body, flags=re.DOTALL)


def _has_ascii_diagram(body: str) -> bool:
    for block in _fenced_blocks(body):
        if any(ch in block for ch in _BOX_DRAWING):
            return True
        if any(tok in block for tok in _ASCII_ARROWS):
            return True
    return False


# PR-diagram harness (#75): the §1 diagram is archetype-generated
# (scripts/pr_diagram.py classify) and must be filled + show current->future.
_STUB_MARKER = "<FILL:"


def _diagram_has_stub(body: str) -> bool:
    """True if any fenced block still carries an unfilled archetype slot."""
    return any(_STUB_MARKER in block for block in _fenced_blocks(body))


def _diagram_has_current_future(body: str) -> bool:
    """True if a fenced block shows a current/future (or before/after) split."""
    for block in _fenced_blocks(body):
        low = block.lower()
        if ("current" in low and "future" in low) or (
            "before" in low and "after" in low
        ):
            return True
    return False


# Issue #112: a heading may carry an enumerator before its title — `## 6.
# Documentation`, `## (d) Documentation confirmation`, `### 1.2 Documentation`.
# The old pattern demanded the word immediately after the hashes and so
# false-negatived on every numbered body. Only *enumerator-shaped* prefixes are
# tolerated (digits / a single letter / roman numerals, optionally bracketed and
# followed by punctuation) — prose is still rejected, so `## Notes on
# documentation` does not satisfy the heading requirement.
_ENUMERATOR = r"(?:[(\[]?(?:\d+(?:\.\d+)*|[a-z]|[ivx]+)[)\].:]*[ \t]+){0,2}"


def _heading_re(word: str) -> re.Pattern[str]:
    """Compile a heading matcher for ``word``, tolerant of enumerator prefixes."""
    return re.compile(
        rf"(?:^|\n)[ \t]*#{{1,6}}[ \t]*{_ENUMERATOR}{word}\b",
        re.IGNORECASE,
    )


_DOC_HEADING_RE = _heading_re(r"documentation")
_FOLLOWUPS_HEADING_RE = _heading_re(r"follow[\s\-]*ups?")


# RULE-050 governing ticket (PRB-0131). SINGLE DEFINITION of the pattern:
# scripts/github.py::_validate_pr_body imports this constant instead of
# spelling the regex a second time, so create-pr and validation-time agree by
# construction. Accepted tokens mirror the `ticket` field of the RULE-050 PR
# naming standard: a parent PRD (PRD-####), governance with no parent PRD
# (PRD-NONE), a problem record (PRB-####), infra-only work (INFRA), or an
# emergency fix (HOTFIX).
GOVERNING_TICKET_RE = re.compile(
    r"\b(PRD-[0-9]{4}|PRD-NONE|PRB-[0-9]{4}|INFRA|HOTFIX)\b"
)

# The message create-pr has emitted since PRB-0131 — kept verbatim so an agent
# who has hit this block before recognises it at validation time.
GOVERNING_TICKET_ERROR = (
    "`PRD-NNNN` missing — include a RULE-050 governing ticket "
    "(PRD-#### / PRD-NONE / PRB-#### / INFRA / HOTFIX)"
)


def check_pr_body(body: str) -> list[str]:
    """Validate ``body`` against the factory PR-body standard.

    Returns a list of human-readable error strings. Empty list == compliant.
    """
    errors: list[str] = []
    low = body.lower()

    # 1. Fenced ASCII diagram
    if not _has_ascii_diagram(body):
        errors.append(
            "No fenced ASCII diagram found — add at least one ``` block using "
            "box-drawing/arrow characters (─│┌┐└┘→ or -->)."
        )
    else:
        # 1a (#75): the archetype skeleton must be filled, not pasted raw.
        if _diagram_has_stub(body):
            errors.append(
                "§1 diagram still contains unfilled `<FILL: ...>` archetype slots "
                "— fill each from your diff (`scripts/pr_diagram.py classify`)."
            )
        # 1b (#75): the diagram must show current -> future, not just wiring.
        if not _diagram_has_current_future(body):
            errors.append(
                "§1 diagram must show current -> future — label the fenced block "
                "with CURRENT/FUTURE (or BEFORE/AFTER). See `scripts/pr_diagram.py "
                "classify` for the right archetype."
            )

    # 2. Current State -> Future State pair
    has_current = re.search(r"current[\s\-]*state", low) is not None
    has_future = re.search(r"future[\s\-]*state", low) is not None
    if not (has_current and has_future):
        missing = []
        if not has_current:
            missing.append("`Current State`")
        if not has_future:
            missing.append("`Future State`")
        errors.append(
            "Current State -> Future State pair incomplete — missing "
            + " and ".join(missing)
            + "."
        )

    # 3. Provenance block (from issue #6786's scripts/provenance.py)
    if (
        not re.search(r"(^|\n)\s*#{1,6}?\s*provenance\b", low)
        and "provenance" not in low
    ):
        errors.append(
            "No Provenance block — include the block emitted by "
            "scripts/provenance.py (issue #6786)."
        )

    # 4. Documentation confirmation line
    has_doc_heading = _DOC_HEADING_RE.search(body) is not None
    has_doc_signal = (
        "none applicable" in low
        or "/prd update-decisions" in low
        or "/prd update-progress" in low
        or re.search(r"update[\s\-]*(decisions|progress)", low) is not None
    )
    if not (has_doc_heading and has_doc_signal):
        errors.append(
            "Documentation confirmation missing — add a `## Documentation` "
            "section naming docs updated and which doc-maintenance command ran "
            "(`/prd update-decisions` / `/prd update-progress`), or state "
            "`none applicable`."
        )

    # 5. Follow-ups section (linked issues carrying execution prompts)
    if not _FOLLOWUPS_HEADING_RE.search(body):
        errors.append(
            "No Follow-ups section — add `## Follow-ups` listing linked issues "
            "(each carrying an execution prompt), or state `none`."
        )

    # 6. Issue-linkage discipline: Closes (full) or Refs/Part of (partial)
    if not re.search(r"\b(closes|refs|part of)\s+#[0-9]+", low):
        errors.append(
            "No issue linkage — use `Closes #NNN` only when this PR fully "
            "closes the issue, otherwise `Refs #NNN` (and update the issue)."
        )

    # 7. RULE-050 governing ticket. Enforced here, not only at create-pr time,
    #    so a missing ticket surfaces from a read-only validation run instead of
    #    from a rejected mutating GitHub call.
    if not GOVERNING_TICKET_RE.search(body):
        errors.append(GOVERNING_TICKET_ERROR)

    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Verify a PR body against the factory PR-body standard (#6788).",
    )
    parser.add_argument(
        "--body-file",
        help="Path to a file containing the PR body. Reads stdin if omitted.",
    )
    args = parser.parse_args(argv)

    if args.body_file:
        try:
            with open(args.body_file, encoding="utf-8") as fh:
                body = fh.read()
        except OSError as exc:  # pragma: no cover - usage error path
            print(f"ERROR: cannot read --body-file: {exc}", file=sys.stderr)
            return 2
    else:
        body = sys.stdin.read()

    if not body.strip():
        print("ERROR: empty PR body", file=sys.stderr)
        return 2

    errors = check_pr_body(body)
    if errors:
        print("PR body FAILS the factory standard (#6788):", file=sys.stderr)
        for err in errors:
            print(f"  - {err}", file=sys.stderr)
        return 1

    print(
        "PR body OK — all six factory sections present "
        "and a RULE-050 governing ticket found (#6788)."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
