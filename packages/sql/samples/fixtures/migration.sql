-- Example migration and data-fix script with both safe and risky statements.
CREATE TABLE users (
    id INT PRIMARY KEY,
    name VARCHAR(100),
    active BIT
);

INSERT INTO users (id, name, active) VALUES (1, 'Ada; Lovelace', 1);

DELETE FROM users;

DELETE FROM users WHERE id = 1;

UPDATE accounts
SET active = 0;

UPDATE accounts
SET active = 0
WHERE last_login < '2025-01-01';

SELECT * FROM orders;

SELECT id, name
FROM orders
WHERE active = 1;

/* Semicolons in comments should not split statements;
   this safe delete has a WHERE clause. */
DELETE FROM audit_logs WHERE created_at < '2024-01-01';
