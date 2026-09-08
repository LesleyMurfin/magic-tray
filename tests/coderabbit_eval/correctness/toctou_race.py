"""Eval fixture: TOCTOU race between an existence check and a write.

Intentionally buggy. This file exists only as ground truth for the CodeRabbit
eval harness and is never imported by production code.
"""

import os


def claim_lock(lock_path: str, owner: str) -> bool:
    """Create a lock file if nobody else holds it."""
    # GOLD-BUG: toctou_race
    if os.path.exists(lock_path):
        return False
    # Another process can create the lock between the check above and the
    # write below; nothing here is atomic.
    with open(lock_path, "w", encoding="utf-8") as handle:
        handle.write(owner)
    return True
