## csharp

Core C# coding convention checks. &nbsp; `import csharp`

**Source:** [`packages/dotnet/csharp/src/`](../../../packages/dotnet/csharp/src/)

---

### Predicates

| Predicate | Applies To | Matches |
|---|---|---|
| `varDeclaration` | Statement | Declaration using `var` |
| `dynamicDeclaration` | Statement | Declaration using `dynamic` |
| `threadSleep` | Statement | Call to `Thread.Sleep()` |
| `consoleCall` | Statement | Call to `Console.*` |
| `catchesBaseException` | Statement | Catches broad `Exception` |
| `swallowsBaseException` | Statement | Catches `Exception` without rethrowing |
| `configureAwaitTrue` | Statement | `ConfigureAwait(true)` (the default) |
| `getAwaiterGetResult` | Statement | `GetAwaiter().GetResult()` — sync-over-async |
| `taskCompletionSourceNew` | Statement | `new TaskCompletionSource` without options |

---

### Checks

| Check | Severity | Message |
|---|---|---|
| `var-declarations` | error | Do not use 'var' |
| `dynamic-declarations` | error | Do not use 'dynamic' |
| `thread-sleep-calls` | error | Use Task.Delay instead of Thread.Sleep |
| `console-calls` | warning | Don't use Console in library code |
| `base-exception-catches` | warning | Catch a specific exception type instead of Exception |
| `swallowed-exceptions` | warning | Do not swallow Exception — rethrow or catch specific type |
| `configure-await-true-calls` | warning | Do not use ConfigureAwait(true) — it is the default |
| `sync-over-async-calls` | warning | Use await instead of GetAwaiter().GetResult() |
| `bare-task-completion-sources` | warning | Use TaskCompletionSource\<T\>(RunContinuationsAsynchronously) |
