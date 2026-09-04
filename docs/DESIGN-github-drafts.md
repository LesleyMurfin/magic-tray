# DESIGN — Help opens a GitHub issue draft

Status: Implemented on `feat/report-bug-snapshot` (PR #102, approved, CI green). This file stays the source of truth; if the C# disagrees with it, change the C#.

The tray does **not** call the GitHub API, OpenAI, or Riley. It copies markdown, opens `issues/new` in the browser, and the user submits while logged in.

## Menu

Tray → **Help/Documentation** →

```
How alerts work
Repository
Report a bug
Request a feature
```

| Item | What happens after Confirm |
| --- | --- |
| Report a bug | MAC-redacted snapshot → clipboard + `https://github.com/LesleyMurfin/magic-tray/issues/new?labels=bug&title=…&body=…` |
| Request a feature | Version (no snapshot) → clipboard + same host with `labels=enhancement` |

Both use an OK/Cancel MessageBox first. Cancel is a no-op. Clipboard holds the full markdown even if the URL is truncated.

Confirm copy:

- **Report a bug:** Magic Tray will collect version, driver badges, battery readings, and the last log lines (Bluetooth MAC redacted), copy them, and open a GitHub issue draft. You submit it while logged in.
- **Request a feature:** Magic Tray will open a GitHub feature-request draft with the app version. The text is also on the clipboard. You submit it while logged in.

On failure (clipboard / browser), open `https://github.com/LesleyMurfin/magic-tray/issues` and log `GITHUB_DRAFT_FAIL`. Do not retry with a token.

## Bug snapshot

`BugReport.FormatMarkdown` (pure string; no WinForms):

- **What happened** — `(type here)`
- **Environment** — Magic Tray version, `RuntimeInformation.OSDescription`, optional `0323 lasting choice` (`kmdf` / `pathA` / `stock`), one-line `TroubleshootHint` when it fires
- **Devices** — Name / PID / Driver / Battery. PID kept. No `BTHENUM` instance path.
- **Log** — last **40** lines of `%APPDATA%\MagicMouseTray\debug.log`, already redacted. Missing file → `(no debug.log)`.

Title: first device whose Driver is not `Ok`, `n/a`, or `PatchedKmdf` → `bug: PID {pid} {driver} (Magic Tray {version})`. Else first PID, else `bug: Magic Tray {version}`.

## Feature draft

No devices, no log, no hint, no redact pass required.

- **Idea** / **Why it helps** — `(type here)`
- **Environment** — Magic Tray version (and OS description). Not a diagnostic dump.

Title: `feat: Magic Tray {version}`. Label: `enhancement` only.

## Redact

`BugReport.Redact` on names, log tail, and the finished bug markdown. Keep Apple PIDs (`0323`, `030D`, …).

| Pattern | Replacement |
| --- | --- |
| `\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b` | `<mac>` |
| `\bDev_[0-9A-Fa-f]{12}\b` | `Dev_<mac>` |
| 12 hex chars not touching other hex | `<mac>` |

No other PII stripping this slice. Do not ship `debug.log` unredacted in the URL.

## TroubleshootHint (local, one line)

First match wins. Not a doctor, not a network call.

| When | Hint |
| --- | --- |
| PID `0323` and Driver contains `Stock` | `0323 is on stock Windows — pointer works; wheel needs KMDF (Test Mode + Memory Integrity off).` |
| Driver contains `PathA` (covers `PathAPatched`) | `Patched Apple: scroll and battery are mutually exclusive. KMDF is the path with both.` |
| Driver contains `NotBound` or `NotInstalled` | `Driver package is not bound. Use the tray Driver radio (confirm UAC).` |

Else empty. Do not hint on `Ok` / `PatchedKmdf`.

## URL

`https://github.com/LesleyMurfin/magic-tray/issues/new?labels={label}&title={title}&body={body}` (`Uri.EscapeDataString`). Cap **7000** chars; if over, shrink body and append `(truncated — full report is on the clipboard)`. Query is `labels=bug` / `labels=enhancement`, not `template=`.

## Files

| File | Role |
| --- | --- |
| `docs/DESIGN-github-drafts.md` | This design. |
| `specs/github-drafts.md` | SSSF for BUILD after approve. |
| `MagicMouseTray/BugReport.cs` | Redact, collect, markdown, title, hint, URL. No WinForms. No HTTP. |
| `MagicMouseTray/TrayApp.cs` (`TrayMenu` + `OpenGitHubDraft`) | Labels, confirm copy, clipboard, `OpenHelpUrl`. |
| `MagicMouseTray.Tests/BugReportTests.cs` | Redact / markdown / URL / hint. No browser, no MessageBox. |
| `MagicMouseTray.Tests/TrayMenuTests.cs` | Exact Help labels. |
| `.github/ISSUE_TEMPLATE/{bug,feature}.md` | Already on the branch; leave unless they drift from this menu. |

## Tests

WindowsDesktop, helpers only. No live HID. Tests do not call `OpenGitHubDraft`, `Clipboard.SetText`, `MessageBox.Show`, or `Process.Start`.

- Redact: colon-MAC, `Dev_AABB…`, bare 12-hex gone; PID `0323` kept.
- `FormatMarkdown` has version, PID, driver badge, lasting choice; no `BTHENUM` / raw MAC.
- `Collect` merges battery + health; name has no `Dev_` MAC.
- Title uses first non-Ok driver (`StockKmdf` over a prior `Ok` row).
- Hint: Stock `0323` mentions KMDF; PathA mentions mutually exclusive; NotBound mentions Driver radio.
- Feature markdown has Idea + version, no `debug.log`.
- `IssueUrl` default `labels=bug`; feature `labels=enhancement` and not `labels=bug`.
- Body in the URL is escaped; raw body text is not a substring. Huge body ≤ `MaxUrlChars` and contains `truncated`.
- `ReadLogTail` last N lines, redacted.
- Labels exactly `Report a bug`, `Request a feature`. No `PATH-A` in those labels.

## Non-goals

- Riley HTTP, OpenAI / any LLM rewrite, API tokens in the exe.
- GitHub Issues API, auto-filing, PAT, or submitting for the user.
- Shipping secrets, full unredacted `debug.log`, or Bluetooth MAC.
- Pairing, driver install/bind, PATH-A/KMDF work.
- Changing Classify, Diagnostics scripts, or ISSUE_TEMPLATE bodies unless they contradict this menu.
- Push.
