# `.aco/` — ACO Maintainer Kit index (zero-daemon)

This directory holds the **local structural graph** used by the ACO Maintainer Kit
(PRD-0106). Contributors do **not** run Elasticsearch, Jev, XERJ, or any other
daemon to triage PRs.

| File | Tracked? | Purpose |
|------|----------|---------|
| `README.md` | yes | This file |
| `ACO-TRIAGE-PROMPT.txt` | yes | Binding triage prompt |
| `graph.db` | **no** | Optional SQLite symbol graph for `riley-kg` |
| `graph.db-wal` / `graph.db-shm` | **no** | SQLite sidecars if a graph file is opened |
| `graph.json` | **no** | Optional export / debug dump |
| `manifest.json` | **no** | Not published by this repo |

## Baseline contract

1. **Structural triage** reads `.aco/graph.db` when that file is present:
   `riley-kg blast-radius --db .aco/graph.db <file>` and
   `riley-kg callers --db .aco/graph.db <symbol>`. No network hop.
   If the file or the CLI is missing, use `rg` and `git`. Never block on the graph.
2. **Cost:** when `aicostoptimizer` is installed, run `aicostoptimizer status`
   before a long run. A non-zero exit is quota fail-closed: stop and report.
3. **Do not commit** `graph.db`.

## Graph file

This repo does not publish `graph.db`. If `.aco/graph.db` is absent, use `rg` and `git`.

## Smoke commands

```bash
python3 .ai/skills/aco-triage/scripts/verify_graceful_degrade.py
python3 .ai/skills/aco-triage/tests/test_graceful_degrade_smoke.py
riley-kg blast-radius --db .aco/graph.db MagicMouseTray/DriverInstaller.cs
riley-kg callers --db .aco/graph.db DriverInstaller
aicostoptimizer status
```

Related skill: `.ai/skills/aco-triage/`. Optional probe hook:
`.ai/hooks/pre/jev-probe.ts` (fail-open). Binding prompt:
`.aco/ACO-TRIAGE-PROMPT.txt`.
