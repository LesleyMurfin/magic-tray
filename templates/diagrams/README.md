# PR-diagram archetypes (#75)

ASCII skeletons the PR-diagram harness emits per change class (see
`design/pr-diagram-harness.md`). `scripts/pr_diagram.py classify` selects **every**
archetype the changed paths trigger — not one — and prints a ready-to-paste stanza
for each; the authoring agent fills every `<FILL: ...>` slot with the real
current→future from the diff.

| Archetype | Role | Selected when the change touches |
|---|---|---|
| `enforcement-flow` | process | `.ai/hooks/**`, `.ai/guardrails/**`, `.ai/validators/**` |
| `automation-pipeline` | process | `scripts/**`, `.forgejo/**` |
| `deploy-topology` | structure | `mops/**`, or an INFRA ticket |
| `config-before-after` | state | config / `settings*.json` / `.ai/claude-config/**` |
| `skill-flow` | process | `.ai/skills/**` |
| `generic` | structure | anything else with runtime surface |
| `none` | — | docs-only / tests-only (no archetype applies) |

Selection is **additive**: a PR touching `.ai/hooks/`, `scripts/`, and `mops/`
requires three diagrams. `plan()` returns them in table order (highest signal
first); `classify()` returns just the primary. Roles say what a reader is looking
at — a flow (`process`), a shape (`structure`), or a delta (`state`).

## Adding an archetype

1. Add `<key>.txt` here. It MUST contain at least one `<FILL: ...>` slot and a
   CURRENT/FUTURE (or BEFORE/AFTER) split — `test_every_archetype_loads_filled_shape`
   enforces both.
2. Add the key to `ARCHETYPES` (in signal order) and `ROLES` in
   `scripts/pr_diagram.py`.
3. Add its trigger to `plan()`, plus a test in `scripts/tests/test_pr_diagram.py`.
4. Add a row to the table above and to
   `.ai/skills/_core/productivity/peer-review/references/pr-body-standard.md`.

## Gates

- `.ai/validators/pr-body-check.py` — body-only: rejects a §1 that still contains a
  `<FILL:` token (anti-stub) or lacks a current/future (or before/after) split.
- `scripts/pr_diagram.py verify` — diff-aware: rejects a body missing any archetype
  the change requires. Wired into `scripts/github.py create-pr`, fail-closed.
