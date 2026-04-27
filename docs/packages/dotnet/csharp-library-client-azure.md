## csharp-library-client-azure

Azure-specific client library checks. &nbsp; `import csharp-library-client-azure`

**Source:** [`packages/dotnet/csharp-library-client-azure/src/`](../../../packages/dotnet/csharp-library-client-azure/src/) &nbsp; **Depends on:** csharp-library-client

---

### Predicates

| Predicate | Applies To | Matches |
|---|---|---|
| `tokenCredential` | [Parameter](../code.md#parameter) | Type is `TokenCredential` |
| `inheritsClientOptions` | [Type](../code.md#type) | Inherits from `ClientOptions` or `ClientPipelineOptions` |
| `requestContent` | [Parameter](../code.md#parameter) | Type is `RequestContent` or `RequestBody` |
| `protocolMethod` | [Method](../code.md#method) | Public virtual method with `RequestContent` parameter |
| `convenienceMethod` | [Method](../code.md#method) | Public virtual method without `RequestContent` parameter |
| `serviceVersionEnum` | [Type](../code.md#type) | Enum named `ServiceVersion` |
| `internalsVisibleTo` | [Statement](../code.md#statement) | `InternalsVisibleTo` attribute |

---

### Checks

| Check | Severity | Message |
|---|---|---|
| `client-needs-token-credential` | warning | Constructor must accept TokenCredential |
| `client-needs-mocking-ctor` | warning | Must have protected parameterless constructor |
| `options-inherits-base` | warning | Options must inherit ClientOptions/ClientPipelineOptions |
| `client-needs-options-ctor` | warning | Must have constructor with options parameter |
| `client-needs-simple-ctor` | warning | Must have constructor with defaulted options |
| `no-request-content-in-convenience` | warning | Convenience methods must not use RequestContent |
| `options-single-ctor-param` | warning | Options constructor should have at most one parameter |
| `no-collection-suffix` | warning | Avoid 'Collection' suffix on model types |
| `no-request-suffix` | warning | Avoid 'Request' suffix on model types |
| `no-parameter-suffix` | warning | Avoid 'Parameter(s)' suffix on model types |
| `no-option-suffix` | warning | Avoid 'Option(s)' suffix on model types |
| `no-resource-suffix` | warning | Avoid 'Resource' suffix on model types |
| `options-first-param-service-version` | warning | Options first param must be ServiceVersion |
| `options-needs-service-version-enum` | warning | Options must have nested ServiceVersion enum |
| `service-version-naming` | warning | ServiceVersion members must match V#_# pattern |
| `service-version-default-value` | warning | ServiceVersion parameter must have default value |
| `service-method-needs-cancellation` | warning | Must accept CancellationToken or RequestContext |
| `no-banned-internal-types` | warning | Must not expose internal framework types |
| `no-raw-http-return-types` | warning | Must not return HttpResponseMessage/HttpRequestMessage |
| `no-pipeline-types-in-api` | warning | Must not expose Azure.Core.Pipeline types |
| `model-reader-writer-context` | warning | ModelReaderWriter.Read must include context argument |
| `protocol-method-return-type` | warning | Protocol methods must return allowed types |
| `no-ambiguous-overloads` | warning | Protocol and convenience methods must not be ambiguous |
| `internals-visible-to` | warning | InternalsVisibleTo must target test assemblies only |
