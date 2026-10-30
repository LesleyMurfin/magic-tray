# AGENTS.md — Magic Tray
Read .context/ first.

- Code graph (optional, fail-open): when `.aco/graph.db` and `riley-kg` are both present and the query returns, `riley-kg blast-radius --db .aco/graph.db <file>` / `riley-kg callers --db .aco/graph.db <symbol>` (from LesleyMurfin/agents@agents-sqlite-graph); otherwise use `rg`/`git`. Never block on the graph.
- Cost: run `aicostoptimizer status` before long or expensive agent runs when it is installed; exit≠0 means quota fail-closed, so stop and report.
- Build: `dotnet build MagicMouseTray/MagicMouseTray.csproj -c Release`
- Test: `dotnet test MagicMouseTray.Tests/MagicMouseTray.Tests.csproj -c Release`
- Lint: `Invoke-ScriptAnalyzer -Path . -Recurse -Settings ./PSScriptAnalyzerSettings.psd1 -Severity Error,Warning`

## Repo identity

**Magic Tray** (`LesleyMurfin/magic-tray`) is a free MIT-licensed **Windows 10/11 system-tray utility** (C# / .NET 8 / WPF) for Apple Magic Mouse, Magic Keyboard, and Magic Trackpad.

- Product name in Windows: **Magic Tray**; published exe: `MagicMouseTray.exe`
- Primary jobs: battery percent in the tray, user-initiated scroll-driver install helpers, keyboard SDP battery unlock
- Companion driver repo (not vendored here): [`LesleyMurfin/magic-mouse-v3-windows-fix`](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix)
- Site / manual: [magictray.app](https://magictray.app/)
- Agent-oriented product notes: [`llms.txt`](llms.txt)

| Path | Role |
|------|------|
| `.context/` | Agent context pack read first: current, decisions, history, map |
| `MagicMouseTray/` | WPF tray app (C# / .NET 8) |
| `MagicMouseTray.Tests/` | Unit tests |
| `docs/` | Site pages + operator docs |
| `scripts/` | Packaging, release verify, keyboard SDP patch |
| `.aco/` | ACO maintainer kit (prompt and cost note; never commit `graph.db`) |
| `.ai/skills/aco-triage/` | Portable ACO triage skill + graceful-degrade verifier |

<!-- BEGIN CORE-RULES -->
## Required charter — CORE-RULES

The charter text in this repo is the inlined block below. `.ai/CORE-RULES.md` is not in this checkout.

### RULE #13 — ADHD output (binding, always on)

**One issue at a time. Action over documents. No drafts. Open ready-for-review PR, then STOP. Lesley is never asked, assigned or requested to merge or review. The owning bot merges its own PR once verified ready (required CI green, red-then-green tests, agent-checked preview where one applies, mergeable/clean, repo land rules). Merges never depend on bot review stamps.**

1. Lead with the action — command, path, or diff on line one. Never context first.
2. Finish the current issue before naming a second; extra findings go in one line at the end.
3. Restate state each turn ("Step 3 of 5 done: … Next: …"); end with exactly one next step.
4. No PRDs/specs/ADRs/issues unless that document was asked for. Fix the thing.
5. Cap tasks shown to Lesley at 5; complete all tasks and improvements. Suggestions: 2 rounds of reiteration with experts max, cheap/fast first, skip if none — do not dump the rounds on her. No preamble, recap, or closers. Diagrams over prose.

Never introduce vocabulary the owner has not used. Honour standing approval — do not re-gate
work she already approved. Off only on `stop adhd mode` / `normal mode`.

Full rule: `rule://adhd-always` · `skill://i-have-adhd` · charter RULE #13.

### PR Triage & Conditions Closure Rule (binding)

Under RULE #13 / Repo Land Rules, when triaging, reviewing, or driving pull requests:

1. **Audit all three PR surfaces:**
   - **Inline code review threads** (review discussions, code comment threads)
   - **Top-level PR comments** (general issue comments, bot/reviewer summaries)
   - **PR body description checklist** (issue description tasklists and checklists)
2. **Zero unattended triage conditions:** Never leave an open checkbox (`- [ ]`), unattended comment feedback, or pending condition verdict. Every item must be actively addressed.
3. **Execute or issue immediately:**
   - If within current scope: implement and verify the fix directly within the PR branch.
   - If deferred or out of scope: immediately open a follow-up GitHub issue (`gh issue create --title "..." --body "..."`) and link the new issue URL as explicit rationale.
4. **Update checkboxes to resolved (`- [x]`):**
   - Check off every checklist item (`- [x]`) in PR comments and the PR body description, citing resolution evidence (commit SHA or created follow-up issue URL).
   - Ensure no PR or workspace run completes or is presented with unchecked boxes (`- [ ]`), unresolved condition comments, or unassigned follow-ups.
5. **Autonomous merge execution:**
   - Per RULE #13, once all checklist items/triage conditions are resolved (`- [x]`), required CI is green, tests pass (red-then-green where applicable), and the PR is mergeable/clean, the owning bot executes the merge autonomously without stopping to ask Lesley or waiting on bot review stamps.

### Never a draft. Never someone else's repo.

- **No draft PRs, ever.** If it needs approval, open it **ready-for-review**. A draft never gets reviewed.
- **Never open a PR into a repo outside this one without explicit owner permission.**
  Prepare the branch and the body, then ask.

| Decade | Rules (summary) |
|--------|------------------|
| Truth | #1–4 evidence, no guessing |
| Judgment | #10–13 assumptions, pushback, **ADHD output (see above)** |
| Change | #20–22 design, MOP, skills |
| Craft | #30–32 simple, surgical, deliberate infra |
| Docs | #40–43 pointers, chunk reads, **current state outranks history** |
<!-- END CORE-RULES -->

## Build & test (verification)

Requires the **.NET 8 SDK on Windows** (CI is Windows-only for the app binary).

```powershell
dotnet build MagicMouseTray/MagicMouseTray.csproj -c Release
dotnet test MagicMouseTray.Tests/MagicMouseTray.Tests.csproj -c Release
dotnet publish MagicMouseTray/MagicMouseTray.csproj -c Release
# Output: bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\MagicMouseTray.exe
```

Also: PowerShell lint via `PSScriptAnalyzerSettings.psd1`; packaging/workflow notes in [`.github/workflows/README.md`](.github/workflows/README.md).

Do **not** vendor or rebuild the KMDF driver here — that lives in `magic-mouse-v3-windows-fix`.

## Hard product constraints

- **0323 battery** is HID Input report `0x90` on COL02 only. Never Feature `0x47`, never WMI as a mouse battery source.
- Tray never runs `bcdedit`, never silently enables Test Mode / disables HVCI.
- Driver install paths are user-confirmed only; missing KMDF package → unavailable, never fall back to patching `applewirelessmouse.sys`.
- Device catalog is code (`MouseBatteryDevice.KnownMice`, `KeyboardBatteryDevice.KnownKeyboards`), not README prose.

## Code graph and cost (binding)

1. **Prompt:** Reference `.aco/ACO-TRIAGE-PROMPT.txt` for structural triage.
2. **Cost:** `aicostoptimizer status` before a long run when that command is installed. Non-zero exit is quota fail-closed: stop and report.
3. **Graph:** When `.aco/graph.db` and `riley-kg` are both present and the query returns, `riley-kg blast-radius --db .aco/graph.db <file>` and `riley-kg callers --db .aco/graph.db <symbol>`. If the file, the CLI, or the query is missing, empty, or stalled, use `rg` and `git`. Never block on the graph.
4. **Output Contract:** Every PR review ends with ```json aco-triage-run```.
5. **Clean repo:** Never commit `.aco/graph.db`.

### Pre-flight

```bash
python3 .ai/skills/aco-triage/scripts/verify_graceful_degrade.py --offline-only
```

Expect PASS. `--offline-only` skips the optional TypeScript probe self-test, which needs Node 22.6+; drop the flag to exercise it. Non-zero → fix the verifier/script first; still do not block review on a missing graph.

### Blast-radius targets

Typical targets: `MagicMouseTray/` (`DriverInstaller.cs`, battery readers, device catalog).

When the graph is unavailable, say so in the run footer rather than omitting it: `graph.present=false`
(or `true` with `node_count=0`), `tooling.riley_kg_ok=false`, and `blast_radius[].method="rg"`.

### Zero daemons

- No localhost graph/search daemons.
- **Never commit** `.aco/graph.db` (blocked by the ACO gitignore fragment).

### Output contract (every PR review)

Every ACO triage / PR review **MUST** end with exactly one full machine footer:

````markdown
```json aco-triage-run
{
  "schema_version": "aco-triage-run/v1",
  "run_id": "<slug-date>",
  "timestamp": "<ISO-Z>",
  "workspace_root": "<pwd>",
  "branch": "<branch>",
  "head_sha": "<sha>",
  "pr_ref": "LesleyMurfin/magic-tray#N",
  "graph": { "present": false, "db_path": null, "node_count": 0, "edge_count": 0, "scoped": true, "build_seconds": null },
  "tooling": { "riley_kg_ok": false, "jev_used": false, "pythonpath": null },
  "targets": [],
  "blast_radius": [],
  "findings": [],
  "verification": [{ "command": "dotnet test MagicMouseTray.Tests/MagicMouseTray.Tests.csproj -c Release", "exit_code": 0, "passed": true }],
  "diff_stats": null,
  "residual_risks": [],
  "prompt_version": "1.5.0-quick",
  "pr_comment_status": "skipped",
  "pr_comment_urls": []
}
```
````

Populate from the real run. Chat narrative alone is not enough for ingest. If a PR is in scope, put the P0/P1 checklist in the PR body. Bots do not post PR comments. Leave `pr_comment_status` as `skipped`.

### Skill pointer

- `.ai/skills/aco-triage/SKILL.md`
- `.ai/skills/aco-triage/scripts/verify_graceful_degrade.py`
- `.aco/README.md` (cost, graph, and smoke commands) · `.aco/jev.env.sample` (probe variables)

## PR hygiene

- Branch off `main`; open **ready-for-review** PRs only (never draft).
- Lesley is never asked, assigned or requested to merge or review. The owning bot merges its own PR once verified ready (required CI green, red-then-green tests, agent-checked preview where one applies, mergeable/clean, repo land rules). Merges never depend on bot review stamps.
- Follow the **PR Triage & Conditions Closure Rule**: audit all three PR surfaces (review threads, PR comments, body checklist), leave zero unattended `- [ ]` boxes, resolve inline or file immediate follow-up issues, update all boxes to `- [x]` with evidence, and merge autonomously when conditions and CI are satisfied.
- Prefer smallest safe diff; evidence for every caller / blast claim.
- Do not push directly to `main`.
- DCO: every commit must include `Signed-off-by:`.
