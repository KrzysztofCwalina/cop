# API Listing

Print the public API surface of C# source files as a flat list of canonical signatures.

## Setup

Create a file called `api-listing.cop` in your project:

```ruby
import csharp-api
import code-analysis

export let api-listing = Code.Api:csharp:publicApi
    :toOutput('{Api.Signature}')
```

## Run

```bash
cop run api-listing.cop
```

Output:

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
import code-analysis

export let methods = Code.Api:csharp:apiMethod
    :toOutput('{Api.Signature}')
```

List only types:

```ruby
import csharp-api
import code-analysis

export let types = Code.Api:csharp:apiType
    :toOutput('{Api.Signature}')
```

## Save to File

To write the API listing to a file instead of stdout, use a `SAVE` command:

```ruby
import csharp-api

export command api-export = SAVE('api-surface.txt', '{Api.Signature}', Code.Api:csharp:publicApi)
```

Run with:

```bash
cop run api-listing.cop api-export
```

This writes one signature per line to `api-surface.txt`. The file can be checked into source control as a baseline for [API diff](api-diff.md).

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
