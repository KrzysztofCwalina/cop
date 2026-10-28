# sql

Analyze SQL files by splitting `.sql` files into statements and exposing basic statement
metadata for static checks. The parser is hand-rolled (no third-party SQL parser) and handles
semicolons inside strings, quoted identifiers (double quotes, brackets, and backticks),
PostgreSQL dollar-quoted bodies, `--` comments, and nested `/* ... */` comments.
Standalone `GO` lines separate batches. `WITH` statements use the main operation's kind;
`SelectsStar` recognizes bare and qualified wildcards in mixed projection lists, but not
`COUNT(*)` or multiplication. Line tracking is linear rather than rescanning the file.

This is not a complete dialect grammar or schema resolver. MySQL `DELIMITER`, Oracle
slash terminators, SQLCMD directives, and procedural dialect control flow are not modeled.
Collections are read lazily per file and share one execution snapshot. Directory symlinks
and reparse points are not followed; file or directory access failures are reported.

## Types

- **SqlStatement** — a single SQL statement. Fields: `Kind` (uppercase leading keyword:
  `SELECT`, `INSERT`, `UPDATE`, `DELETE`, `CREATE`, `ALTER`, `DROP`, `MERGE`, or `OTHER`),
  `Text` (statement text normalized to one line), `Line`, `SelectsStar`, `HasWhere`, `File`,
  `Source`.

## Usage

```cop
import sql

let statements = sql.parse().Statements
let migrations = sql.parse('migrations').Statements
```

`SqlStatement` conforms to `TextFilePosition`, so it works directly with `toError` /
`toWarning` / `toInfo` from `code-analysis`.

## Example — flag risky DML and SELECT *

```cop
import sql
import code

predicate isUnscopedMutation(SqlStatement) =>
    (SqlStatement.Kind:equals('UPDATE') || SqlStatement.Kind:equals('DELETE'))
    && !SqlStatement.HasWhere

predicate isSelectStar(SqlStatement) => SqlStatement.SelectsStar

let violations =
    sql.parse().Statements:isUnscopedMutation
        :toError('SQL {item.Kind} statement has no WHERE clause') +
    sql.parse().Statements:isSelectStar
        :toWarning('SQL SELECT statement uses SELECT *')

command MAIN = CHECK(violations)
```

See `samples/risky-dml-and-select-star.cop` for a runnable version.
