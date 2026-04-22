# Python Package Reference

The `python` package provides core Python coding convention checks. It detects `print()` calls, bare `except` clauses, broad `Exception` catches, and swallowed exceptions.

**Source:** [`packages/python/python/src/`](../../packages/python/python/src/)

## Import

```ruby
import python
```

## Predicates

Defined in `definitions.cop`:

| Predicate | Matches |
|---|---|
| `printCall(Statement)` | Call to `print()` |
| `bareExcept(Statement)` | `except:` with no exception type |
| `catchesException(Statement)` | Error handler catching broad `Exception` |
| `swallowsException(Statement)` | Error handler catching `Exception` without reraising |

## Checks

Defined in `checks.cop`:

| Check | Severity | Message |
|---|---|---|
| `print-calls` | warning | Avoid print() — use logging instead |
| `bare-except-clauses` | warning | Do not use bare except — catch a specific exception type |
| `silenced-exceptions` | warning | Do not silence Exception — reraise or catch a specific type |

All checks are combined into the `python-checks` array.

## Usage

```ruby
import python

# Run all Python checks
CHECK(python-checks)
```
