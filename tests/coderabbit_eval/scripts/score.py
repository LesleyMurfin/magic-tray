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
     "line": 14,
     "body": "This query concatenates user input into SQL."}

This shape was NOT confirmed against a live CodeRabbit export; it mirrors the
GitHub review-comments API, which CodeRabbit posts through. Tolerated aliases:
``file``/``filename``/``path`` for the path, and ``line``/``original_line``/
``start_line``/``position`` for the line. A top-level object wrapping the list
under ``comments`` is also accepted. An empty file (e.g. ``/dev/null``) parses
as zero comments rather than an error.

Matching heuristic
------------------
A ``must_flag`` fixture is a true positive when some comment's path matches the
fixture file (suffix match) AND either the comment line is within +/-3 of the
gold line, or the comment body contains a keyword for the fixture's category
(see ``CATEGORY_KEYWORDS``). A control fixture is a false positive when any
comment matches its path at all. If the keyword requirement proves too strict
against a real export, drop it and match on file+line proximity alone, and note
that change here.

Exit codes: 0 on a successful report (whatever the verdict); 2 only for
genuinely invalid JSON or an unreadable/invalid labels manifest. This is a
reporting tool, not a test-suite gate.
"""

from __future__ import annotations

import argparse
import json
import os
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
        "except",
        "broad exception",
        "bare except",
        "swallow",
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
        payload = payload.get("comments", [])
    if not isinstance(payload, list):
        raise ScoreError(
            f"comments file {path} must contain a JSON array of comment objects"
        )
    return [item for item in payload if isinstance(item, dict)]


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
    for entry in payload:
        if not isinstance(entry, dict) or "file" not in entry:
            raise ScoreError(f"labels file {path} has an entry without a 'file' key")
    return payload


def comment_path(comment: dict[str, Any]) -> str:
    for key in ("path", "file", "filename"):
        value = comment.get(key)
        if isinstance(value, str) and value:
            return value
    return ""


def comment_line(comment: dict[str, Any]) -> int | None:
    for key in ("line", "original_line", "start_line", "position"):
        value = comment.get(key)
        if isinstance(value, bool):
            continue
        if isinstance(value, int):
            return value
        if isinstance(value, str) and value.strip().lstrip("-").isdigit():
            return int(value)
    return None


def paths_match(gold_file: str, candidate: str) -> bool:
    """Suffix-tolerant path comparison, normalized to forward slashes."""
    if not candidate:
        return False
    gold = os.path.normpath(gold_file).replace(os.sep, "/").lstrip("./")
    other = os.path.normpath(candidate).replace(os.sep, "/").lstrip("./")
    return gold == other or gold.endswith("/" + other) or other.endswith("/" + gold)


def body_mentions_category(comment: dict[str, Any], category: str) -> bool:
    body = comment.get("body")
    if not isinstance(body, str):
        return False
    lowered = body.lower()
    return any(word in lowered for word in CATEGORY_KEYWORDS.get(category, ()))


def fixture_is_hit(entry: dict[str, Any], comments: list[dict[str, Any]]) -> bool:
    """True when some comment plausibly reports this fixture's planted bug."""
    gold_line = entry.get("line")
    category = str(entry.get("category", ""))
    for comment in comments:
        if not paths_match(str(entry["file"]), comment_path(comment)):
            continue
        line = comment_line(comment)
        if (
            isinstance(gold_line, int)
            and line is not None
            and abs(line - gold_line) <= LINE_TOLERANCE
        ):
            return True
        if body_mentions_category(comment, category):
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
    flagged_total = true_positives + false_positives
    precision = true_positives / flagged_total if flagged_total else 1.0
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
