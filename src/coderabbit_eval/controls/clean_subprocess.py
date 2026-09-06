"""Control fixture: subprocess with an argument list, no shell.

No planted bug. CodeRabbit flagging anything here counts as a false positive.
"""

import subprocess


def archive_directory(target_dir: str, archive_path: str) -> None:
    """Tar up a directory without invoking a shell."""
    subprocess.run(
        ["tar", "-czf", archive_path, "--", target_dir],
        check=True,
        capture_output=True,
        timeout=300,
    )


def ping_host(hostname: str) -> bool:
    """Ping a host once, returning whether it answered."""
    result = subprocess.run(
        ["ping", "-c", "1", "--", hostname],
        check=False,
        capture_output=True,
        timeout=10,
    )
    return result.returncode == 0
