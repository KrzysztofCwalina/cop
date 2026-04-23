# API Listing

Generate the public API surface of C# source files or compiled assemblies as structured C# stubs — the same format used by Azure SDK's GenAPI baseline files.

## Quick Start

Create a file called `api-listing.cop` in your project:

```ruby
import csharp-api

export command api-listing = SAVE('api-surface.txt', '{Api.StubLine}', Code.Api:csharp:publicApi)
```

```bash
cop run api-listing.cop api-listing
```

This produces a C# stub listing with proper structure — namespaces, type blocks, indented members, and stub bodies:

```csharp
namespace Azure.ResourceManager.GraphServices
{
    public partial class GraphServicesExtensions
    {
        public static Azure.Response<Azure.ResourceManager.GraphServices.GraphServicesAccountResource> GetGraphServicesAccountResource(this Azure.ResourceManager.ArmClient client, Azure.Core.ResourceIdentifier id) { throw null; }
        public static Azure.ResourceManager.GraphServices.GraphServicesAccountResourceCollection GetGraphServicesAccountResources(this Azure.ResourceManager.Resources.ResourceGroupResource resourceGroup) { throw null; }
    }
    public partial class GraphServicesAccountResource : Azure.ResourceManager.ArmResource
    {
        protected GraphServicesAccountResource() { }
        public virtual Azure.ResourceManager.GraphServices.GraphServicesAccountResourceData Data { get { throw null; } }
        public virtual Azure.Response<Azure.ResourceManager.GraphServices.GraphServicesAccountResource> Get(System.Threading.CancellationToken cancellationToken) { throw null; }
        public virtual System.Threading.Tasks.Task<Azure.Response<Azure.ResourceManager.GraphServices.GraphServicesAccountResource>> GetAsync(System.Threading.CancellationToken cancellationToken) { throw null; }
        public virtual Azure.ResourceManager.ArmOperation Delete(Azure.WaitUntil waitUntil, System.Threading.CancellationToken cancellationToken) { throw null; }
    }
    public partial class GraphServicesAccountResourceData : Azure.ResourceManager.Models.TrackedResourceData
    {
        public GraphServicesAccountResourceData(Azure.Core.AzureLocation location) { }
        public string AppId { get { throw null; } set { } }
    }
    public enum ServiceVersion
    {
        V2023_04_13,
    }
}
```

The output matches Azure SDK's `api/*.cs` stub file format: `partial class/struct/interface`, fully qualified type names (when loaded from DLLs), `{ throw null; }` for non-void methods and getters, `{ }` for void methods, setters, constructors, and event accessors.

## Loading from Assemblies

Use `Code.Load()` to read types from a compiled .NET DLL. This returns the same data model as source loading — just with no statements or line info. Access sub-collections via dot syntax:

```ruby
import csharp-api

let dll = Code.Load('bin/Release/net8.0/MyPackage.dll')

export command list-dll = SAVE('api-surface.txt', '{Api.StubLine}', dll.Api:publicApi)
```

`Code.Load()` and the implicit source loading are superset-subset of the same model:

| Sub-collection | Source | Code.Load |
|---|---|---|
| `.Types` | ✅ all parsed types | ✅ public exported types |
| `.Api` | ✅ public API entries | ✅ public API entries |
| `.Statements` | ✅ all statements | empty (no source) |
| `.Lines` | ✅ source lines | empty (no source) |
| `.Files` | ✅ source files | ✅ the loaded assembly |

This is useful for:
- Generating baselines from published packages
- Comparing against NuGet package DLLs
- Getting fully qualified type names (e.g., `System.Threading.CancellationToken`)

## Signature Format (for Diff)

For API diff comparison, use `Api.Signature` instead of `Api.StubLine`. Signatures are compact canonical keys:

```ruby
import csharp-api

export command api-signatures = SAVE('api-signatures.txt', '{Api.Signature}', Code.Api:csharp:publicApi)
```

```
class MyClient
ctor MyClient(Uri, TokenCredential, MyClientOptions)
method MyClient.GetItemAsync(string, CancellationToken) : Task<Response<Item>>
property MyClientOptions.Diagnostics : DiagnosticsOptions
event MyClient.ItemChanged : EventHandler
enumvalue ServiceVersion.V2024_01_01
```

See [API Diff](api-diff.md) for baseline comparison using signatures.

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

Each entry in `Code.Api` (or from `Code.Load().Api`) has these fields:

| Field | Type | Description |
|---|---|---|
| `Api.Kind` | string | `class`, `interface`, `struct`, `enum`, `method`, `property`, `event`, `ctor`, `enumvalue` |
| `Api.TypeName` | string | Declaring type name |
| `Api.MemberName` | string | Member name (empty for type-level entries) |
| `Api.Signature` | string | Canonical signature (stable comparison key) |
| `Api.StubLine` | string | C# stub declaration (for listings) |
| `Api.Line` | int | Source line number (0 for assembly-loaded entries) |
| `Api.File` | File | Source file |

### StubLine Format

Each `StubLine` is a single C# declaration line with stub bodies (indentation added by the generator based on nesting):

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

### Signature Format

```
{kind} {typeName}[.{memberName}][({paramTypes})][ : {returnType}]
```
