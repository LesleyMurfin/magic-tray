"""Eval fixture: hardcoded credential in source.

THE SECRET BELOW IS FAKE. It is a non-functional placeholder string with an
obvious ``sk-fake-`` prefix, invented solely so the CodeRabbit eval harness has
a secret-detection target. It is not a credential, has never been issued by any
vendor, and authenticates against nothing.
"""

import urllib.request

# GOLD-BUG: hardcoded_secret
# FAKE, NON-FUNCTIONAL PLACEHOLDER -- never a real credential.
API_KEY = "sk-fake-1234567890abcdef"

BASE_URL = "https://api.example.invalid/v1"


def fetch_account(account_id: str) -> bytes:
    """Fetch an account record using the module-level API key."""
    request = urllib.request.Request(
        f"{BASE_URL}/accounts/{account_id}",
        headers={"Authorization": f"Bearer {API_KEY}"},
    )
    with urllib.request.urlopen(request) as response:
        return response.read()
