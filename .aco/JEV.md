# Cost and graph

Before a long or expensive agent run, when `aicostoptimizer` is installed:

```bash
aicostoptimizer status
```

A non-zero exit means quota fail-closed: stop and report.

When `.aco/graph.db` exists:

```bash
riley-kg blast-radius --db .aco/graph.db <file>
riley-kg callers --db .aco/graph.db <symbol>
```

Otherwise use `rg` and `git`. Never block on the graph.

The optional URL probe in `.ai/hooks/pre/jev-probe.ts` is fail-open. It is not the cost check and not the graph query. Sample variables for that probe are in `.aco/jev.env.sample`. Do not commit live secrets.
