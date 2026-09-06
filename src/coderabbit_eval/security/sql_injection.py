"""Eval fixture: SQL injection via string concatenation.

Intentionally vulnerable. This file exists only as ground truth for the
CodeRabbit eval harness and is never imported by production code.
"""

import sqlite3


def find_user(conn: sqlite3.Connection, username: str):
    """Look up a single user row by name."""
    cursor = conn.cursor()
    # GOLD-BUG: sql_injection
    query = "SELECT id, email FROM users WHERE username = '" + username + "'"
    cursor.execute(query)
    return cursor.fetchone()


def search_users(conn: sqlite3.Connection, term: str):
    """Return every user whose email matches a search term."""
    cursor = conn.cursor()
    cursor.execute(f"SELECT id, email FROM users WHERE email LIKE '%{term}%'")
    return cursor.fetchall()
