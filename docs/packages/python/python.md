## python

Python coding convention checks. &nbsp; `import python`

**Source:** [`packages/python/python/src/`](../../packages/python/python/src/)

---

### Predicates

| Predicate | Applies To | Matches |
|---|---|---|
| `printCall` | [`Statement`](../code.md#statement) | Call to `print()` |
| `bareExcept` | [`Statement`](../code.md#statement) | `except:` with no exception type |
| `catchesException` | [`Statement`](../code.md#statement) | Error handler catching broad `Exception` |
| `swallowsException` | [`Statement`](../code.md#statement) | Error handler catching `Exception` without reraising |

---

### Checks

| Check | Severity | Message |
|---|---|---|
| `print-calls` | warning | Avoid print() — use logging instead |
| `bare-except-clauses` | warning | Do not use bare except — catch a specific exception type |
| `silenced-exceptions` | warning | Do not silence Exception — reraise or catch a specific type |
