# API Diff

Compare the current public API surface against a saved baseline to detect breaking changes (removed APIs) and additions.

## How It Works

1. **Generate a baseline** — extract the current API surface to a text file and check it into source control
2. **Run diff checks** — cop compares the current source against the baseline and reports removed or added APIs

The baseline file is a plain text file with one [canonical signature](api-listing.md#signature-format) per line. Cop reads it through the `Code.Lines` collection — no special format or tooling needed.

## Setup

Create a file called `api-compat.cop` in your project:

```ruby
import csharp-api
import code-analysis

# Which file is the baseline (customize the path pattern to match your repo)
predicate baselineLine(Line) => Line.File.Path:matches('api-baseline') && Line.Text:matches('\\S')

# Export command to generate/update the baseline
export command api-export = SAVE('api-baseline.txt', '{Api.Signature}', Code.Api:csharp:publicApi)

# Build lookup lists from current source and baseline
let currentSignatures = Code.Api:csharp:publicApi:select(Api.Signature)
let baselineSignatures = Code.Lines:baselineLine:select(Line.Text)

# Diff predicates
predicate removedApi(Line) => baselineLine && !Line.Text:in(currentSignatures)
predicate addedApi(Api) => publicApi && !Api.Signature:in(baselineSignatures)

# Report results
export let api-removed = Code.Lines:removedApi
    :toError('API REMOVED (breaking): {Line.Text}')
export let api-added = Code.Api:csharp:addedApi
    :toInfo('API ADDED: {Api.Signature}')
```

## Step 1: Generate the Baseline

```bash
cop run api-compat.cop api-export
```

This scans your C# source files and writes `api-baseline.txt`:

```
class MyClient
class MyClientOptions
ctor MyClient(Uri, TokenCredential, MyClientOptions)
ctor MyClient()
method MyClient.GetItemAsync(string, CancellationToken) : Task<Response>
method MyClient.DeleteItemAsync(string, CancellationToken) : Task<Response>
```

Check this file into source control.

## Step 2: Run the Diff

After making changes to your source, run:

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

## Step 3: Update the Baseline

When you intentionally change the API, regenerate the baseline:

```bash
cop run api-compat.cop api-export
```

Review the diff in source control and commit.

## Customization

Everything in `api-compat.cop` is policy you control:

| What | How | Example |
|---|---|---|
| **Baseline filename** | Change the `SAVE` path | `SAVE('ApiSurface.txt', ...)` |
| **Baseline path pattern** | Change `baselineLine` predicate | `Line.File.Path:matches('ApiSurface')` |
| **Severity of removed APIs** | Use `toError`, `toWarning`, or `toInfo` | `:toWarning('...')` |
| **Severity of added APIs** | Same | `:toError('...')` |
| **Scope** | Add predicates to filter | Only check certain types or namespaces |

### Example: Custom Baseline Path

For a repo that stores baselines at `sdk/{service}/api/ApiSurface.txt`:

```ruby
import csharp-api
import code-analysis

predicate baselineLine(Line) => Line.File.Path:matches('ApiSurface') && Line.Text:matches('\\S')
export command api-export = SAVE('api/ApiSurface.txt', '{Api.Signature}', Code.Api:csharp:publicApi)

let currentSignatures = Code.Api:csharp:publicApi:select(Api.Signature)
let baselineSignatures = Code.Lines:baselineLine:select(Line.Text)

predicate removedApi(Line) => baselineLine && !Line.Text:in(currentSignatures)
predicate addedApi(Api) => publicApi && !Api.Signature:in(baselineSignatures)

export let api-removed = Code.Lines:removedApi:toError('BREAKING: {Line.Text}')
export let api-added = Code.Api:csharp:addedApi:toWarning('ADDED: {Api.Signature}')
```

## Key Concepts

- **`:select()`** projects a collection to a list of strings (e.g., `Code.Api:publicApi:select(Api.Signature)` → list of signature strings)
- **`:in()`** checks if a value is in a list (e.g., `!Line.Text:in(currentSignatures)` → true if the line text is not found in the current signatures)
- **`baselineLine`** is a predicate you define — it decides which lines in which files are considered baseline entries
- **Cross-document comparison** works automatically — cop aggregates data across all source files before evaluating `:select()` lets

## CI Integration

```bash
# In CI pipeline
cop run api-compat.cop
# Exit code 1 if any removed or added APIs found, 0 if clean
```

To fail only on breaking changes (removed APIs), split into separate checks:

```ruby
# ci-checks.cop
import csharp-api
import code-analysis

predicate baselineLine(Line) => Line.File.Path:matches('api-baseline') && Line.Text:matches('\\S')

let currentSignatures = Code.Api:csharp:publicApi:select(Api.Signature)
predicate removedApi(Line) => baselineLine && !Line.Text:in(currentSignatures)

export let breaking-changes = Code.Lines:removedApi
    :toError('BREAKING: {Line.Text}')
```

```bash
cop run ci-checks.cop
# Exits 1 only if there are breaking changes
```
