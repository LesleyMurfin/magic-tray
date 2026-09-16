"""JSON config loading with built-in defaults."""

import json


def load_config(path: str) -> dict:
    """Read a JSON config file, returning defaults when anything goes wrong."""
    config = {"retries": 3, "timeout": 30}
    try:
        with open(path, encoding="utf-8") as handle:
            config.update(json.load(handle))
    except Exception:
        pass
    return config
