# API Listing

Generate the public API surface of C# source files as a flat list of canonical signatures.

## Setup

Create a file called `api-listing.cop` in your project:

```ruby
import csharp-api

export command api-listing = SAVE('api-surface.txt', '{Api.Signature}', Code.Api:csharp:publicApi)
```

## Run

```bash
cop run api-listing.cop api-listing
```

This writes `api-surface.txt` with one signature per line:

```
class ClientOptions
class MyClientOptions
class MyClient
ctor MyClient(Uri, TokenCredential, MyClientOptions)
ctor MyClient()
method MyClient.GetItemAsync(string, CancellationToken) : Task<Response<Item>>
method MyClient.DeleteItemAsync(string, CancellationToken) : Task<Response>
property MyClientOptions.Diagnostics : DiagnosticsOptions
event MyClient.ItemChanged : EventHandler
```

The file can be checked into source control as a baseline for [API diff](api-diff.md).

## Filtering

The `csharp-api` package exports predicates for filtering by API kind:

| Predicate | Matches |
|---|---|
| `publicApi` | All public API entries |
| `apiType` | Classes, interfaces, structs, enums |
| `apiMethod` | Methods |
| `apiProperty` | Properties |
| `apiEvent` | Events |
| `apiCtor` | Constructors |
| `apiEnumValue` | Enum values |

List only methods:

```ruby
import csharp-api

export command list-methods = SAVE('methods.txt', '{Api.Signature}', Code.Api:csharp:apiMethod)
```

List only types:

```ruby
import csharp-api

export command list-types = SAVE('types.txt', '{Api.Signature}', Code.Api:csharp:apiType)
```

## Diagnostics vs. Listings

The `SAVE` command writes plain text to a file — use it for listings and baselines.

For analysis checks that report problems, use `toError`, `toWarning`, or `toInfo`. These create `Violation` objects with severity, file location, and message:

```ruby
import csharp-api
import code-analysis

# This creates Violation objects — use for analysis, not for listings
export let missing-docs = Code.Api:csharp:publicApi
    :toWarning('{Api.Signature} has no documentation')
```

The `Violation` type has these fields:

| Field | Type | Description |
|---|---|---|
| `Violation.Severity` | string | `'error'`, `'warning'`, or `'info'` |
| `Violation.Message` | string | The formatted message |
| `Violation.File` | string | Source file path |
| `Violation.Line` | int | Source line number |
| `Violation.Source` | string | Language of the source file |

## The Api Type

Each entry in `Code.Api` has these fields:

| Field | Type | Description |
|---|---|---|
| `Api.Kind` | string | `class`, `interface`, `struct`, `enum`, `method`, `property`, `event`, `ctor`, `enumvalue` |
| `Api.TypeName` | string | Declaring type name |
| `Api.MemberName` | string | Member name (empty for type-level entries) |
| `Api.Signature` | string | Canonical signature (see format below) |
| `Api.Line` | int | Source line number |
| `Api.File` | File | Source file |

### Signature Format

```
{kind} {typeName}[.{memberName}][({paramTypes})][ : {returnType}]
```

Examples:

```
class MyClient
interface IMyService
method MyClient.GetItemAsync(string, CancellationToken) : Task<Response>
property MyClientOptions.RetryCount : int
ctor MyClient(Uri, TokenCredential, MyClientOptions)
event MyClient.ItemChanged : EventHandler
enumvalue ServiceVersion.V2024_01_01
```
