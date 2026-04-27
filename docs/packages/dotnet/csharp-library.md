## csharp-library

Design convention checks for .NET class libraries. &nbsp; `import csharp-library`

**Source:** [`packages/dotnet/csharp-library/src/`](../../../packages/dotnet/csharp-library/src/) &nbsp; **Depends on:** csharp

---

### Predicates

| Predicate | Applies To | Matches |
|---|---|---|
| `asyncBoolParam` | Parameter | Named `async` with type `bool` |
| `publicWithAsyncBool` | Method | Public method with `async` bool parameter |
| `exposesAsyncBool` | Type | Has public method with `async` bool parameter |
| `nonPublicAsync` | Method | Non-public async method |
| `awaitUsingDefault` | Statement | Await without `ConfigureAwait(false)` |
| `publicType` | Type | Has public modifier |

---

### Checks

| Check | Severity | Message |
|---|---|---|
| `public-async-bool-params` | warning | Should not have bool 'async' parameter in public API |
| `async-missing-bool-param` | warning | Non-public async method should have 'async' parameter |
| `awaits-using-default` | warning | Library code must use ConfigureAwait(false) |
