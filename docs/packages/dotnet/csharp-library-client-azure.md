# Azure C# Client Library Package Reference

The `csharp-library-client-azure` package provides Azure-specific client library checks. It builds on `csharp-library-client` with Azure.Core pipeline integration, TokenCredential authentication requirements, service versioning, and protocol/convenience method patterns.

**Source:** [`packages/dotnet/csharp-library-client-azure/src/`](../../packages/dotnet/csharp-library-client-azure/src/)

## Import

```ruby
import csharp-library-client-azure
```

This also brings `csharp-library-client`, `csharp-library`, and `csharp` into scope.

## Predicates

Defined in `definitions.cop`:

| Predicate | Matches |
|---|---|
| `tokenCredential(Parameter)` | Parameter type is `TokenCredential` |
| `inheritsClientOptions(Type)` | Inherits from `ClientOptions` or `ClientPipelineOptions` |
| `requestContent(Parameter)` | Parameter type is `RequestContent` or `RequestBody` |
| `protocolMethod(Method)` | Public virtual method with `RequestContent` parameter |
| `convenienceMethod(Method)` | Public virtual method without `RequestContent` parameter |
| `serviceVersionEnum(Type)` | Enum type named `ServiceVersion` |
| `internalsVisibleTo(Statement)` | `InternalsVisibleTo` attribute |

## Checks

Defined in `checks.cop` (25+ checks):

| Check | Severity | Description |
|---|---|---|
| `client-needs-token-credential` | warning | Client constructor must accept TokenCredential |
| `client-needs-mocking-ctor` | warning | Client must have protected parameterless constructor |
| `options-inherits-base` | warning | Options type must inherit from ClientOptions/ClientPipelineOptions |
| `client-needs-options-ctor` | warning | Client must have constructor with options parameter |
| `client-needs-simple-ctor` | warning | Client must have constructor with defaulted options |
| `no-request-content-in-convenience` | warning | Convenience methods must not use RequestContent |
| `options-single-ctor-param` | warning | Options constructor should have at most one parameter |
| `no-collection-suffix` | warning | Avoid 'Collection' suffix on model types |
| `no-request-suffix` | warning | Avoid 'Request' suffix on model types |
| `no-parameter-suffix` | warning | Avoid 'Parameter(s)' suffix on model types |
| `no-option-suffix` | warning | Avoid 'Option(s)' suffix on model types |
| `no-resource-suffix` | warning | Avoid 'Resource' suffix on model types |
| `options-first-param-service-version` | warning | Options constructor first param must be ServiceVersion |
| `options-needs-service-version-enum` | warning | Options type must have nested ServiceVersion enum |
| `service-version-naming` | warning | ServiceVersion members must match V#_# pattern |
| `service-version-default-value` | warning | ServiceVersion parameter must have default value |
| `service-method-needs-cancellation` | warning | Service methods must accept CancellationToken or RequestContext |
| `no-banned-internal-types` | warning | Public API must not expose internal framework types |
| `no-raw-http-return-types` | warning | Must not return HttpResponseMessage/HttpRequestMessage |
| `no-pipeline-types-in-api` | warning | Must not expose Azure.Core.Pipeline types |
| `model-reader-writer-context` | warning | ModelReaderWriter.Read must include context argument |
| `protocol-method-return-type` | warning | Protocol methods must return allowed types |
| `no-ambiguous-overloads` | warning | Protocol and convenience methods must not be ambiguous |
| `internals-visible-to` | warning | InternalsVisibleTo must target test assemblies only |

All checks are combined into the `csharp-library-client-azure` array.

## Usage

```ruby
import csharp-library-client-azure

# Run all Azure client library checks
CHECK(csharp-library-client-azure)
```
