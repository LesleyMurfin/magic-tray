#!/usr/bin/env python3
"""Score a CodeRabbit PR-comments export against the eval harness ground truth.

Usage
-----
    python tests/coderabbit_eval/scripts/score.py \
        --comments <path-to-json-or-/dev/null> \
        --labels tests/coderabbit_eval/gold/labels.json \
        [--threshold 0.8]

Assumed comments schema
-----------------------
The ``--comments`` file is a JSON array of objects, each with at least::

    {"path": "tests/coderabbit_eval/security/sql_injection.py",
     "line": 9,
     "body": "This query concatenates user input into SQL."}

This shape was NOT confirmed against a live CodeRabbit export; it mirrors the
GitHub review-comments API, which CodeRabbit posts through. Tolerated aliases:
``file``/``filename``/``path`` for the path, and ``line``/``original_line``/
``start_line`` for the line. A comment line must be a positive integer, either
as an int or as an all-digit string; anything else -- including ``0`` and
``"-5"`` -- counts as "no line". GitHub's ``position`` field is deliberately
NOT accepted: it is a line index inside the unified diff, not a source-file
line, and the harness has no diff to translate it through. A top-level object
is accepted only as a wrapper carrying the list under ``comments``; any other
object shape is a fatal input error rather than a silent zero-comment report,
and so is an array holding anything other than comment objects: the array must
contain only objects. An empty file (e.g. ``/dev/null``) parses as zero
comments rather than an error.

Matching heuristic
------------------
A ``must_flag`` fixture is a true positive when some comment names the fixture
file by its complete repository-relative path AND mentions a keyword for the
fixture's category (see ``CATEGORY_KEYWORDS``) AND -- when the gold entry
carries a line -- reports a line within +/-3 of it. A gold entry with no line
(``"line": null``) matches on path and keyword alone. Both signals are
required because either one on its own is satisfiable by a comment that found
nothing: five "Consider adding a type annotation." comments on the gold lines
used to print ``recall=1.00 precision=1.00 verdict=buy``. Keywords match on
word boundaries, so ``except`` cannot be claimed by the word "exceptions",
while ``os.system`` still matches inside ``os.system(...)`` prose because a
parenthesis is a non-word character. A basename-only path (e.g.
``sql_injection.py``) is rejected, because it is ambiguous across fixture
trees and would let one comment satisfy several gold entries. A control
fixture is a false positive when any comment matches its path at all.
Precision is true positives over the number of comments that landed on any
labelled fixture path, so unrelated chatter on the must-catch files lowers it
instead of leaving it pinned at 1.00. If the keyword requirement proves too
strict against a real export, relax the conjunction back to line proximity
alone, and note that change here.

Exit codes: 0 on a successful report (whatever the verdict); 2 for genuinely
invalid JSON, an unreadable/invalid labels manifest, or a ``--threshold``
outside 0.0-1.0 (rejected by argparse, which also exits 2). This is a
reporting tool, not a test-suite gate.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from typing import Any

LINE_TOLERANCE = 3

CATEGORY_KEYWORDS: dict[str, tuple[str, ...]] = {
    "sql_injection": ("sql injection", "sqli", "parameterized", "parameterised"),
    "command_injection": (
        "command injection",
        "shell injection",
        "os.system",
        "shlex",
    ),
    "hardcoded_secret": (
        "secret",
        "hardcoded credential",
        "hard-coded credential",
        "api key",
        "credential",
    ),
    "broad_except": (
        "broad except",
        "broad exception",
        "bare except",
        "except exception",
        "swallow",
        "swallows",
        "swallowed",
        "silently ignored",
    ),
    "toctou_race": ("race condition", "toctou", "time-of-check", "atomic"),
}


class ScoreError(Exception):
    """Fatal, user-facing input error."""


def load_comments(path: str) -> list[dict[str, Any]]:
    """Parse a comments export; empty input yields zero comments."""
    try:
        with open(path, encoding="utf-8") as handle:
            raw = handle.read()
    except OSError as exc:
        raise ScoreError(f"cannot read comments file {path}: {exc}") from exc

    if not raw.strip():
        return []

    try:
        payload = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ScoreError(f"invalid JSON in comments file {path}: {exc}") from exc

    if isinstance(payload, dict):
        if "comments" not in payload:
            raise ScoreError(
                f"comments file {path} must contain a JSON array of comment "
                f"objects, or an object wrapping one under 'comments'"
            )
        payload = payload["comments"]
    if not isinstance(payload, list):
        raise ScoreError(
            f"comments file {path} must contain a JSON array of comment objects"
        )
    for index, item in enumerate(payload):
        if not isinstance(item, dict):
            raise ScoreError(
                f"comments file {path} entry {index} must be a comment object, "
                f"got {type(item).__name__}"
            )
    return payload


def load_labels(path: str) -> list[dict[str, Any]]:
    """Parse the ground-truth manifest."""
    try:
        with open(path, encoding="utf-8") as handle:
            payload = json.load(handle)
    except OSError as exc:
        raise ScoreError(f"cannot read labels file {path}: {exc}") from exc
    except json.JSONDecodeError as exc:
        raise ScoreError(f"invalid JSON in labels file {path}: {exc}") from exc

    if not isinstance(payload, list) or not payload:
        raise ScoreError(f"labels file {path} must be a non-empty JSON array")
    for index, entry in enumerate(payload):
        if not isinstance(entry, dict) or "file" not in entry:
            raise ScoreError(f"labels file {path} has an entry without a 'file' key")
        where = f"labels file {path} entry {index} ({entry['file']!r})"
        if not isinstance(entry["file"], str) or not entry["file"].strip():
            raise ScoreError(f"{where}: 'file' must be a non-empty string")
        must_flag = entry.get("must_flag")
        if not isinstance(must_flag, bool):
            raise ScoreError(
                f"{where}: 'must_flag' must be a boolean, got "
                f"{type(must_flag).__name__}"
            )
        category = entry.get("category")
        if not isinstance(category, str) or not category.strip():
            raise ScoreError(f"{where}: 'category' must be a non-empty string")
        line = entry.get("line")
        if line is not None and (isinstance(line, bool) or not isinstance(line, int)):
            raise ScoreError(
                f"{where}: 'line' must be an integer or null, got "
                f"{type(line).__name__}"
            )
    return payload


def comment_path(comment: dict[str, Any]) -> str:
    for key in ("path", "file", "filename"):
        value = comment.get(key)
        if isinstance(value, str) and value:
            return value
    return ""


def comment_line(comment: dict[str, Any]) -> int | None:
    for key in ("line", "original_line", "start_line"):
        value = comment.get(key)
        if isinstance(value, bool):
            continue
        if isinstance(value, str) and value.strip().isdigit():
            value = int(value)
        if isinstance(value, int) and value > 0:
            return value
    return None


def _normalize_path(value: str) -> str:
    """Collapse separators and strip a leading ``./`` for comparison."""
    return os.path.normpath(value).replace(os.sep, "/").removeprefix("./")


def paths_match(gold_file: str, candidate: str) -> bool:
    """True when ``candidate`` names exactly the gold file, not just a basename.

    An extra leading prefix on the candidate (e.g. a checkout directory) is
    tolerated because the complete gold path is still present. A shorter,
    basename-only candidate is rejected: it cannot identify one fixture
    unambiguously, and accepting it would let a single comment satisfy several
    gold entries at once.
    """
    if not candidate:
        return False
    gold = _normalize_path(gold_file)
    other = _normalize_path(candidate)
    return gold == other or other.endswith("/" + gold)


def body_mentions_category(comment: dict[str, Any], category: str) -> bool:
    body = comment.get("body")
    if not isinstance(body, str):
        return False
    lowered = body.lower()
    return any(
        re.search(rf"(?<!\w){re.escape(word)}(?!\w)", lowered)
        for word in CATEGORY_KEYWORDS.get(category, ())
    )


def fixture_is_hit(entry: dict[str, Any], comments: list[dict[str, Any]]) -> bool:
    """True when some comment reports this fixture's planted bug.

    Requires the comment to name the fixture by its complete repository-relative
    path and to mention the category, plus - when the gold entry carries a line -
    to land within ``LINE_TOLERANCE`` of it. Either signal alone is reachable by a
    comment that found nothing, which would print ``verdict=buy`` for a reviewer
    that detected nothing.
    """
    gold_line = entry.get("line")
    category = str(entry.get("category", ""))
    for comment in comments:
        if not paths_match(str(entry["file"]), comment_path(comment)):
            continue
        if not body_mentions_category(comment, category):
            continue
        if not isinstance(gold_line, int):
            return True
        line = comment_line(comment)
        if line is not None and abs(line - gold_line) <= LINE_TOLERANCE:
            return True
    return False


def control_is_flagged(entry: dict[str, Any], comments: list[dict[str, Any]]) -> bool:
    """True when any comment lands on a control fixture at all."""
    return any(
        paths_match(str(entry["file"]), comment_path(comment)) for comment in comments
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Score a CodeRabbit comments export against gold labels."
    )
    parser.add_argument(
        "--comments",
        required=True,
        help="Path to the CodeRabbit comments JSON export (/dev/null for none).",
    )
    parser.add_argument("--labels", required=True, help="Path to gold/labels.json.")
    parser.add_argument(
        "--threshold",
        type=float,
        default=0.8,
        help="Minimum recall on must-catch fixtures for a 'buy' verdict.",
    )
    args = parser.parse_args(argv)

    if not 0.0 <= args.threshold <= 1.0:
        parser.error("--threshold must be between 0.0 and 1.0")

    try:
        comments = load_comments(args.comments)
        labels = load_labels(args.labels)
    except ScoreError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    rows: list[tuple[str, str, str]] = []
    true_positives = 0
    must_catch = 0
    false_positives = 0

    for entry in labels:
        file_name = str(entry["file"])
        category = str(entry.get("category", ""))
        if entry.get("must_flag"):
            must_catch += 1
            hit = fixture_is_hit(entry, comments)
            true_positives += int(hit)
            rows.append((file_name, category, "HIT" if hit else "MISS"))
        else:
            flagged = control_is_flagged(entry, comments)
            false_positives += int(flagged)
            rows.append((file_name, category, "FP" if flagged else "CLEAN"))

    recall = true_positives / must_catch if must_catch else 0.0
    on_corpus = sum(
        any(paths_match(str(entry["file"]), comment_path(comment)) for entry in labels)
        for comment in comments
    )
    precision = true_positives / on_corpus if on_corpus else 1.0
    verdict = "buy" if recall >= args.threshold and false_positives == 0 else "skip"

    width = max((len(row[0]) for row in rows), default=4)
    print(f"{'FIXTURE'.ljust(width)}  {'CATEGORY'.ljust(18)}  RESULT")
    for file_name, category, result in rows:
        print(f"{file_name.ljust(width)}  {category.ljust(18)}  {result}")

    print(
        f"\ncomments={len(comments)} must_catch={must_catch} "
        f"true_positives={true_positives} false_positives={false_positives}"
    )
    print(f"recall={recall:.2f} precision={precision:.2f} verdict={verdict}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
