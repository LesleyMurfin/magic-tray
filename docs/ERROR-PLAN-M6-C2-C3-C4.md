# Error Plan: Tier A, M6, M7 Scope

## Overview
This error plan defines the expected behavior, recovery mechanisms, and log taxonomy for failures related to the `mmt-overnight-quickwins-logic` branch scope.

## 1. Multi-Device UX (M6)
**Scenario**: XML Injection in Toast Notifier
*   **Trigger**: A device name contains special characters (e.g., `<`, `>`, `&`) that break the Toast XML schema.
*   **App Behavior**: The `ToastNotifier` must XML-escape the device name before interpolation. If escaping fails, the app must fall back to a generic name (e.g., "Apple Device") rather than throwing an XML parsing exception that crashes the app.
*   **Logging**: Log a `WARN` event if invalid characters are detected, noting the fallback.

**Scenario**: Missing Capability Row Data
*   **Trigger**: The device capability matrix fails to populate due to an unhandled device type.
*   **App Behavior**: Display a gracefully degraded row indicating "Unsupported device capabilities". Do not throw null reference exceptions during menu generation.

## 2. Polling Cadence (C2)
**Scenario**: Negative or Invalid Drain Rate
*   **Trigger**: The battery percentage increases (charging event) or reads as invalid, resulting in a negative drain rate.
*   **App Behavior**: `DrainRateTracker` must reset the interval history or clamp the rate to a safe floor (e.g., 30m). It must not schedule negative intervals or throw `ArgumentOutOfRangeException`.
*   **Logging**: Log an `INFO` event noting "Charge detected, resetting drain history."

## 3. Battery Alerts (C3/C4)
**Scenario**: Double-Triggering of Critical Alerts
*   **Trigger**: Polling loop triggers a 1% critical alert reading multiple times.
*   **App Behavior**: Check for existing `_criticalAlert` presence before spawning a new window. Do not spawn duplicate windows. 
*   **Logging**: Deduplicate logs; only log `CRITICAL_ALERT_SHOWN` on the first spawn.

**Scenario**: Rapid Flapping at Threshold Boundary
*   **Trigger**: Battery fluctuates exactly at the alert threshold (e.g., 20% -> 19% -> 20%).
*   **App Behavior**: Ensure the re-arm logic only fires when `pct >= threshold`. If noise causes repeated drops, debounce the alert within a 5-minute window if possible.
