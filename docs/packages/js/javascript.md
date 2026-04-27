## javascript

JavaScript and TypeScript coding convention checks. &nbsp; `import javascript`

**Source:** [`packages/js/javascript/src/`](../../packages/js/javascript/src/)

---

### Predicates

| Predicate | Applies To | Matches |
|---|---|---|
| `consoleCall` | Statement | Call to `console.*` |
| `alertCall` | Statement | Call to `alert()` |
| `evalCall` | Statement | Call to `eval()` |
| `debuggerStatement` | Statement | `debugger` statement |
| `usesVar` | Statement | Variable declaration using `var` |
| `catchWithoutRethrow` | Statement | Error handler without rethrowing |

---

### Checks

| Check | Severity | Message |
|---|---|---|
| `console-calls` | warning | Avoid console.{item.MemberName} in production code |
| `alert-calls` | error | Do not use alert() |
| `eval-calls` | error | Do not use eval() — it is a security risk |
| `debugger-statements` | error | Remove debugger statement |
| `var-declarations` | warning | Use const or let instead of var |
| `swallowed-exceptions` | warning | Do not swallow errors — rethrow or handle explicitly |
