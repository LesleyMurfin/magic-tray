"""Control fixture: parameterized SQL, no injection risk.

No planted bug. CodeRabbit flagging anything here counts as a false positive.
"""

import sqlite3


def find_user(conn: sqlite3.Connection, username: str):
    """Look up a single user row by name using a bound parameter."""
    cursor = conn.cursor()
    cursor.execute(
        "SELECT id, email FROM users WHERE username = ?",
        (username,),
    )
    return cursor.fetchone()


def search_users(conn: sqlite3.Connection, term: str):
    """Return every user whose email matches a search term."""
    cursor = conn.cursor()
    cursor.execute(
        "SELECT id, email FROM users WHERE email LIKE ?",
        (f"%{term}%",),
    )
    return cursor.fetchall()
