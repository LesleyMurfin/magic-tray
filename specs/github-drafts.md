# Spec — Help → GitHub issue drafts

Status: Implemented. Built and reviewed on `feat/report-bug-snapshot` (PR #102). `docs/DESIGN-github-drafts.md` remains the contract; this spec records the shipped surface.

## Current State

On this branch the draft already exists:

- `MagicMouseTray/BugReport.cs` — `Redact`, `ReadLogTail` (40 lines), `Collect`, `FormatMarkdown`, `FormatFeatureMarkdown`, `IssueTitle` / `FeatureTitle`, `TroubleshootHint`, `IssueUrl` (`NewIssueBase` = `https://github.com/LesleyMurfin/magic-tray/issues/new`, `MaxUrlChars` = 7000). No GitHub HTTP client. No OpenAI. No Riley.
- `TrayMenu` in `MagicMouseTray/TrayApp.cs` — labels `Report a bug` / `Request a feature` plus confirm strings. Help submenu: How alerts work, Repository, then those two items.
- `TrayApp.OpenGitHubDraft(bool feature)` — MessageBox OK/Cancel → build markdown → `Clipboard.SetText` → `OpenHelpUrl(url)`. Catch → log `GITHUB_DRAFT_FAIL` and open `TrayMenu.IssuesUrl`.
- `MagicMouseTray.Tests/BugReportTests.cs` — redact, markdown, collect, title, Stock-0323 hint, feature markdown, URL labels, truncation, log tail. Does not open a browser or MessageBox.
- `TrayMenuTests.HelpUrls_AndLabels_AreExact` asserts the two labels.
- `.github/ISSUE_TEMPLATE/bug.md` and `feature.md` already point at the Help items.

Gaps vs design (fix after approve, do not expand scope):

- Hint tests cover Stock 0323 only; add PathA and NotBound.
- Do not add `template=` query params; keep `labels=bug` / `labels=enhancement`.
- Do not introduce GitHub API, tokens, or LLM.

## Summary

After approve: Help → **Report a bug** copies a MAC-redacted snapshot and opens a GitHub **draft** (`labels=bug`). Help → **Request a feature** copies version-only markdown and opens `labels=enhancement`. User submits in the browser while logged in. Local one-line `TroubleshootHint` for Stock 0323 / PathA / not-bound. Confirm dialog. Clipboard + browser.

## Files to Touch

| File | Change after approve |
| --- | --- |
| `MagicMouseTray/BugReport.cs` | Align with design (redact, snapshot, feature body, hint, URL cap). Stay WinForms-free. |
| `MagicMouseTray/TrayApp.cs` | Keep `TrayMenu` labels/confirm; `OpenGitHubDraft` = confirm → clipboard → `issues/new`. |
| `MagicMouseTray.Tests/BugReportTests.cs` | Cover redact, both drafts, all three hints, URL labels, truncation. No MessageBox / browser. |
| `MagicMouseTray.Tests/TrayMenuTests.cs` | Keep exact `Report a bug` / `Request a feature` strings. |

Do not edit README, CONTRIBUTING, ISSUE_TEMPLATE, pairing/driver code, or Diagnostics in this vertical unless a string already contradicts the design.

## Step-by-Step

1. Stop until `docs/DESIGN-github-drafts.md` is approved. Then that file wins over this branch’s C#.
2. Keep `BugReport` a pure helper. No `HttpClient`, no GitHub token, no OpenAI/Riley package, no `issues` POST.
3. Bug path: `Collect(_health, _deviceBatteries)` + `ReadLogTail(Logger.LogPath, 40)` + `FormatMarkdown` + `IssueTitle` + `IssueUrl(..., "bug")`.
4. Feature path: `FormatFeatureMarkdown` + `FeatureTitle` + `IssueUrl(..., "enhancement")`. No log tail, no device table.
5. Redact colon/dash MAC, `Dev_<12 hex>`, and bare 12-hex. Keep PIDs.
6. `TroubleshootHint`: Stock 0323 → KMDF; PathA → mutually exclusive; NotBound/NotInstalled → Driver radio. First match. Empty otherwise.
7. Confirm with `MessageBox` OK/Cancel using the existing `TrayMenu` copy. Cancel returns. After OK: clipboard then browser. Failure: issues list URL, no secret, no retry-as-API.
8. URL length ≤ 7000; truncated body notes that the clipboard has the rest.
9. Tests stay in `MagicMouseTray.Tests`; they construct strings and temp log files only.

## Verification

On Windows (this project is WindowsDesktop; do not run the suite on Linux):

```
dotnet test MagicMouseTray.Tests/MagicMouseTray.Tests.csproj -c Release
```

Must pass. Tests must not open a browser or `MessageBox`. Targeted filter if iterating:

```
dotnet test MagicMouseTray.Tests/MagicMouseTray.Tests.csproj -c Release --filter "FullyQualifiedName~BugReportTests|FullyQualifiedName~HelpUrls_AndLabels_AreExact"
```

Do not run formatters, linters, or a project-wide build beyond that test project unless the human asks.

## Notes for Next Agent

- Lane is engineer BUILD only after human gate. Do not commit/push from design.
- Design file is source of truth; if C# disagrees, change C#.
- No OpenAI key, no Riley HTTP, no GitHub PAT in the exe or tests.
- Do not auto-file issues. Do not pair devices or install drivers in this vertical.
- Product name in copy is **Magic Tray**. Repo is `LesleyMurfin/magic-tray`.
- After BUILD: Status on the design note can move to Implemented.
