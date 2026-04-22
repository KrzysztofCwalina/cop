# C# Package Reference

The `csharp` package provides core C# coding convention checks. It detects common issues like implicit typing with `var`, `dynamic` usage, `Thread.Sleep`, console output, exception handling anti-patterns, and sync-over-async calls.

**Source:** [`packages/dotnet/csharp/src/`](../../packages/dotnet/csharp/src/)

## Import

```ruby
import csharp
```

## Predicates

Defined in `definitions.cop`:

| Predicate | Matches |
|---|---|
| `varDeclaration(Statement)` | Declaration using `var` keyword |
| `dynamicDeclaration(Statement)` | Declaration using `dynamic` keyword |
| `threadSleep(Statement)` | Call to `Thread.Sleep()` |
| `consoleCall(Statement)` | Call to `Console.*` |
| `catchesBaseException(Statement)` | Error handler catching broad `Exception` |
| `swallowsBaseException(Statement)` | Error handler catching `Exception` without rethrowing |
| `configureAwaitTrue(Statement)` | `ConfigureAwait(true)` call (the default) |
| `getAwaiterGetResult(Statement)` | `GetAwaiter().GetResult()` — sync-over-async |
| `taskCompletionSourceNew(Statement)` | `new TaskCompletionSource` without options |

## Checks

Defined in `checks.cop`:

| Check | Severity | Message |
|---|---|---|
| `var-declarations` | error | Do not use 'var' for {Statement.MemberName} |
| `dynamic-declarations` | error | Do not use 'dynamic' |
| `thread-sleep-calls` | error | Use Task.Delay instead of Thread.Sleep |
| `console-calls` | warning | Don't use Console.{Statement.MemberName} in library code |
| `base-exception-catches` | warning | Catch a specific exception type instead of Exception |
| `swallowed-exceptions` | warning | Do not swallow Exception — rethrow or catch a specific type |
| `configure-await-true-calls` | warning | Do not use ConfigureAwait(true) — it is the default |
| `sync-over-async-calls` | warning | Do not use GetAwaiter().GetResult() — use await instead |
| `bare-task-completion-sources` | warning | Use TaskCompletionSource\<T\>(RunContinuationsAsynchronously) |

All checks are combined into the `csharp-checks` array.

## Usage

```ruby
import csharp

# Run all C# checks
CHECK(csharp-checks)
```
