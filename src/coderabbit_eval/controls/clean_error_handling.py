"""Control fixture: narrow exception handling with logging.

No planted bug. CodeRabbit flagging anything here counts as a false positive.
"""

import json
import logging

logger = logging.getLogger(__name__)


def load_config(path: str) -> dict:
    """Read a JSON config file, falling back to defaults on known failures."""
    config = {"retries": 3, "timeout": 30}
    try:
        with open(path, encoding="utf-8") as handle:
            config.update(json.load(handle))
    except FileNotFoundError:
        logger.info("Config %s not found; using defaults", path)
    except (OSError, json.JSONDecodeError):
        logger.warning("Config %s unreadable; using defaults", path, exc_info=True)
    return config
