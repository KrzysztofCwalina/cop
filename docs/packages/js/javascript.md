# JavaScript Package Reference

The `javascript` package provides core JavaScript and TypeScript coding convention checks. It detects `console` calls, `alert()`, `eval()`, `debugger` statements, `var` usage, and swallowed exceptions.

**Source:** [`packages/js/javascript/src/`](../../packages/js/javascript/src/)

## Import

```ruby
import javascript
```

## Predicates

Defined in `definitions.cop`:

| Predicate | Matches |
|---|---|
| `consoleCall(Statement)` | Call to `console.*` |
| `alertCall(Statement)` | Call to `alert()` |
| `evalCall(Statement)` | Call to `eval()` |
| `debuggerStatement(Statement)` | `debugger` statement |
| `usesVar(Statement)` | Variable declaration using `var` |
| `catchWithoutRethrow(Statement)` | Error handler without rethrowing |

## Checks

Defined in `checks.cop`:

| Check | Severity | Message |
|---|---|---|
| `console-calls` | warning | Avoid console.{Statement.MemberName} in production code |
| `alert-calls` | error | Do not use alert() |
| `eval-calls` | error | Do not use eval() — it is a security risk |
| `debugger-statements` | error | Remove debugger statement |
| `var-declarations` | warning | Use const or let instead of var |
| `swallowed-exceptions` | warning | Do not swallow errors — rethrow or handle explicitly |

All checks are combined into the `javascript-checks` array.

## Usage

```ruby
import javascript

# Run all JavaScript/TypeScript checks
CHECK(javascript-checks)
```
