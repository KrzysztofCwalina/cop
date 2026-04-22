# C# Library Package Reference

The `csharp-library` package provides design convention checks for .NET class libraries. It covers async patterns, `ConfigureAwait` usage, and API surface design rules.

**Source:** [`packages/dotnet/csharp-library/src/`](../../packages/dotnet/csharp-library/src/)

## Import

```ruby
import csharp-library
```

This also brings `csharp` into scope.

## Predicates

Defined in `definitions.cop`:

| Predicate | Matches |
|---|---|
| `asyncBoolParam(Parameter)` | Parameter named `async` with type `bool` |
| `publicWithAsyncBool(Method)` | Public method with `async` bool parameter |
| `exposesAsyncBool(Type)` | Type with public method having `async` bool parameter |
| `nonPublicAsync(Method)` | Non-public async method |
| `awaitUsingDefault(Statement)` | Await without `ConfigureAwait(false)` |
| `publicType(Type)` | Type with public modifier |

## Checks

Defined in `checks.cop`:

| Check | Severity | Message |
|---|---|---|
| `public-async-bool-params` | warning | {Type.Name} should not have a bool 'async' parameter in public API |
| `async-missing-bool-param` | warning | {Type.Name} non-public async method should have 'async' parameter |
| `awaits-using-default` | warning | Library code must use ConfigureAwait(false) on await expressions |

All checks are combined into the `csharp-library` array.

## Usage

```ruby
import csharp-library

# Run all library design checks
CHECK(csharp-library)
```
