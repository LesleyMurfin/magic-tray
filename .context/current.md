<!-- State now. Outranks history.md and decisions.md; AGENTS.md and Lesley outrank it. Keep it short. No transcripts or secrets. -->

# Current

**Goal:** Free MIT Windows 10/11 tray app (product Magic Tray, exe `MagicMouseTray.exe`) for Magic Mouse, Magic Keyboard, and Magic Trackpad battery percent, plus user-confirmed scroll-driver helpers and the keyboard SDP battery unlock (`README.md`, `AGENTS.md`).

**State on main:** `99511d2` (2026-10-16) binds the riley_kg state schema to AICostOptimizer jev. Shipped release is **v1.1.0** (2026-09-02, tag `v1.1.0`). `MagicMouseTray/MagicMouseTray.csproj` is **1.1.1** / **1.1.1.0**, unreleased (`README.md` Releases). The 0323 KMDF package is not on `magic-mouse-v3-windows-fix` `main`, so the tray reports it missing and does not fall back (`README.md` Building from source).

**Open work:** Six open PRs. #169 keyboard SDP unlock idempotent. #168 Magic Mouse v2 (`0269`) battery reads. #154 draft, v3 filter defects. #153 keep every battery interface. #151 drivers hub design. #150 site FAQ accordion and stepper. Merge is Lesley-only (`AGENTS.md` PR hygiene).

**Next:** Lesley reviews those PRs. Do not vendor the KMDF driver in this repo.
