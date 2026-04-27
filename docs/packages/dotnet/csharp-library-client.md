## csharp-library-client

Design convention checks for client libraries. &nbsp; `import csharp-library-client`

**Source:** [`packages/dotnet/csharp-library-client/src/`](../../../packages/dotnet/csharp-library-client/src/) &nbsp; **Depends on:** csharp-library

---

### Collections

| Collection | Type | Description |
|---|---|---|
| `Clients` | `[Type]` | Types ending with `Client` (excludes options types) |
| `Models` | `[Type]` | Public classes that are not clients or options |
| `ClientOptions` | `[Type]` | Types ending with `ClientOptions` |

---

### Predicates

| Predicate | Applies To | Matches |
|---|---|---|
| `client` | Type | Name ends with 'Client' |
| `clientOptionsType` | Type | Name ends with 'ClientOptions' |
| `model` | Type | Public class, not a client or options type |
| `constructorAcceptsOptions` | Constructor | Has options parameter |
| `cancellationToken` | Parameter | Type is `CancellationToken` |
| `publicAsync` | Method | Public async method |
| `asyncServiceMethod` | Method | Public virtual method ending with 'Async' |
| `protectedParameterless` | Constructor | Protected parameterless constructor |
| `collectionSuffix` | Type | Name ends with 'Collection' |
| `requestSuffix` | Type | Name ends with 'Request' |

---

### Checks

| Check | Severity | Message |
|---|---|---|
| `client-needs-options-ctor` | warning | Client should accept an options parameter |
| `client-sealed-or-abstract` | warning | Client should be sealed or abstract |
| `async-needs-cancellation-token` | warning | Async method missing CancellationToken |
| `client-methods-virtual` | warning | Service method should be virtual |
| `async-needs-sync-counterpart` | warning | Async method without sync counterpart |
