# API Listing

Generate the public API surface of C# source files or compiled assemblies.

## Quick Start — Signatures

Create a file called `api-listing.cop` in your project:

```ruby
import csharp-api

export command api-listing = SAVE('api-surface.txt', '{Api.Signature}', Code.Api:csharp:publicApi)
```

```bash
cop run api-listing.cop api-listing
```

This writes `api-surface.txt` with one signature per line:

```
class MyClient
ctor MyClient(Uri, TokenCredential, MyClientOptions)
method MyClient.GetItemAsync(string, CancellationToken) : Task<Response<Item>>
property MyClientOptions.Diagnostics : DiagnosticsOptions
event MyClient.ItemChanged : EventHandler
```

## Quick Start — C# Stubs

For C# stub output (Azure SDK GenAPI-compatible format), use `Api.StubLine`:

```ruby
import csharp-api

export command api-stubs = SAVE('api-stubs.txt', '{Api.StubLine}', Code.Api:csharp:publicApi)
```

Each `StubLine` is a single C# declaration with stub bodies:

```csharp
public sealed class MyClient
public MyClient(Uri endpoint, TokenCredential credential, MyClientOptions options) { }
public virtual Task<Response> GetItemAsync(string id, CancellationToken cancellationToken) { throw null; }
public string Name { get { throw null; } set { } }
public event EventHandler ItemChanged { add { } remove { } }
```

## Loading from Assemblies

Use `Code.Load('path')` to read the public API from a compiled .NET DLL instead of source files. Type names are fully qualified (from metadata), matching GenAPI output:

```ruby
import csharp-api

let apis = Code.Load('bin/Release/net8.0/MyPackage.dll')
predicate allApi(Api) => Api.Kind != ''

export command list-dll = SAVE('api-surface.txt', '{Api.Signature}', apis:allApi)
```

This is useful for:
- Generating baselines from published packages
- Comparing against NuGet package DLLs
- Getting fully qualified type names (e.g., `System.Threading.CancellationToken`)

## The toApiLine Function

The `toApiLine` function maps `Api` entries to structured `ApiLine` objects:

```ruby
import csharp-api

let apis = Code.Api:csharp:publicApi:toApiLine
```

The `ApiLine` type has these fields:

| Field | Type | Description |
|---|---|---|
| `ApiLine.Text` | string | The `StubLine` text (C# stub representation) |
| `ApiLine.Kind` | string | API kind (`class`, `method`, `property`, etc.) |
| `ApiLine.TypeName` | string | Declaring type name |
| `ApiLine.MemberName` | string | Member name (empty for type-level entries) |
| `ApiLine.File` | string | Source file path |
| `ApiLine.Line` | int | Source line number |

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

## Diagnostics vs. Listings

The `SAVE` command writes plain text to a file — use it for listings and baselines.

For analysis checks that report problems, use `toError`, `toWarning`, or `toInfo`. These create `Violation` objects with severity, file location, and message:

```ruby
import csharp-api
import code-analysis

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

Each entry in `Code.Api` (or from `Code.Load()`) has these fields:

| Field | Type | Description |
|---|---|---|
| `Api.Kind` | string | `class`, `interface`, `struct`, `enum`, `method`, `property`, `event`, `ctor`, `enumvalue` |
| `Api.TypeName` | string | Declaring type name |
| `Api.MemberName` | string | Member name (empty for type-level entries) |
| `Api.Signature` | string | Canonical signature (stable comparison key) |
| `Api.StubLine` | string | C# stub representation (for listings) |
| `Api.Line` | int | Source line number (0 for assembly-loaded entries) |
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

### StubLine Format

Each `StubLine` is a single C# declaration with stub bodies:

| Kind | Example |
|---|---|
| Class | `public partial class MyClient : ClientBase` |
| Interface | `public partial interface IMyService` |
| Enum | `public enum ServiceVersion` |
| Method (void) | `public void Close() { }` |
| Method (non-void) | `public virtual string GetName() { throw null; }` |
| Property (get/set) | `public string Name { get { throw null; } set { } }` |
| Property (get-only) | `public int Count { get { throw null; } }` |
| Constructor | `public MyClient(Uri endpoint) { }` |
| Event | `public event EventHandler Changed { add { } remove { } }` |
| Field | `public static readonly string Empty;` |
| Enum value | `V2024_01_01,` |
