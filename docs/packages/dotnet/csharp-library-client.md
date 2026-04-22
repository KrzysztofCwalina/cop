# C# Client Library Package Reference

The `csharp-library-client` package provides design convention checks for building client libraries. It covers constructor patterns, virtual methods, CancellationToken requirements, and naming conventions for models.

**Source:** [`packages/dotnet/csharp-library-client/src/`](../../packages/dotnet/csharp-library-client/src/)

## Import

```ruby
import csharp-library-client
```

This also brings `csharp-library` and `csharp` into scope.

## Collections

| Collection | Description |
|---|---|
| `Clients` | C# types ending with `Client` (excludes options types) |
| `Models` | Public classes that are not clients or options types |
| `ClientOptions` | C# types ending with `ClientOptions` |

## Predicates

Defined in `definitions.cop`:

| Predicate | Matches |
|---|---|
| `client(Type)` | Type name ends with 'Client' |
| `clientOptionsType(Type)` | Type name ends with 'ClientOptions' |
| `model(Type)` | Public class, not a client or options type |
| `constructorAcceptsOptions(Constructor)` | Constructor has options parameter |
| `cancellationToken(Parameter)` | Parameter type is `CancellationToken` |
| `publicAsync(Method)` | Public async method |
| `asyncServiceMethod(Method)` | Public virtual method ending with 'Async' |
| `protectedParameterless(Constructor)` | Protected parameterless constructor |
| `collectionSuffix(Type)` | Type name ends with 'Collection' |
| `requestSuffix(Type)` | Type name ends with 'Request' |

## Checks

Defined in `checks.cop`:

| Check | Severity | Message |
|---|---|---|
| `client-needs-options-ctor` | warning | {Type.Name} should accept an options parameter |
| `client-sealed-or-abstract` | warning | {Type.Name} should be sealed or abstract |
| `async-needs-cancellation-token` | warning | {Type.Name} is async without CancellationToken |
| `client-methods-virtual` | warning | {Type.Name} has non-virtual service method |
| `async-needs-sync-counterpart` | warning | {Type.Name} has async method without sync counterpart |

All checks are combined into the `csharp-library-client` array.

## Usage

```ruby
import csharp-library-client

# Run all client library checks
CHECK(csharp-library-client)
```
