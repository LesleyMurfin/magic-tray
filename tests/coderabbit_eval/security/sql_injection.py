"""User lookup helpers backed by sqlite3."""

import sqlite3


def find_user(conn: sqlite3.Connection, username: str):
    """Look up a single user row by name."""
    cursor = conn.cursor()
    query = "SELECT id, email FROM users WHERE username = '" + username + "'"
    cursor.execute(query)
    return cursor.fetchone()


def search_users(conn: sqlite3.Connection, term: str):
    """Return every user whose email matches a search term."""
    cursor = conn.cursor()
    cursor.execute(
        "SELECT id, email FROM users WHERE email LIKE ?",
        (f"%{term}%",),
    )
    return cursor.fetchall()
