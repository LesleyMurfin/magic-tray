# Test Plan: Tier A, M6, M7 Scope
## Overview
This test plan focuses on the recent logic improvements introduced in the `mmt-overnight-quickwins-logic` branch, specifically targeting:
1. **Tier A**: Logic bounds for low and critical battery alerts (C3/C4).
2. **M6**: Multi-device UX and device-type-aware toast titles.
3. **M7**: Polling cadence tightening at the user threshold (C2).
4. **B5**: GitHub Actions workflow for PRs and main.

## 1. Automated Unit Tests (L1)
These tests validate pure logic and boundary conditions without requiring real HID hardware.

### 1.1 Polling Cadence (C2) - `DrainRateTracker`
*   **TP-C2-01**: Verify `GetNextInterval(pct)` returns `CeilingNormal` (24h) when `pct >= userThreshold + margin`.
*   **TP-C2-02**: Verify transition from 24h to exponential/rate-based intervals when `pct < userThreshold`.
*   **TP-C2-03**: Verify rate clamping to the correct floors (5m for v3, 30m for non-v3).

### 1.2 Battery Alerts (C3/C4) - `TrayApp` Logic
*   **TP-C3-01**: Verify toast alerts fire exactly when `pct <= boundary` (not off-by-one).
*   **TP-C3-02**: Verify cascade behavior: if battery drops from 15% to 8% (with 10% threshold), both threshold and critical alerts fire (or lowest crossed).
*   **TP-C4-01**: Verify persistent critical alert fires correctly at the low band (e.g., `0 <= pct <= CriticalPct`) instead of exactly `== 1`.

### 1.3 Multi-Device UX & Toasts (M6) - `ToastNotifier` & `TrayApp`
*   **TP-M6-01**: Verify `ToastNotifier.Show` title dynamically uses the device name (e.g., "Magic Keyboard Battery Low") instead of hardcoded "Magic Mouse".
*   **TP-M6-02**: Verify device names are XML-escaped before interpolation into toast XML to prevent crashes on malicious/special characters.

## 2. Integration / Hardware Tests (Manual)
*   **IM-M6-01**: Connect a Mouse and a Keyboard simultaneously. Verify both appear as distinct items in the Tray menu with independent capability matrices.
*   **IM-C2-01**: Set threshold to 25%. Monitor logs to ensure polling cadence increases once battery drops to 24%.

## 3. CI/CD (B5)
*   **CI-B5-01**: Open a PR to `main` and verify the `.github/workflows/ci.yml` pipeline triggers, runs `dotnet build` and `dotnet test` on a `windows-latest` runner, and blocks merge on failure.
