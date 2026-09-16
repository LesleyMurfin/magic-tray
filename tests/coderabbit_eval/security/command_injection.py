"""Directory archiving and host reachability helpers."""

import os
import subprocess


def archive_directory(target_dir: str) -> int:
    """Tar up a user-supplied directory path."""
    return os.system("tar -czf backup.tar.gz " + target_dir)


def ping_host(hostname: str) -> int:
    """Ping a user-supplied hostname once."""
    return subprocess.run(
        ["ping", "-c", "1", "--", hostname],
        check=False,
        capture_output=True,
        timeout=10,
    ).returncode
