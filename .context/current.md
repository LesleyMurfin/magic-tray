<!-- State now. Outranks history.md and decisions.md; AGENTS.md and Lesley outrank it. Keep it short. No transcripts or secrets. -->

# Current

**Goal:** Free MIT Windows 10/11 tray app (product Magic Tray, exe `MagicMouseTray.exe`) for Magic Mouse, Magic Keyboard, and Magic Trackpad battery percent, plus user-confirmed scroll-driver helpers and the keyboard SDP battery unlock (`README.md`, `AGENTS.md`).

**State:** PR #174 on `ai/dot-context-seed-2026-10-18-dco` seeds `.context/`, points `AGENTS.md` at it, and drops Lesley as a CODEOWNERS reviewer. `main` is still `99511d2`. Shipped release is **v1.1.0**. `MagicMouseTray/MagicMouseTray.csproj` is **1.1.1**, unreleased. The 0323 KMDF driver is not vendored (`README.md`).

**Open work:** #174 is ready for review with no assignees. Other open PRs: #169 keyboard SDP unlock, #168 Magic Mouse v2 battery, #154 draft v3 filter defects, #153 battery interfaces, #151 drivers hub design, #150 site FAQ stepper.

**Next:** The owning bot merges #174 once required CI is green and the PR is mergeable. Lesley is not asked, assigned, or requested to review or merge.
