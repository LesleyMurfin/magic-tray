---
title: "PRD-0106 ACO Maintainer Kit — Test Matrix"
type: design-note
prd_id: PRD-0106
version: 1.0.0
created: 2026-10-13
updated: 2026-10-13
status: active
---

# ACO Maintainer Kit — Test Matrix

Runnable gates for code development against the zero-daemon graph + optional Jev kit.
**Default posture:** offline-first. Live Jev/XERJ is never required to pass.

## Baselines (comparison targets)

| Suite | Baseline source | Expected |
|-------|-----------------|----------|
| Primary graph (23) | Issue #68 / prior green | 23 passed, exit 0 |
| Regression pack (~200) | Issue #68 | ~200 passed, exit 0 |
| Kit smoke | kit ship evidence | 4/4 OK |
| Kit verify | measured in this repo | `--offline-only`: passed=24 failed=0, RESULT: PASS |
| jev-probe self-test | kit ship evidence | all passed |
| blast-radius `scripts/pr_diagram.py` | measured, graph present | exit 0, `symbol_count` 19, &lt; 1.0s |
| graph.db present | optional, never committed | 204 KB / 95 symbols over 11 indexable files; the 69 `.cs` files are not indexed |

The first two rows are the engine's own baselines and only apply in the `agents` checkout
(Layer B). The graph rows are **conditional**: magic-tray publishes no `.aco/graph.db`.

## Layer A — Kit / graceful-degrade

**Workspace:** kit target checkout  
**When:** any change under `.ai/skills/aco-triage/**` or `.ai/hooks/pre/jev-probe*`

```bash
cd <kit-target-root>
python3 .ai/skills/aco-triage/tests/test_graceful_degrade_smoke.py
JEV_DISABLED=1 python3 .ai/skills/aco-triage/scripts/verify_graceful_degrade.py --offline-only
python3 .ai/skills/aco-triage/scripts/verify_graceful_degrade.py --offline-only
node --experimental-strip-types --no-warnings .ai/hooks/pre/jev-probe.self-test.ts
```

`jev-probe.self-test.ts` needs **Node ≥ 22.6** for `--experimental-strip-types`; on older
Node it exits non-zero for that reason alone. Use `--offline-only` on the verifier to skip
the probe self-test, and treat A4 as SKIP where Node is older.

| ID | Command | Pass criteria |
|----|---------|---------------|
| A1 | smoke | 4 passed, exit 0 |
| A2 | verify `--offline-only` + `JEV_DISABLED=1` | passed=24 failed=0, exit 0 |
| A3 | verify `--offline-only` | passed=24 failed=0, RESULT: PASS |
| A4 | jev-probe.self-test.ts | all assertions pass, exit 0 (**SKIP** on Node &lt; 22.6) |

## Layer B — Structural engine (`riley_kg`)

**Workspace:** agents tree with `PYTHONPATH=.`  
**When:** any change under `riley_kg/`

```bash
cd <agents-root>
export PYTHONPATH=.
python3 -m pytest tests/test_init_sync_cli.py tests/test_sqlite_graph_engine.py tests/test_sqlite_store.py -q
python3 -m pytest tests/riley_kg/ -q
# optional broader pack
python3 -m pytest tests/test_aicostoptimizer_status.py tests/test_token_guard.py \
  tests/test_openrouter_benchmark_sync.py tests/test_lifecycle_reaper.py \
  tests/test_eve_eval.py tests/test_automations_e2e.py tests/test_fusion_harness.py -q
```

| ID | Suite | Pass criteria |
|----|-------|---------------|
| B1 | Primary graph 3 files | 23 passed (baseline #68) |
| B2 | `tests/riley_kg/` | all passed (record count) |
| B3 | Regression pack | ~200 passed (baseline #68) |

## Layer C — Product smoke on real `.aco/graph.db` (conditional)

**When:** only when a maintainer has built `.aco/graph.db` for this checkout **and**
`riley-kg` is installed. magic-tray ships no graph, so this layer is **SKIPPED by
default** and its absence never fails the gate.

`riley-kg` is the console script from `LesleyMurfin/agents@agents-sqlite-graph`
(`[project.scripts] riley-kg = "riley_kg.cli:main"`). There is no `python3 -m riley_kg.cli`
entry point — do not document one.

```bash
cd <repo-root>
command -v riley-kg >/dev/null && test -f .aco/graph.db \
  || echo "SKIP Layer C (no riley-kg on PATH and/or no .aco/graph.db)"
riley-kg --help
riley-kg blast-radius --db .aco/graph.db scripts/pr_diagram.py
riley-kg blast-radius --db .aco/graph.db docs/star.js
riley-kg callers --db .aco/graph.db _is_doc
python3 -c "import sqlite3;c=sqlite3.connect('.aco/graph.db');\
print(c.execute('SELECT COUNT(*) FROM kg_nodes').fetchone(),\
c.execute('SELECT edge_type,COUNT(*) FROM kg_edges GROUP BY 1').fetchall())"
du -h .aco/graph.db
```

`--db` has **no default** in this build — omit it and the command exits 2. Pass
`--db .aco/graph.db` on every invocation.

A graph is built with
`riley-kg extract-symbols --path . --repo magic-tray --sqlite-db .aco/graph.db`.

**Measured coverage (do not overstate it).** That parser indexes `.py`, `.ts`, `.js` and
`.rs` only. Extracting this repo returns `{"files": 11, "symbols": 95}` — the eight `.py`,
one `.js` and two `.ts` files, with 95 `defined-in` and 110 `calls` edges, all intra-file.
**All 69 `.cs` files are invisible**: `blast-radius … MagicMouseTray/DriverInstaller.cs`
exits 0 with `"symbol_count": 0`. The graph covers the Python/JS tooling and nothing of the
tray app, so C# triage is an `rg` + `git` job. Targets above are chosen accordingly.

| ID | Check | Pass criteria |
|----|-------|---------------|
| C0 | preconditions | `riley-kg` on PATH **and** `.aco/graph.db` present; else **SKIP** |
| C1 | CLI `--help` / subcommand help | exit 0 |
| C2 | blast-radius `scripts/pr_diagram.py` | exit 0, `symbol_count` 19, wall &lt; 1.0s |
| C3 | blast-radius `docs/star.js` | exit 0, `symbol_count` 7, wall &lt; 1.0s |
| C4 | callers `_is_doc` | exit 0 **and non-empty** (caller `plan`). Exit 0 alone proves nothing: an unknown symbol also returns `[]` with exit 0 |
| C5 | counts | 95 nodes; `calls` 110 / `defined-in` 95 at the sizes above |
| C6 | db size | well under 1 MB for 11 files; **never commit** |
| C7 | C# is out of graph scope | `blast-radius … DriverInstaller.cs` ⇒ `symbol_count: 0`; fall back to `rg` |

## Layer D — CI publisher static (not applicable here)

magic-tray has **no** `.github/workflows/aco-index-publish.yml`. The workflows present are
`actionlint, ci, codeql, dco, indexnow, no-draft-prs, packaging, ps-lint, release, site,
winget-submit`. This repo publishes no graph artifact, so Layer D is **SKIPPED**.

Run it only in a checkout that actually ships the publisher:

```bash
test -f .github/workflows/aco-index-publish.yml \
  && python3 -c "import yaml; yaml.safe_load(open('.github/workflows/aco-index-publish.yml'))" \
  || echo "SKIP Layer D (no aco-index-publish.yml in this repo)"
```

| ID | Check | Pass criteria |
|----|-------|---------------|
| D0 | publisher workflow present? | **No** in magic-tray → D1/D2 **SKIP** |
| D1 | workflow YAML parse | no exception (only where the file exists) |
| D2 | (optional) Actions dispatch | artifact + manifest sha256 match |

## Fast pre-merge gate (dev)

In **this repo** the merge bar is Layer A only: **A1 + A2 + A3** (plus A4 on Node ≥ 22.6).
B and C need the `agents` checkout / a built graph and are SKIP here.
Where both exist, the bar is **A1+A2+A4 + B1 + C2**.

## Out of scope for this matrix

- Full Electron / Playwright e2e
- Live Jev/XERJ as required dependency
- Committing `graph.db` into git

## Results log

Machine-written comparison runs are recorded in
[`TEST-RESULTS.md`](./TEST-RESULTS.md) next to this file.
