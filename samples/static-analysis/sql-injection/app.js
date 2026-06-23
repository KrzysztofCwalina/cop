function getUser(db, userId) {
  // BAD: string concatenation builds the query from untrusted input
  return db.query("SELECT * FROM users WHERE id = " + userId);
}

function search(db, term) {
  // BAD: template-literal interpolation into the query
  return db.query(`SELECT * FROM users WHERE name LIKE '%${term}%'`);
}

function getUserSafe(db, userId) {
  // GOOD: parameterized query
  return db.query("SELECT * FROM users WHERE id = ?", [userId]);
}
