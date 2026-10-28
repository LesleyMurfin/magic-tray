<!-- ≤80 lines. One dated bullet per entry (≤~3 lines), tagged HIST-YYYY-MM-DD; roll old entries into one-line HIST-ROLLUP lines, never delete. Never load in full: rg -n "HIST-" history.md | rg -i <keyword>. Never overrides current.md, AGENTS.md or Lesley. No transcripts or secrets. -->

# History

- HIST-ROLLUP 2026-04-20..2026-04-24: `cb28715` Magic Mouse Battery Tray v1.0.0; `e046721` MIT LICENSE and CONTRIBUTING; PR #1 2024 scroll plus PID detection; PR #8 startup-repair; PR #9 driver priority.
- HIST-2026-04-27: `5001ab3` KMDF function-driver design confirmed (#11); `53f00b7` InputHandler BRB-level interception.
- HIST-ROLLUP 2026-05-06..2026-05-10: merges #27 path-a design, #30 multi-device detection, #32 v3 recycle manager, #34 keyboard battery monitor, #42 mouse protocol confirm, #43 path-a bundle, #44 pathA v5 SRE fixes.
- HIST-2026-05-18: `936c08c` merge PR #50 `ai/remove-docs-from-tracking`.
- HIST-2026-05-19: `f23f328` merge PR #51 `ai/magic-mouse-complete-fix`.
- HIST-2026-05-30: `1280f11` merge PR #53 salvage m4 v3 mouse findings.
- HIST-2026-06-27: `a96b01c` keyboard battery #54; `abf7ef8` keyboard battery #55; `33efb15` device expansion #57; `ad23136` driver matrix #58; `5f4f2a5` mac icon #56.
- HIST-2026-07-08: `0a5132f` merge PR #61 remove stale mm task runner.
- HIST-2026-07-29: `48fbdc7` merge PR #62 CI build fixes.
- HIST-2026-09-02: `f655203` ship Magic Tray 1.1.0 as a free Magic Utilities alternative (#75). Tag `v1.1.0`.
- HIST-2026-09-16: `0e72c8d` device-health #144; `97c050b` IndexNow parser #152; `824ae24` 0323 KMDF is not installable; `bbb7e81` and `60c5abd` redact Bluetooth addresses and profile names at the log sink (CWE-532).
- HIST-2026-09-23: `786c77c` bump actions/checkout from 4.4.0 to 7.0.1.
- HIST-2026-10-14: `b9b55e6` bump actions/upload-artifact from 7.0.1 to 7.0.2.
- HIST-2026-10-15: `356b8fa` bump github/codeql-action to v4.39.0.
- HIST-2026-10-16: `fbd47e3` add `AGENTS.md` and the ACO maintainer kit; `ac0dda8` wire AICostOptimizer Jev and purge Orca; `99511d2` bind riley_kg state schema to jev.
- HIST-2026-10-19: Seed `.context/` and the `AGENTS.md` pointer. #172 (`4b96781`) fails DCO because a blank line splits the trailer block; force-push is forbidden. Replacement is #174, and #172 was closed on 2026-10-28.
- HIST-2026-10-19: `1c9b8ce` merge PR #173 — `no-draft-prs` now fails draft pull requests.
- HIST-2026-10-28: `b94d2a1` bump the codeql-action group.
- HIST-2026-10-28: On #174, delete `.github/CODEOWNERS`, replace Lesley-only merge wording, and remove dead `aicostoptimizer jev`, C# walker, `riley_kg` init/sync, and `aco-index-publish.yml` instructions outside `.ai/skills/`. Fold `.aco/JEV.md` into `.aco/README.md`; bump the triage prompt to `1.5.0-quick`.
