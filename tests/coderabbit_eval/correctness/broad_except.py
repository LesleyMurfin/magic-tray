"""Eval fixture: broad exception handler that silently swallows a failure.

Intentionally buggy. This file exists only as ground truth for the CodeRabbit
eval harness and is never imported by production code.
"""

import json


def load_config(path: str) -> dict:
    """Read a JSON config file, returning defaults when anything goes wrong."""
    config = {"retries": 3, "timeout": 30}
    try:
        with open(path, encoding="utf-8") as handle:
            config.update(json.load(handle))
    # GOLD-BUG: broad_except
    except Exception:  # noqa: BLE001, S110 — GOLD-BUG fixture: bug is the point
        pass
    return config
