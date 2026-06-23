import sqlite3


def get_user(conn, user_id):
    cur = conn.cursor()
    # BAD: string concatenation builds the query from untrusted input
    cur.execute("SELECT * FROM users WHERE id = " + user_id)
    return cur.fetchone()


def search(conn, term):
    cur = conn.cursor()
    # BAD: f-string interpolation into the query
    cur.execute(f"SELECT * FROM users WHERE name LIKE '%{term}%'")
    return cur.fetchall()


def get_user_safe(conn, user_id):
    cur = conn.cursor()
    # GOOD: parameterized query
    cur.execute("SELECT * FROM users WHERE id = ?", (user_id,))
    return cur.fetchone()
