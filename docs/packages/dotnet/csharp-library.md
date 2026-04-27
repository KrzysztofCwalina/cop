## csharp-library

Design convention checks for .NET class libraries. &nbsp; `import csharp-library`

**Source:** [`packages/dotnet/csharp-library/src/`](../../../packages/dotnet/csharp-library/src/) &nbsp; **Depends on:** csharp

---

### Predicates

| Predicate | Applies To | Matches |
|---|---|---|
| `asyncBoolParam` | [Parameter](../code.md#parameter) | Named `async` with type `bool` |
| `publicWithAsyncBool` | [Method](../code.md#method) | Public method with `async` bool parameter |
| `exposesAsyncBool` | [Type](../code.md#type) | Has public method with `async` bool parameter |
| `nonPublicAsync` | [Method](../code.md#method) | Non-public async method |
| `awaitUsingDefault` | [Statement](../code.md#statement) | Await without `ConfigureAwait(false)` |
| `publicType` | [Type](../code.md#type) | Has public modifier |

---

### Checks

| Check | Severity | Message |
|---|---|---|
| `public-async-bool-params` | warning | Should not have bool 'async' parameter in public API |
| `async-missing-bool-param` | warning | Non-public async method should have 'async' parameter |
| `awaits-using-default` | warning | Library code must use ConfigureAwait(false) |
