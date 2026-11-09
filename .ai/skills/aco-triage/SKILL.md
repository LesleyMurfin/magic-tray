---
name: aco-triage
description: >-
  ACO Maintainer Kit triage skill (PRD-0106 M2). Prefer the zero-daemon local
  `.aco/graph.db` via the `riley-kg` CLI for blast-radius and callers. Optional
  Jev/XERJ semantic enrichment is fail-open and NEVER required for contributors.
  Use when triaging PRs, checking blast radius, finding callers, or routing
  structural vs semantic queries via AICostOptimizer.
version: 0.3.0
---

# /aco-triage — ACO Maintainer Kit (Zero-Daemon Baseline)

## Contract (non-negotiable)

| Rule | Binding |
|------|---------|
| **Baseline** | Local SQLite `.aco/graph.db` through the `riley-kg` console script, when the db and the CLI are both present. **No daemon required.** Either one missing ⇒ `rg` + `git`, never a block. |
| **Jev/XERJ** | **OPTIONAL** and **fail-open**. Contributors MUST complete triage without it. |
| **Opt-in** | Enrichment only when `JEV_URL` is set AND the pre-probe reports reachable. |
| **Disable** | `JEV_DISABLED=1` (or unset URLs) forces pure local path. |
| **No hardpaths** | Resolve DB relative to repo root (`.aco/graph.db`). No `/home/...` without `# hardpath-ok`. |
| **Secrets** | Do NOT push active Jev endpoints or maintainer secrets to public remotes. |

## Purpose

Give maintainers and agents a single triage path that:

1. Always answers **structural** questions (blast radius, callers, symbols) from the local graph.
2. **May** enrich with **semantic** memory via AICostOptimizer Jev when `JEV_URL` is set and up.
3. **Never** blocks, times out, or fails a session because Jev is down, firewalled, or absent.

## Routing

```
                    ┌─────────────────────────────┐
  triage request ─► │ jev-probe (<100ms, fail-open)│
                    └─────────────┬───────────────┘
                                  │
              ┌───────────────────┴───────────────────┐
              ▼                                       ▼
   structural query                          semantic query
   (always local)                            (optional only)
              │                                       │
              ▼                                       ▼
   riley-kg → .aco/graph.db              Jev HTTP at $JEV_URL
                                         (no CLI; no aicostoptimizer
                                         subcommand) only if JEV_URL
                                         set + reachable, else → local
```

- **Structural** (blast-radius, callers, symbol extract): `riley-kg` + `.aco/graph.db` whenever both are present and the query returns; if the file, the CLI, or the query is missing, empty, or stalled, use `rg` + `git` and label callers `method: "rg"`. Never block on the graph.
- **Semantic** (risk class, safety invariants, ADR score): try Jev **only** when:
  - `JEV_DISABLED` is unset/false, **and**
  - `JEV_URL` or `XERJ_URL` is set (opt-in), **and**
  - `.ai/hooks/pre/jev-probe.ts` last probe reported `reachable: true`.
- If any of those fail → continue on local graph and emit a one-line note:
  `ACO: Jev enrichment skipped (fail-open); using .aco/graph.db only`.

## AICostOptimizer — there is no Jev CLI

`aicostoptimizer --help` on the installed engine prints
`usage: aicostoptimizer [-h] {status} ...`. **`status` is the only subcommand.**

| Form | Actual result | Use it? |
|------|---------------|---------|
| `aicostoptimizer status` | exit 0 — daily provider quota gauge | **Yes**, before a long run; non-zero is quota fail-closed, stop and report |
| `aicostoptimizer jev choice` / `jev noul` / `jev score` | **exit 2**, `invalid choice: 'jev' (choose from 'status')` | **No** — no such command group |
| `python3 -m aicostoptimizer …` | **exit 1**, `No module named aicostoptimizer.__main__` | **No** — no module entry point |

So Jev is **HTTP-only**: reach it at `$JEV_URL` when the probe reports it up. Nothing in
this kit shells out to a `jev` subcommand, and no documented step may invent one.

## Environment

| Variable | Role | Default |
|----------|------|---------|
| `JEV_URL` | Optional Jev base URL — when set, routing is active | unset → no enrichment |
| `XERJ_URL` | Optional XERJ base URL (opt-in) | unset → no enrichment |
| `JEV_DISABLED` | Force offline / local-only | unset (fail-open) |
| `ACO_GRAPH_DB` | Override path to graph DB | `.aco/graph.db` |
| `PATH` | Must contain the `riley-kg` console script for graph queries | absent → graph skipped, `rg`/`git` path |

Probe timeout is hard-capped at **100ms** (see hook). Unreachable = skip, never retry-loop.

## Prerequisites

Both are **optional**. Without them triage still completes on `rg` + `git`.

1. Repo has `.aco/graph.db`. A maintainer builds it; this repo does not publish one.
2. `riley-kg` is on `PATH`. It is the console script declared by
   `LesleyMurfin/agents@agents-sqlite-graph`
   (`[project.scripts] riley-kg = "riley_kg.cli:main"`), e.g.:

```bash
pipx install 'git+ssh://git@github.com/LesleyMurfin/agents@agents-sqlite-graph'
riley-kg --help
```

There is **no** `python3 -m riley_kg.cli` entry point; the module form does not execute.
Build a graph with:

```bash
riley-kg extract-symbols --path . --repo magic-tray --sqlite-db .aco/graph.db
```

**Measured coverage.** That engine's parser indexes `.py`, `.ts`, `.js` and `.rs` only.
Extracting this repo yields `{"files": 11, "symbols": 95}` — the eight `.py`, one `.js`
and two `.ts` files. All 69 `.cs` files are invisible:
`blast-radius --db … MagicMouseTray/DriverInstaller.cs` exits 0 with `"symbol_count": 0`.
`calls` edges are intra-file only. The C# tray app is therefore an `rg` + `git` job, and a
graph here only covers the Python/JS tooling. Treat thin or empty results as
`method: "rg"`, never as a failure.

No Elasticsearch, no Java daemon, no process on `:9200` / `:8009` is required for baseline triage.

## SOP

### Step 0 — Help

If args are only `--help` / `help`, print this skill's Contract + Commands sections and **STOP**.

### Step 1 — Resolve graph DB (fail-open)

```bash
DB="${ACO_GRAPH_DB:-.aco/graph.db}"
if [ -f "$DB" ] && command -v riley-kg >/dev/null 2>&1; then
  GRAPH=1
else
  GRAPH=0
  echo "ACO: no usable graph ($DB / riley-kg) — structural answers come from rg + git"
fi
```

A missing graph or missing CLI is **not** a failure and **not** a reason to require Jev.
Record `graph.present=false`, `tooling.riley_kg_ok=false`, and label callers `method: "rg"`.

### Step 2 — Honor disable / probe (fail-open)

```bash
# Forced local-only
if [ "${JEV_DISABLED:-}" = "1" ]; then
  echo "ACO: JEV_DISABLED=1 — local .aco/graph.db only"
fi

# Optional pre-flight (never blocks). Prefer the TS hook in-session;
# for shell smoke, use the skill verifier:
python3 .ai/skills/aco-triage/scripts/verify_graceful_degrade.py --offline-only
```

In an OMP session the auto-discovered hook `.ai/hooks/pre/jev-probe.ts` runs at `session_start`, probes in **&lt;100ms**, and leaves `pi.acoJev` state. On any error it **fails open** (`reachable: false`).

### Step 3 — Structural triage (baseline)

When `GRAPH=1`, blast radius of a file (symbols + callers):

```bash
riley-kg blast-radius --db "${ACO_GRAPH_DB:-.aco/graph.db}" scripts/pr_diagram.py
```

Inbound callers of a symbol:

```bash
riley-kg callers --db "${ACO_GRAPH_DB:-.aco/graph.db}" _is_doc
```

`--db` has **no default** in this build: omit it and the command exits 2. `callers` returns
`[]` with **exit 0** for an unknown symbol, exactly as for a real symbol with no callers —
exit 0 is not proof the symbol exists. When `GRAPH=0`, or a query is empty or stalls,
switch to `rg` + `git` immediately and label callers `method: "rg"`. Never block on the graph.

Acceptance target (M1): blast-radius for a hot path returns in well under a second once
the DB exists; no network hop is involved.

### Step 4 — Optional semantic enrichment (never required)

Only when Step 2 reported reachable **and** the user/agent explicitly wants semantic
routing. Enrichment is an **HTTP call to `$JEV_URL`** — risk classification, safety
invariants, ADR scoring. There is no CLI for it; see the table above.

Rules:

- Timeout ≤ 100ms connect; overall request should stay short.
- Any failure → drop enrichment, keep structural answer, note skip.
- **Never** treat empty/unreachable Jev as triage failure.
- **Never** require contributors to set `JEV_URL` / `XERJ_URL`.

### Step 5 — Emit result

Always include:

1. Structural answer from `.aco/graph.db` (or explicit missing-DB error).
2. Enrichment status: `local-only` | `jev-enriched` | `jev-skipped-fail-open`.
3. DB path used and whether `JEV_DISABLED` was set.

## Commands (cheat sheet)

```bash
# Baseline — zero daemon; only when .aco/graph.db and riley-kg both exist
riley-kg blast-radius --db .aco/graph.db scripts/pr_diagram.py
riley-kg callers --db .aco/graph.db _is_doc

# Cost gauge — the only real aicostoptimizer subcommand
aicostoptimizer status

# Forced offline (acceptance: JEV_DISABLED=1 must still succeed)
JEV_DISABLED=1 python3 .ai/skills/aco-triage/scripts/verify_graceful_degrade.py --offline-only

# Optional probe only — needs Node >= 22.6; never fails the process on unreachable
node --experimental-strip-types --no-warnings .ai/hooks/pre/jev-probe.self-test.ts
```

## Verification

```bash
python3 .ai/skills/aco-triage/scripts/verify_graceful_degrade.py --offline-only
```

Drop `--offline-only` to also run the TypeScript probe self-test; that needs **Node ≥ 22.6**
and exits non-zero on older Node for that reason alone.

The verifier asserts:

- Offline path works with no network and with `JEV_DISABLED=1`.
- Optional Jev path is not required (unset URLs ⇒ local-only success).
- Probe helper is fail-open under absurd URLs / tiny timeouts.

## Non-goals

- Replacing `eve_smoke` or other memory indices.
- Shipping production XERJ endpoints in public remotes.
- Requiring full-repo AST rebuild on every contributor clone (CI artifact is preferred).

## Feedback

Bump `version` in frontmatter when changing routing or env contracts. Keep language explicit: **optional**, **fail-open**, **no daemon required**.
