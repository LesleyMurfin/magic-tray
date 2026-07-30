#!/usr/bin/env python3
"""PR-diagram harness (issue #75) — classify a change, emit the right archetype.

Implements the deterministic stages (1, 2, 5) of `design/pr-diagram-harness.md`.
Stage 3 (filling the archetype's ``<FILL: ...>`` slots with the real
current->future) is done by the authoring agent, which holds the diff + PRD in
context; the strengthened ``.ai/validators/pr-body-check.py`` enforces the
result (fenced ASCII + current/future split + no leftover ``<FILL:`` stub).

A change gets EVERY diagram its surfaces call for, not one: a PR that adds a
hook, a script, and a MOP carries an enforcement-flow, an automation-pipeline,
and a deploy-topology diagram.

Subcommands:
  classify   Print every required archetype + a fill-in skeleton for each + a
             current-state snapshot for a branch (default: HEAD vs ``main``).
  verify     Diff-aware gate: assert a PR body carries a diagram for every
             archetype the change requires (body-only rules live in the
             validator; only this side can see the diff).
  sync       Write/refresh the diagram block(s) into a Doc-Target doc (stage 5).

Dependency-free (stdlib only); Python 3.9+ (``from __future__ import annotations``).
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
ARCHETYPE_DIR = REPO_ROOT / "templates" / "diagrams"

# Ordered highest-signal first. A change gets EVERY archetype it triggers, not
# just the first — a PR that adds a hook, a script, and a MOP is three separate
# things to see, so it carries three diagrams.
ARCHETYPES = (
    "enforcement-flow",
    "automation-pipeline",
    "deploy-topology",
    "config-before-after",
    "skill-flow",
    "generic",
    "none",
)

# What each archetype shows. Surfaced in the emitted label so a reader knows
# whether they are looking at a flow, a structure, or a state delta.
ROLES = {
    "enforcement-flow": "process",
    "automation-pipeline": "process",
    "deploy-topology": "structure",
    "config-before-after": "state",
    "skill-flow": "process",
    "generic": "structure",
}

_DOC_BLOCK_START = "<!-- pr-diagram:start -->"
_DOC_BLOCK_END = "<!-- pr-diagram:end -->"

# Per-diagram marker the harness emits and the diff-aware gate reads back.
# Deliberately distinct from the :start/:end doc-block markers above.
_MARKER_RE = re.compile(r"<!--\s*pr-diagram:\s*([a-z-]+)\s*(?:\([a-z]+\))?\s*-->")


def _is_doc(path: str) -> bool:
    # Real documentation only — NOT operational markdown under mops/ or .ai/skills/,
    # which the taxonomy classifies by their directory.
    if path.startswith(("design/", "docs/")):
        return True
    return "/" not in path and path.endswith(".md")  # top-level README/AGENTS/etc.


def _is_test(path: str) -> bool:
    tail = path.rsplit("/", 1)[-1]
    return path.startswith("tests/") or "/tests/" in path or tail.startswith("test_")


def plan(paths: list[str], ticket: str | None = None) -> list[str]:
    """Map changed paths (+ optional RULE-050 ticket) to EVERY archetype needed.

    Pure function — the whole classifier surface. ``paths`` are repo-relative.
    Returns archetype keys in ``ARCHETYPES`` order (highest signal first), so a
    PR spanning several surfaces carries a diagram for each one. ``["none"]``
    means no archetype applies (docs-/tests-only); ``["generic"]`` means code
    changed but no specific surface matched.
    """
    paths = [p for p in paths if p]
    ticket = (ticket or "").upper()

    # Docs-only / tests-only change: no archetype applies.
    if paths and all(_is_doc(p) or _is_test(p) for p in paths):
        return ["none"]

    def touches(*prefixes: str) -> bool:
        return any(p.startswith(prefixes) for p in paths)

    keys: list[str] = []
    # .ai/validators/ is an enforcement surface too: those ARE the gates.
    if touches(".ai/hooks/", ".ai/guardrails/", ".ai/validators/"):
        keys.append("enforcement-flow")
    if touches("scripts/", ".forgejo/"):
        keys.append("automation-pipeline")
    if touches("mops/") or ticket == "INFRA":
        keys.append("deploy-topology")
    if touches(".ai/claude-config/") or any(
        re.search(r"settings.*\.json$", p) for p in paths
    ):
        keys.append("config-before-after")
    if touches(".ai/skills/"):
        keys.append("skill-flow")

    return keys or ["generic"]


def classify(paths: list[str], ticket: str | None = None) -> str:
    """The primary (highest-signal) archetype for a change. See ``plan``."""
    return plan(paths, ticket)[0]


def body_archetypes(body: str) -> list[str]:
    """Archetype keys declared by ``<!-- pr-diagram: <key> -->`` markers in a body."""
    found = [m.group(1) for m in _MARKER_RE.finditer(body)]
    return [k for k in found if k in ARCHETYPES]


def missing_archetypes(body: str, required: list[str]) -> list[str]:
    """Required archetypes with no marked diagram in ``body`` (``none`` is a pass)."""
    if required == ["none"]:
        return []
    present = set(body_archetypes(body))
    return [k for k in required if k not in present]


def load_archetype(key: str) -> str:
    """Return the ASCII skeleton for ``key`` (empty string for ``none``)."""
    if key == "none":
        return ""
    src = ARCHETYPE_DIR / f"{key}.txt"
    if not src.exists():
        raise FileNotFoundError(f"archetype not found: {src}")
    return src.read_text(encoding="utf-8").rstrip("\n")


def _git(args: list[str], repo_dir: Path) -> str:
    r = subprocess.run(
        ["git", "-C", str(repo_dir), *args],
        capture_output=True,
        text=True,
    )
    return r.stdout if r.returncode == 0 else ""


def _rev_exists(ref: str, repo_dir: Path) -> bool:
    return bool(_git(["rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}"], repo_dir).strip())


def resolve_base(base: str, repo_dir: Path) -> str:
    """Resolve ``base`` to the fork point this branch actually diverged from (#159).

    Two corrections over using ``base`` literally:

    1. **Prefer the remote-tracking ref.** In a multi-worktree repo the local
       ``main`` routinely lags ``origin/main``, so a bare ``main`` is a stale
       ref whose diff sweeps in every *other* branch that landed since.
    2. **Use the merge-base, not the tip.** ``merge-base <base> HEAD`` is the
       commit this branch forked from, so the diff contains this branch's own
       work and nothing that arrived on the base afterwards.

    Falls back to ``base`` unchanged when there is no remote-tracking ref or no
    common ancestor (e.g. a fresh repo with no ``origin``), so the caller is
    never left without a usable ref.
    """
    candidate = base
    if "/" not in base and _rev_exists(f"origin/{base}", repo_dir):
        candidate = f"origin/{base}"
    merge_base = _git(["merge-base", candidate, "HEAD"], repo_dir).strip()
    return merge_base or candidate


def changed_paths(base: str, repo_dir: Path) -> list[tuple[str, str]]:
    """Return (status, path) for the working tree vs the fork point from ``base``.

    ``base`` is resolved through :func:`resolve_base` first, so the file set is
    this branch's own work — not whatever other in-flight branches have landed
    on the base since (issue #159).

    Two-dot diff (fork point vs working tree) so committed, staged, and
    unstaged *tracked* changes are all seen; ``classify`` therefore still works
    before the branch is committed, as long as new files have been ``git add``ed.

    Untracked files are deliberately NOT included: they are local scratch (e.g.
    ``.riley-session``), belong to no PR, and were a direct source of false
    blast-radius claims in merged PR bodies (#159).
    """
    rows: list[tuple[str, str]] = []
    out = _git(["diff", "--name-status", resolve_base(base, repo_dir)], repo_dir)
    for line in out.splitlines():
        parts = line.split("\t")
        if len(parts) >= 2:
            rows.append((parts[0][:1], parts[-1]))
    return rows


def _snapshot(rows: list[tuple[str, str]]) -> str:
    """A terse current-state hint: what exists today (modified/deleted) vs new."""
    existing = [p for s, p in rows if s in ("M", "D")]
    added = [p for s, p in rows if s == "A"]
    lines = ["current-state snapshot (what exists on base today):"]
    lines += [f"  ~ {p}" for p in existing] or ["  (no pre-existing files touched)"]
    if added:
        lines.append("new in this PR:")
        lines += [f"  + {p}" for p in added]
    return "\n".join(lines)


def render_block(key: str) -> str:
    """The full §1 stanza for one archetype: marker + heading + fenced skeleton."""
    role = ROLES.get(key, "structure")
    return (
        f"<!-- pr-diagram: {key} ({role}) -->\n"
        f"#### {key} — {role}\n\n"
        "```\n"
        f"{load_archetype(key)}\n"
        "```"
    )


def cmd_classify(args: argparse.Namespace) -> int:
    repo_dir = Path(args.repo_dir).resolve()
    rows = changed_paths(args.base, repo_dir)
    keys = plan([p for _, p in rows], args.ticket)

    print(f"archetypes ({len(keys)}): {', '.join(keys)}")
    if keys == ["none"]:
        print("docs-only / tests-only change — no archetype applies (state N/A).")
        return 0
    print()
    print(_snapshot(rows))
    print()
    print(f"This change spans {len(keys)} surface(s); §1 (Architecture) carries a")
    print("diagram for EACH. Paste every stanza below, keeping its <!-- pr-diagram: -->")
    print("marker, and fill every <FILL: ...> slot from your diff.")
    print()
    for key in keys:
        print(render_block(key))
        print()
    return 0


def cmd_verify(args: argparse.Namespace) -> int:
    """Diff-aware gate: does the body carry every diagram this change needs?

    Complements ``.ai/validators/pr-body-check.py``, which is body-only (and so
    cannot know which archetypes a diff demands).
    """
    body = (
        sys.stdin.read()
        if args.body_file == "-"
        else Path(args.body_file).read_text(encoding="utf-8")
    )
    repo_dir = Path(args.repo_dir).resolve()
    rows = changed_paths(args.base, repo_dir)
    required = plan([p for _, p in rows], args.ticket)
    missing = missing_archetypes(body, required)

    if missing:
        print(
            f"ERROR: PR body is missing {len(missing)} required diagram(s) (#75):",
            file=sys.stderr,
        )
        for key in missing:
            print(f"  - {key} ({ROLES.get(key, 'structure')})", file=sys.stderr)
        print(
            f"\nThis change touches {len(required)} surface(s): {', '.join(required)}.\n"
            "Run `python3 scripts/pr_diagram.py classify` and paste every stanza.",
            file=sys.stderr,
        )
        return 1
    label = "no archetype required" if required == ["none"] else ", ".join(required)
    print(f"OK: body carries every required diagram ({label}).")
    return 0


def sync_doc(doc_path: Path, diagram: str | list[str]) -> str:
    """Return ``doc_path`` content with the pr-diagram block written/refreshed.

    ``diagram`` is one diagram or several — the doc mirrors the same set the PR
    body carries. Idempotent: replaces an existing delimited block, else appends
    a new ``## Architecture (current -> future)`` section.
    """
    diagrams = [diagram] if isinstance(diagram, str) else list(diagram)
    fences = "\n\n".join(f"```\n{d.rstrip()}\n```" for d in diagrams)
    block = (
        f"{_DOC_BLOCK_START}\n"
        "## Architecture (current -> future)\n\n"
        f"{fences}\n"
        f"{_DOC_BLOCK_END}"
    )
    text = doc_path.read_text(encoding="utf-8") if doc_path.exists() else ""
    pattern = re.compile(
        re.escape(_DOC_BLOCK_START) + r".*?" + re.escape(_DOC_BLOCK_END),
        re.DOTALL,
    )
    if pattern.search(text):
        return pattern.sub(block, text)
    sep = "" if text.endswith("\n\n") or text == "" else ("\n" if text.endswith("\n") else "\n\n")
    return f"{text}{sep}{block}\n"


def cmd_sync(args: argparse.Namespace) -> int:
    doc_path = Path(args.doc_target)
    diagrams: list[str] = []
    for name in args.diagram_file:
        src = Path(name)
        if not src.exists():
            print(f"ERROR: --diagram-file not found: {src}", file=sys.stderr)
            return 2
        text = src.read_text(encoding="utf-8")
        if "<FILL:" in text:
            print(f"ERROR: {src} still has <FILL: ...> slots — fill it first.", file=sys.stderr)
            return 2
        diagrams.append(text)
    new_text = sync_doc(doc_path, diagrams)
    doc_path.parent.mkdir(parents=True, exist_ok=True)
    doc_path.write_text(new_text, encoding="utf-8")
    print(f"synced {len(diagrams)} diagram(s) into {doc_path}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="cmd", required=True)

    c = sub.add_parser("classify", help="Print every required archetype + skeleton.")
    c.add_argument("--base", default="main", help="Base ref to diff against (default: main).")
    c.add_argument("--repo-dir", default=".", help="Repo working dir (default: .).")
    c.add_argument("--ticket", default=None, help="RULE-050 ticket (e.g. INFRA) for a stronger signal.")
    c.set_defaults(func=cmd_classify)

    v = sub.add_parser("verify", help="Check a PR body carries every required diagram.")
    v.add_argument("--body-file", required=True, help="PR body file, or '-' for stdin.")
    v.add_argument("--base", default="main", help="Base ref to diff against (default: main).")
    v.add_argument("--repo-dir", default=".", help="Repo working dir (default: .).")
    v.add_argument("--ticket", default=None, help="RULE-050 ticket (e.g. INFRA).")
    v.set_defaults(func=cmd_verify)

    s = sub.add_parser("sync", help="Write/refresh the diagram block(s) into a doc.")
    s.add_argument("--doc-target", required=True, help="Path to the doc to sync into.")
    s.add_argument(
        "--diagram-file",
        required=True,
        nargs="+",
        help="File(s) holding the filled §1 diagram(s) — pass one per archetype.",
    )
    s.set_defaults(func=cmd_sync)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
