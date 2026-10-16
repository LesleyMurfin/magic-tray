# AGENTS.md — Magic Tray

## Repo identity

**Magic Tray** (`LesleyMurfin/magic-tray`) is a free MIT-licensed **Windows 10/11 system-tray utility** (C# / .NET 8 / WPF) for Apple Magic Mouse, Magic Keyboard, and Magic Trackpad.

- Product name in Windows: **Magic Tray**; published exe: `MagicMouseTray.exe`
- Primary jobs: battery percent in the tray, user-initiated scroll-driver install helpers, keyboard SDP battery unlock
- Companion driver repo (not vendored here): [`LesleyMurfin/magic-mouse-v3-windows-fix`](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix)
- Site / manual: [magictray.app](https://magictray.app/)
- Agent-oriented product notes: [`llms.txt`](llms.txt)

| Path | Role |
|------|------|
| `MagicMouseTray/` | WPF tray app (C# / .NET 8) |
| `MagicMouseTray.Tests/` | Unit tests |
| `docs/` | Site pages + operator docs |
| `scripts/` | Packaging, release verify, keyboard SDP patch |
| `.aco/` | ACO maintainer kit (prompts, JEV sample; never commit `graph.db`) |
| `.ai/skills/aco-triage/` | Portable ACO triage skill + graceful-degrade verifier |

<!-- BEGIN CORE-RULES -->
## Required charter — CORE-RULES

**Every agent must follow** the revive_ai-dev agent charter: `.ai/CORE-RULES.md`
(SSOT: `/data/projects/revive_ai-dev/.ai/CORE-RULES.md`, vendored by `governance_sync.py`).

### RULE #13 — ADHD output (binding, always on)

**One issue at a time. Action over documents. No drafts. Open ready-for-review PR, then STOP — merge is Lesley-only (RULE #23 / 2026-09-12 freeze).**

1. Lead with the action — command, path, or diff on line one. Never context first.
2. Finish the current issue before naming a second; extra findings go in one line at the end.
3. Restate state each turn ("Step 3 of 5 done: … Next: …"); end with exactly one next step.
4. No PRDs/specs/ADRs/issues unless that document was asked for. Fix the thing.
5. Cap tasks shown to Lesley at 5; complete all tasks and improvements. Suggestions: 2 rounds of reiteration with experts max, cheap/fast first, skip if none — do not dump the rounds on her. No preamble, recap, or closers. Diagrams over prose.

Never introduce vocabulary the owner has not used. Honour standing approval — do not re-gate
work she already approved. Off only on `stop adhd mode` / `normal mode`.

Full rule: `rule://adhd-always` · `skill://i-have-adhd` · charter RULE #13.

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

## ACO Triage & Code Graph

Portable ACO maintainer kit under `.aco/` and `.ai/skills/aco-triage/`.

### Opening prompt (mandatory)

Every structural triage / PR-review session **MUST** start from:

```text
.aco/ACO-TRIAGE-PROMPT.txt
```

### Jev routing (optional, fail-open)

- **When `JEV_URL` is set:** use Jev semantic routing for triage enrichment.
- **When `JEV_URL` is unset or unreachable:** fail open immediately to local `.aco/graph.db` (if present) and `git`/`rg`. Never block review on Jev.

### Pre-flight

```bash
python3 .ai/skills/aco-triage/scripts/verify_graceful_degrade.py
```

Expect 25/25 PASS on the offline path. Non-zero → fix the verifier/script first; still do not block review on a missing graph.

### Blast-radius query (conditional, fail-open)

When **both** `.aco/graph.db` and the `riley_kg` CLI are present:

```bash
PYTHONPATH="${RILEY_KG_ROOT:-${ACO_RILEY_KG_ROOT:-}}" \
  python3 -m riley_kg.cli blast-radius --db .aco/graph.db <path-or-symbol>
```

Typical targets: `MagicMouseTray/` (`DriverInstaller.cs`, battery readers, device catalog).

If graph DB, CLI, or query is missing / times out / empty:

- Continue with `git` + `rg` (label callers `method: "rg"` / heuristic).
- Set `graph.present=false` (or `true` with `node_count=0`) in the run footer.
- **Never** block a PR review on graph availability.

### Zero daemons

- No localhost graph/search daemons (including ports commonly used for optional search backends).
- **Never commit** `.aco/graph.db` (blocked by the ACO gitignore fragment). Indexes are local-only.

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
  "prompt_version": "1.2.1-quick",
  "pr_comment_status": "skipped|posted",
  "pr_comment_urls": []
}
```
````

Populate from the real run. Chat narrative alone is not enough for ingest. If a PR is in scope, post a `gh pr comment` checklist (P0/P1) and record URLs in the footer.

### Skill pointer

- `.ai/skills/aco-triage/SKILL.md`
- `.ai/skills/aco-triage/scripts/verify_graceful_degrade.py`
- `.aco/README.md` · optional `.aco/JEV.md` + `.aco/jev.env.sample`

## PR hygiene

- Branch off `main`; open **ready-for-review** PRs only (never draft).
- Merge is **Lesley-only**.
- Prefer smallest safe diff; evidence for every caller / blast claim.
- Do not push directly to `main`.
- DCO: every commit must include `Signed-off-by:`.
