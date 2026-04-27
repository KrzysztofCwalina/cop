## csharp-library-client

Design convention checks for client libraries. &nbsp; `import csharp-library-client`

**Source:** [`packages/dotnet/csharp-library-client/src/`](../../../packages/dotnet/csharp-library-client/src/) &nbsp; **Depends on:** csharp-library

---

### Collections

| Collection | Type | Description |
|---|---|---|
| `Clients` | `[`[`Type`](../code.md#type)`]` | Types ending with `Client` (excludes options types) |
| `Models` | `[`[`Type`](../code.md#type)`]` | Public classes that are not clients or options |
| `ClientOptions` | `[`[`Type`](../code.md#type)`]` | Types ending with `ClientOptions` |

---

### Predicates

| Predicate | Applies To | Matches |
|---|---|---|
| `client` | [Type](../code.md#type) | Name ends with 'Client' |
| `clientOptionsType` | [Type](../code.md#type) | Name ends with 'ClientOptions' |
| `model` | [Type](../code.md#type) | Public class, not a client or options type |
| `constructorAcceptsOptions` | [Constructor](../code.md#method) | Has options parameter |
| `cancellationToken` | [Parameter](../code.md#parameter) | Type is `CancellationToken` |
| `publicAsync` | [Method](../code.md#method) | Public async method |
| `asyncServiceMethod` | [Method](../code.md#method) | Public virtual method ending with 'Async' |
| `protectedParameterless` | [Constructor](../code.md#method) | Protected parameterless constructor |
| `collectionSuffix` | [Type](../code.md#type) | Name ends with 'Collection' |
| `requestSuffix` | [Type](../code.md#type) | Name ends with 'Request' |

---

### Checks

| Check | Severity | Message |
|---|---|---|
| `client-needs-options-ctor` | warning | Client should accept an options parameter |
| `client-sealed-or-abstract` | warning | Client should be sealed or abstract |
| `async-needs-cancellation-token` | warning | Async method missing CancellationToken |
| `client-methods-virtual` | warning | Service method should be virtual |
| `async-needs-sync-counterpart` | warning | Async method without sync counterpart |
