#!/usr/bin/env python3
"""ADR numbering + index verifier (issue #78).

Binary, dependency-free gate over ``design/adr/``. Exits non-zero unless every
one of these holds:

  1. **Unique numbers** — no two ADR files share an ``ADR-NNNN`` prefix.
     (``main`` carried three files all claiming ``ADR-0004``; that is what this
     check exists to stop recurring.)
  2. **Well-formed names** — every ``.md`` in the directory except ``README.md``
     matches ``ADR-NNNN-<slug>.md``.
  3. **Header agrees with filename** — the file's first ``# ADR-NNNN`` heading
     carries the same number as its filename. Catches a half-finished renumber.
  4. **Listed in the index** — every ADR file is linked from the ``## Index``
     table of ``design/adr/README.md``.
  5. **No dangling index rows** — every file an index row links to exists.

The root cause of #78 was that ADR numbers are allocated at authoring time on
long-lived branches and reconciled "at merge" by a human step that never ran.
This validator replaces that step: it is deterministic, so it can run in the
pre-PR gate and simply refuse the collision.

Usage:
    python3 .ai/validators/adr-number-check.py                 # repo default dir
    python3 .ai/validators/adr-number-check.py --adr-dir DIR
    python3 .ai/validators/adr-number-check.py --next          # next free number

Importable (hyphenated file — see tests/conftest.py):
    errors = adr_number_check.check_adr_numbering(Path("design/adr"))  # [] == clean

Exit codes: 0 = clean, 1 = one or more violations, 2 = usage error.
"""

from __future__ import annotations

import argparse
import re
import sys
from collections import defaultdict
from pathlib import Path

# ``ADR-0004-resource-finops-governance.md``
_FILENAME_RE = re.compile(r"^ADR-(\d{4})-(.+)\.md$")

# First markdown H1 of an ADR: ``# ADR-0004 — Title``
_HEADING_RE = re.compile(r"^#\s+ADR-(\d{4})\b", re.MULTILINE)

# A markdown link in the index whose target is an ADR file:
# ``[ADR-0004](ADR-0004-resource-finops-governance.md)``
_INDEX_LINK_RE = re.compile(r"\]\((ADR-\d{4}-[^)]+\.md)\)")

INDEX_FILENAME = "README.md"
_INDEX_HEADING_RE = re.compile(r"^##+\s+Index\s*$", re.MULTILINE)


def default_adr_dir() -> Path:
    """``design/adr`` relative to the repo root that contains this validator."""
    return Path(__file__).resolve().parents[2] / "design" / "adr"


def adr_files(adr_dir: Path) -> list[Path]:
    """Every ``ADR-NNNN-<slug>.md`` in ``adr_dir``, sorted by name."""
    return sorted(
        p
        for p in adr_dir.glob("ADR-*.md")
        if p.is_file() and _FILENAME_RE.match(p.name)
    )


def _index_section(adr_dir: Path) -> str | None:
    """Text of the index README from its ``## Index`` heading onward.

    Returns ``None`` when the README is absent — the caller turns that into a
    fail-closed error rather than a silent pass. Falls back to the whole file
    when there is no ``## Index`` heading, so a reformatted README degrades to a
    weaker check instead of a false failure.
    """
    index = adr_dir / INDEX_FILENAME
    if not index.is_file():
        return None
    text = index.read_text(encoding="utf-8")
    match = _INDEX_HEADING_RE.search(text)
    return text[match.start() :] if match else text


def next_free_number(adr_dir: Path) -> int:
    """Lowest number strictly greater than every number in use.

    ADR numbers are append-only — gaps are history (a superseded or renumbered
    ADR), never a free slot to recycle.
    """
    used = [int(m.group(1)) for p in adr_files(adr_dir) if (m := _FILENAME_RE.match(p.name))]
    return max(used) + 1 if used else 1


def check_adr_numbering(adr_dir: Path) -> list[str]:
    """Return a list of violations; empty list means compliant."""
    errors: list[str] = []

    if not adr_dir.is_dir():
        return [f"ADR directory not found: {adr_dir} — cannot verify, blocking"]

    # (2) Well-formed filenames.
    for path in sorted(adr_dir.glob("*.md")):
        if path.name == INDEX_FILENAME:
            continue
        if not _FILENAME_RE.match(path.name):
            errors.append(
                f"{path.name}: malformed ADR filename — expected ADR-NNNN-<slug>.md"
            )

    files = adr_files(adr_dir)

    # (1) Unique numbers.
    by_number: dict[str, list[str]] = defaultdict(list)
    for path in files:
        match = _FILENAME_RE.match(path.name)
        assert match is not None  # guaranteed by adr_files()
        by_number[match.group(1)].append(path.name)
    for number, names in sorted(by_number.items()):
        if len(names) > 1:
            errors.append(
                f"duplicate ADR number ADR-{number} claimed by {len(names)} files: "
                + ", ".join(sorted(names))
                + " — renumber all but one to the next free number "
                f"(ADR-{next_free_number(adr_dir):04d})"
            )

    # (3) Heading agrees with filename.
    for path in files:
        match = _FILENAME_RE.match(path.name)
        assert match is not None
        file_number = match.group(1)
        heading = _HEADING_RE.search(path.read_text(encoding="utf-8"))
        if heading is None:
            errors.append(f"{path.name}: no '# ADR-NNNN ...' heading found")
        elif heading.group(1) != file_number:
            errors.append(
                f"{path.name}: heading says ADR-{heading.group(1)} but the filename "
                f"says ADR-{file_number} — half-finished renumber"
            )

    # (4)/(5) Index coverage, both directions.
    index_text = _index_section(adr_dir)
    if index_text is None:
        errors.append(
            f"ADR index missing at {adr_dir / INDEX_FILENAME} — cannot verify, blocking"
        )
        return errors

    linked = set(_INDEX_LINK_RE.findall(index_text))
    for path in files:
        if path.name not in linked:
            errors.append(
                f"{path.name}: not listed in {INDEX_FILENAME} — add its row to the "
                "## Index table in the same commit that adds the ADR"
            )
    for name in sorted(linked):
        if not (adr_dir / name).is_file():
            errors.append(
                f"{INDEX_FILENAME}: index row links {name}, which does not exist"
            )

    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Verify ADR numbers are unique and the index is complete (#78)."
    )
    parser.add_argument(
        "--adr-dir",
        type=Path,
        default=None,
        help="ADR directory to check (default: <repo>/design/adr)",
    )
    parser.add_argument(
        "--next",
        action="store_true",
        help="print the next free ADR number (e.g. ADR-0015) and exit 0",
    )
    args = parser.parse_args(argv)

    adr_dir = args.adr_dir or default_adr_dir()

    if args.next:
        if not adr_dir.is_dir():
            print(f"ERROR: ADR directory not found: {adr_dir}", file=sys.stderr)
            return 2
        print(f"ADR-{next_free_number(adr_dir):04d}")
        return 0

    errors = check_adr_numbering(adr_dir)
    if errors:
        print(f"FAIL: ADR numbering/index violations in {adr_dir} (#78):", file=sys.stderr)
        for err in errors:
            print(f"  - {err}", file=sys.stderr)
        return 1

    print(f"PASS: {len(adr_files(adr_dir))} ADRs, unique numbers, index complete")
    return 0


if __name__ == "__main__":
    sys.exit(main())
