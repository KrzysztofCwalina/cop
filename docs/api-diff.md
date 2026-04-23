# API Diff

Compare the current public API surface against a baseline to detect breaking changes (removed APIs) and additions.

## How It Works

1. **Maintain a baseline** — a C# stub file listing the public API surface, checked into source control
2. **Run diff checks** — cop parses both the baseline stub and the current source through the same C# parser, then compares `Api.Signature` entries from each side

The baseline is a standard C# file (like Azure SDK's `api/*.cs` stubs). Both sides go through the same parser, so signatures match naturally — no custom format needed.

## Setup

Create a file called `api-compat.cop` in your project:

```ruby
import csharp-api
import code-analysis

# Baseline: C# stub files in api/ directory
predicate baselineApi(Api) => publicApi && Api.File.Path:matches('[/\\\\]api[/\\\\]')
# Source: everything NOT in api/
predicate sourceApi(Api) => publicApi && !Api.File.Path:matches('[/\\\\]api[/\\\\]')

# Build lookup lists from current source and baseline
let baselineSignatures = Code.Api:csharp:baselineApi:select(Api.Signature)
let currentSignatures = Code.Api:csharp:sourceApi:select(Api.Signature)

# Diff predicates
predicate removedApi(Api) => baselineApi && !Api.Signature:in(currentSignatures)
predicate addedApi(Api) => sourceApi && !Api.Signature:in(baselineSignatures)

# Report results
export let api-removed = Code.Api:removedApi
    :toError('API REMOVED (breaking): {Api.Signature}')
export let api-added = Code.Api:addedApi
    :toInfo('API ADDED: {Api.Signature}')
```

## Baseline Format

The baseline is a regular C# file with type and member declarations. This is the same format used by the Azure SDK (`sdk/*/api/*.cs`):

```csharp
// api/MyPackage.cs
namespace MyNamespace
{
    public sealed class MyClient
    {
        public MyClient(Uri endpoint, TokenCredential credential, MyClientOptions options) { }
        protected MyClient() { }
        public Task<Response> GetItemAsync(string id, CancellationToken cancellationToken) { throw null; }
        public Task<Response> DeleteItemAsync(string id, CancellationToken cancellationToken) { throw null; }
    }
    public class MyClientOptions : ClientOptions
    {
        public ServiceVersion Version { get { throw null; } }
    }
}
```

Cop parses this as normal C# source and extracts the same `Code.Api` entries it would from real source code.

## Running the Diff

```bash
cop run api-compat.cop
```

If an API was removed (breaking change):

```
ERROR: API REMOVED (breaking): method MyClient.DeleteItemAsync(string, CancellationToken) : Task<Response>
```

If an API was added (non-breaking):

```
INFO: API ADDED: method MyClient.ListItemsAsync(CancellationToken) : Task<Response<List<Item>>>
```

If the baseline matches the current source, there is no output and cop exits with code 0.

## Updating the Baseline

When you intentionally change the API, update the baseline stub file to match the new surface. The baseline is a regular `.cs` file — edit it directly or regenerate it with your existing tooling (e.g., GenAPI).

## Customization

Everything in `api-compat.cop` is policy you control:

| What | How | Example |
|---|---|---|
| **Baseline path pattern** | Change `baselineApi` predicate | `Api.File.Path:matches('ApiSurface')` |
| **Severity of removed APIs** | Use `toError`, `toWarning`, or `toInfo` | `:toWarning('...')` |
| **Severity of added APIs** | Same | `:toError('...')` |
| **Scope** | Add predicates to filter | Only check certain types or namespaces |

### Example: Custom Baseline Location

For a repo that stores baselines at `sdk/{service}/ApiSurface.cs`:

```ruby
import csharp-api
import code-analysis

predicate baselineApi(Api) => publicApi && Api.File.Path:matches('ApiSurface')
predicate sourceApi(Api) => publicApi && !Api.File.Path:matches('ApiSurface')

let baselineSignatures = Code.Api:csharp:baselineApi:select(Api.Signature)
let currentSignatures = Code.Api:csharp:sourceApi:select(Api.Signature)

predicate removedApi(Api) => baselineApi && !Api.Signature:in(currentSignatures)
predicate addedApi(Api) => sourceApi && !Api.Signature:in(baselineSignatures)

export let api-removed = Code.Api:removedApi:toError('BREAKING: {Api.Signature}')
export let api-added = Code.Api:addedApi:toWarning('ADDED: {Api.Signature}')
```

## Key Concepts

- **Api-to-Api comparison** — both the baseline stub and the current source are parsed as C#, producing `Code.Api` entries with identical signature formats
- **`:select()`** projects a collection to a list of strings (e.g., `Code.Api:baselineApi:select(Api.Signature)` → list of signature strings)
- **`:in()`** checks if a value is in a list (e.g., `!Api.Signature:in(currentSignatures)` → true if signature not found)
- **Cross-document comparison** works automatically — cop aggregates data across all source files before evaluating `:select()` lets

## CI Integration

```bash
# In CI pipeline
cop run api-compat.cop
# Exit code 1 if any removed or added APIs found, 0 if clean
```

To fail only on breaking changes (removed APIs), export only the removal check:

```ruby
import csharp-api
import code-analysis

predicate baselineApi(Api) => publicApi && Api.File.Path:matches('[/\\\\]api[/\\\\]')
predicate sourceApi(Api) => publicApi && !Api.File.Path:matches('[/\\\\]api[/\\\\]')

let currentSignatures = Code.Api:csharp:sourceApi:select(Api.Signature)
predicate removedApi(Api) => baselineApi && !Api.Signature:in(currentSignatures)

export let breaking-changes = Code.Api:removedApi
    :toError('BREAKING: {Api.Signature}')
```
