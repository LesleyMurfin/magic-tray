"""Eval fixture: command injection via os.system with unsanitized input.

Intentionally vulnerable. This file exists only as ground truth for the
CodeRabbit eval harness and is never imported by production code.
"""

import os


def archive_directory(target_dir: str) -> int:
    """Tar up a user-supplied directory path."""
    # GOLD-BUG: command_injection
    return os.system("tar -czf backup.tar.gz " + target_dir)


def ping_host(hostname: str) -> int:
    """Ping a user-supplied hostname once."""
    return os.system(f"ping -c 1 {hostname}")
