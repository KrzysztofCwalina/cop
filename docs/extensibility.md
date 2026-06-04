# Extensibility

Cop is designed to be extended in two ways:

1. **Data Providers** — supply new data collections (types, APIs, metrics, anything) to `.cop` programs
2. **External Analyzers** — wrap existing analysis tools (Ruff, ESLint, etc.) so their results flow through cop's unified output

Both approaches produce packages that can be published, shared, and composed with other packages.

---

## Choosing a Runtime

| Runtime | Best for | Performance | Setup |
|---------|----------|-------------|-------|
| **CLR** (.NET) | High-performance, large datasets | Fastest (in-process binary) | Requires .NET SDK to build |
| **Python** | Wrapping Python tools, quick prototyping | Good (process + JSON) | Requires Python on target |
| **Node.js** | Wrapping JS tools, npm ecosystem | Good (process + JSON) | Requires Node.js on target |

---

## Python Provider

Create a package directory with `cop.json` and a Python entry script:

```
my-provider/
├── cop.json
└── src/
    ├── main.py
    └── my-provider.cop
```

**cop.json:**
```json
{
  "name": "my-provider",
  "version": "1.0.0",
  "title": "My Provider",
  "description": "Supplies Widget data for analysis",
  "provider": "python",
  "providerEntry": "src/main.py"
}
```

**src/main.py:**
```python
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', '..', '..', 'sdk', 'python'))
from cop_provider_sdk import define_provider

def get_schema():
    return {
        "types": [
            {
                "name": "Widget",
                "properties": [
                    {"name": "Name"},
                    {"name": "Category"},
                    {"name": "Weight", "type": "float"},
                    {"name": "IsActive", "type": "bool"},
                ],
            }
        ],
        "collections": [{"name": "Widgets", "itemType": "Widget"}],
    }

def query(params):
    root_path = params.get("rootPath", ".")
    # Load or compute your data here
    return {
        "Widgets": [
            {"Name": "Sprocket", "Category": "hardware", "Weight": 2.5, "IsActive": True},
        ]
    }

define_provider(schema=get_schema, query=query)
```

**src/my-provider.cop:**
```ruby
let data = provider('my-provider', nic)

export let Widgets = data.Widgets

type Widget = {
    Name : string,
    Category : string,
    Weight : float,
    IsActive : bool
}
```

---

## Node.js Provider

Same structure as Python, but use `"provider": "node"` in cop.json and a JavaScript entry:

**cop.json:**
```json
{
  "name": "my-provider",
  "version": "1.0.0",
  "title": "My Provider",
  "description": "Supplies Widget data for analysis",
  "provider": "node",
  "providerEntry": "src/index.js"
}
```

**src/index.js:**
```javascript
const { defineProvider } = require('@aspect/cop-provider-sdk');

defineProvider({
  schema: () => ({
    types: [
      {
        name: 'Widget',
        properties: [
          { name: 'Name' },
          { name: 'Category' },
          { name: 'Weight', type: 'float' },
          { name: 'IsActive', type: 'bool' },
        ],
      },
    ],
    collections: [{ name: 'Widgets', itemType: 'Widget' }],
  }),

  query: async (params) => {
    const { rootPath } = params;
    // ... load or compute your data ...
    return {
      Widgets: [
        { Name: 'Sprocket', Category: 'hardware', Weight: 2.5, IsActive: true },
      ],
    };
  },
});
```

The `query` function can be sync or async (return a Promise).

---

## CLR Provider (.NET)

For maximum performance, implement a .NET class that extends `ObjectProvider`. CLR providers run in-process with no serialization overhead.

### Package Structure

```
my-provider/
├── cop.json                # Package metadata (JSON)
├── src/
│   └── my-provider.cop     # Cop type definitions and predicates
└── lib/
    ├── my-provider.dll     # Your compiled provider
    └── my-provider.deps.json
```

### Package Metadata (`cop.json`)

```json
{
  "name": "my-provider",
  "version": "1.0.0",
  "title": "My Data Provider",
  "description": "Provides X, Y, Z collections for analysis",
  "authors": "your-name",
  "tags": ["relevant", "tags"],
  "provider": "clr",
  "providerEntry": "MyNamespace.MyProvider",
  "providerAssembly": "my-provider.dll"
}
```

Key fields:
- **`"provider": "clr"`** — tells the engine this package includes a .NET provider DLL
- **`"providerEntry"`** — the fully-qualified class name of your `ObjectProvider` subclass
- **`"providerAssembly"`** — the DLL filename (required when `lib/` contains multiple DLLs)

### Cop Type Definitions (`src/*.cop`)

Define types, predicates, and checks in `.cop` files. These define the user-facing API of your package:

```cop
## My Provider Package
## Provides collections for analyzing widgets.

let db = provider('my-provider')
export let Widgets = db.Widgets

type Widget = {
    Name : string,
    Category : string,
    Weight : float,
    IsActive : bool
}

predicate isHeavy(Widget) = Widget.Weight:gt(100)
```

### Provider DLL (`lib/`)

Place your compiled DLL and its `deps.json` file in `lib/`. Any third-party dependencies should also go here — the engine loads them automatically via the deps manifest.

### Implementing an ObjectProvider

The simplest provider returns a schema and object collections:

```csharp
using Cop.Core;

namespace MyNamespace;

public class MyProvider : ObjectProvider
{
    public override ObjectFormat SupportedFormats => ObjectFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema()
    {
        var schema = new ProviderSchema
        {
            Types =
            [
                new() { Name = "Widget", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Category" },
                    new() { Name = "Weight", Type = "float" },
                    new() { Name = "IsActive", Type = "bool" },
                ]}
            ],
            Collections =
            [
                new() { Name = "Widgets", ItemType = "Widget" }
            ]
        };
        return schema.ToJson();
    }

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query)
    {
        var widgets = new List<object>();

        foreach (var file in Directory.GetFiles(query.RootPath ?? ".", "*.widget"))
        {
            widgets.Add(new Dictionary<string, object?>
            {
                ["Name"] = Path.GetFileNameWithoutExtension(file),
                ["Category"] = "default",
                ["Weight"] = 42.0,
                ["IsActive"] = true,
            });
        }

        return new Dictionary<string, List<object>>
        {
            ["Widgets"] = widgets
        };
    }
}
```

### Schema

The schema describes what types and collections your provider exposes. The engine uses this to register types in the type system before querying data.

**`ProviderTypeSchema`** defines a type:
- `Name` — PascalCase type name
- `Base` — optional base type name (for inheritance)
- `Properties` — list of `ProviderPropertySchema`

**`ProviderPropertySchema`** defines a property:
- `Name` — PascalCase property name
- `Type` — `"string"` (default), `"int"`, `"float"`, `"bool"`, `"byte"`, `"bytes"`, or another type name
- `Optional` — `true` if the property may be null
- `Collection` — `true` if the property is a list

**`ProviderCollectionSchema`** defines a top-level collection:
- `Name` — PascalCase collection name (e.g., `"Widgets"`)
- `ItemType` — the type name of items in the collection

### Data Formats

Providers choose a data format via `SupportedFormats`:

| Format | Override Method | Use Case |
|--------|----------------|----------|
| `ObjectCollections` | `QueryCollections()` | CLR object data — simplest approach. Return `Dictionary<string, List<object>>` where objects are `Dictionary<string, object?>`. |
| `InMemoryDatabase` | `QueryData()` | High-performance binary format with stride-based `DataTable` records and shared UTF-8 string heap. Best for large datasets. |
| `Json` | `Query()` | Return raw UTF-8 JSON bytes. Useful when data is already in JSON format. |

For streaming (push-like) providers, use `StreamProvider` instead of `ObjectProvider` — see the Streaming Providers section below.

For most providers, **`ObjectCollections`** is the recommended format. It's the simplest to implement and performs well for typical dataset sizes.

### Runtime Bindings (Optional)

For CLR object data (not dictionaries), override `GetRuntimeBindings()` to tell the engine how to access properties on your objects:

```csharp
public override RuntimeBindings? GetRuntimeBindings()
{
    return new RuntimeBindings
    {
        ClrTypeMappings = new()
        {
            [typeof(WidgetInfo)] = "Widget",
        },
        Accessors = new()
        {
            ["Widget"] = new()
            {
                ["Name"] = obj => ((WidgetInfo)obj).Name,
                ["Category"] = obj => ((WidgetInfo)obj).Category,
                ["Weight"] = obj => ((WidgetInfo)obj).Weight,
                ["IsActive"] = obj => ((WidgetInfo)obj).IsActive,
            }
        }
    };
}
```

If you return `Dictionary<string, object?>` from `QueryCollections`, runtime bindings are not needed — the engine accesses dictionary keys directly.

### Provider Query

The `ProviderQuery` parameter gives context about what the engine needs:

- **`RootPath`** — the project root directory (the `-t` target path)
- **`RequestedCollections`** — which collections the engine needs (null = all). Skip expensive work for unrequested collections.
- **`ExcludedDirectories`** — directory names to skip during filesystem traversal (e.g., `.git`, `node_modules`, `bin`, `obj`)
- **`Filter`** — optional pushdown filter for query optimization (providers can ignore this)
- **`Options`** — extensible key-value options

### Provider Functions (Optional)

Expose callable functions to `.cop` programs:

```csharp
public override Dictionary<string, Func<List<object?>, Task<object?>>>? GetProviderFunctions()
{
    return new()
    {
        ["Compute"] = async args =>
        {
            var input = args[0]?.ToString() ?? "";
            return await Task.FromResult<object?>(input.Length);
        }
    };
}
```

Functions are registered under the provider's import namespace (e.g., if the package is imported as `my-provider`, call `my-provider.Compute('hello')`).

### Setting Up the .csproj

#### In-Repo Development

If you're developing alongside the Cop source:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\cop\cli\cop.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>
</Project>
```

> **Important:** Set `<Private>false</Private>` on the ProjectReference. This prevents `cop.dll` from being copied to your output — at runtime, your DLL loads into the Cop process and shares its types.

#### Standalone Development

If you're building outside the Cop repo, reference the installed `cop.exe` (or `cop.dll` from a build):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="cop">
      <HintPath>$(HOME)/.cop/bin/cop.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

#### Auto-Copy to Package

Add an MSBuild target to copy your built DLL into the package `lib/` directory:

```xml
<Target Name="CopyToPackageLib" AfterTargets="Build">
  <PropertyGroup>
    <PackageLibDir>$(MSBuildThisFileDirectory)..\my-package\lib\</PackageLibDir>
  </PropertyGroup>
  <MakeDir Directories="$(PackageLibDir)" />
  <ItemGroup>
    <ProviderFiles Include="$(OutputPath)my-provider.dll" />
    <ProviderFiles Include="$(OutputPath)my-provider.deps.json" />
  </ItemGroup>
  <Copy SourceFiles="@(ProviderFiles)" DestinationFolder="$(PackageLibDir)" SkipUnchangedFiles="true" />
</Target>
```

### Writing a Source Code Provider

To add support for a new programming language (e.g., Haskell), extend the code model infrastructure.

#### Implementing ISourceParser

```csharp
using Cop.Providers.SourceModel;

namespace MyNamespace;

public class HaskellParser : ISourceParser
{
    public string[] Extensions => [".hs"];
    public string Language => "haskell";

    public SourceFile Parse(string filePath, string sourceText)
    {
        var sourceFile = new SourceFile { FilePath = filePath };

        sourceFile.Types.Add(new TypeDeclaration
        {
            Name = "MyType",
            Kind = "Class",
            Namespace = "MyModule",
        });

        sourceFile.Methods.Add(new MethodDeclaration
        {
            Name = "myFunction",
            ReturnType = "Int",
        });

        var lines = sourceText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            sourceFile.Lines.Add(new LineInfo
            {
                LineNumber = i + 1,
                Text = lines[i],
                Kind = lines[i].TrimStart().StartsWith("--") ? "comment" : "code"
            });
        }

        return sourceFile;
    }
}
```

#### Code Provider with ISourceParser

```csharp
using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace MyNamespace;

public class HaskellProvider : ObjectProvider
{
    public override ObjectFormat SupportedFormats => ObjectFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema() => CodeSchema.GetJson();

    public override RuntimeBindings? GetRuntimeBindings() => CodeBindings.Build();

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query)
    {
        var parsers = new SourceParserRegistry();
        parsers.Register(new HaskellParser());
        parsers.Register(new TextFileParser());

        return CodeCollectionBuilder.CollectAndParse(query, parsers);
    }
}
```

The `CodeSchema` and `CodeBindings` classes provide the shared code model schema (Types, Methods, Statements, Lines, Files, etc.) that all language providers use. The `CodeCollectionBuilder` handles file discovery, parallel parsing, and collection assembly.

#### Available Code Model Types

| Type | Description |
|------|-------------|
| `Type` | Type declarations (classes, interfaces, enums, structs) |
| `Method` | Method/function declarations |
| `Statement` | Individual statements within methods |
| `Line` | Source lines with metadata |
| `File` | Source file information |
| `Field` | Field/property declarations |
| `Event` | Event declarations |
| `Api` | Public API surface items |

Collections are named `Code.Types`, `Code.Methods`, `Code.Statements`, `Code.Lines`, `Code.Files`, etc. Language packages expose these via explicit exports:

```cop
let cb : Codebase = provider('haskell')
export let Types = cb.Types
export let Methods = cb.Methods
export let Lines = cb.Lines
export let Files = cb.Files
```

### Streaming Providers (Source & Sink)

For push-like providers that yield items indefinitely (e.g., HTTP servers, message queues, timers), use the separate `StreamProvider` and `SinkProvider` base classes instead of `ObjectProvider`.

#### StreamProvider

```csharp
using System.Runtime.CompilerServices;
using Cop.Core;
using Cop.Lang;

public class MySource : StreamProvider
{
    public override ReadOnlyMemory<byte> GetSchema()
    {
        var schema = new ProviderSchema
        {
            Types = [new ProviderTypeSchema { Name = "Event", Properties = [...] }],
            Collections = [new ProviderCollectionSchema { Name = "Events", ItemType = "Event" }]
        };
        return schema.ToJson();
    }

    public override async IAsyncEnumerable<object> QueryStream(
        ProviderQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var item = new DataObject("Event");
            item.Set("Message", "hello");
            yield return item;
            await Task.Delay(1000, cancellationToken);
        }
    }
}
```

#### SinkProvider

```csharp
using Cop.Core;

public class MySink : SinkProvider
{
    public override string Name => "Output";

    public override Task WriteAsync(object? originalItem, object result)
    {
        Console.WriteLine(result.ToString());
        return Task.CompletedTask;
    }
}
```

#### Cop Package Usage

```cop
import my-streaming-package

export let Events : [Event] = source('my-streaming-package')
export let OUTPUT : [Result] = sink('my-streaming-package')
```

The engine discovers all `StreamProvider` and `SinkProvider` subclasses in the provider DLL automatically. They are registered under the package namespace.

### Testing Your Provider

#### Local Testing

1. Build your provider DLL
2. Copy it to your package's `lib/` directory
3. Create a test `.cop` file:

```cop
import my-provider

foreach Widgets
    '{Widget.Name} ({Widget.Category}): {Widget.Weight}'
```

4. Run: `cop test.cop -t /path/to/test/data`

#### Unit Testing

Reference your provider in a test project and verify schema and data:

```csharp
[Test]
public void Schema_ReturnsValidTypes()
{
    var provider = new MyProvider();
    var schema = ProviderSchema.FromJson(provider.GetSchema());

    Assert.That(schema.Types, Has.Count.GreaterThan(0));
    Assert.That(schema.Collections, Has.Count.GreaterThan(0));
}

[Test]
public void Query_ReturnsData()
{
    var provider = new MyProvider();
    var query = new ProviderQuery { RootPath = "/path/to/test/data" };
    var collections = provider.QueryCollections(query);

    Assert.That(collections, Is.Not.Null);
    Assert.That(collections!["Widgets"], Has.Count.GreaterThan(0));
}
```

### Publishing Your Provider

#### As a GitHub Repository

Cop packages can be hosted as directories in GitHub repositories. Set up a feed:

```bash
cop package feed add https://github.com/your-org/your-packages
```

The repository should contain your package directory at the root or under a subdirectory. Users install with:

```bash
cop package restore
```

after adding `import my-provider` to their `.cop` files.

#### Package Directory Layout

For a GitHub-hosted package feed, the repository structure looks like:

```
your-packages/
└── my-provider/
    ├── my-provider.md
    ├── src/
    │   └── my-provider.cop
    └── lib/
        ├── my-provider.dll
        └── my-provider.deps.json
```

---

## External Analyzers

An external analyzer package wraps an existing tool (Ruff, ESLint, Pylint, etc.) so its output flows through cop. The analyzer runs as-is — cop doesn't reimplement its rules. The package translates the tool's native output into cop's unified format.

### Why Wrap an External Tool?

- **Unified output** — all findings (from cop rules, Ruff, ESLint, etc.) appear in the same `file(line): severity: message` format
- **Composability** — combine external findings with native cop checks using `+`
- **Filtering** — use `.cop` predicates to exclude rules or paths
- **Aggregation** — run multiple tools in one command and get a single report

### Architecture

An external analyzer package is just a Python (or Node.js) provider that:

1. Runs the external tool as a subprocess
2. Parses its structured output (JSON, SARIF, etc.)
3. Returns the results as a typed collection (e.g., `Violations`)

```
┌──────────────┐       ┌──────────────┐       ┌──────────────┐
│  cop engine  │──────▶│  provider.py │──────▶│  ruff/eslint │
│              │◀──────│  (adapter)   │◀──────│  (tool)      │
└──────────────┘ JSON  └──────────────┘ JSON  └──────────────┘
```

### Example: Ruff (Python Linter)

The `python-ruff` package demonstrates the pattern:

**cop.json:**
```json
{
  "name": "python-ruff",
  "version": "1.0.0",
  "title": "Ruff Python Linter",
  "description": "Runs ruff and exposes violations",
  "provider": "python",
  "providerEntry": "src/main.py"
}
```

**src/main.py:**
```python
import json, os, subprocess, sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', '..', '..', 'sdk', 'python'))
from cop_provider_sdk import define_provider

def get_schema():
    return {
        "types": [
            {
                "name": "Violation",
                "properties": [
                    {"name": "File"},
                    {"name": "Line", "type": "int"},
                    {"name": "Severity"},
                    {"name": "Message"},
                    {"name": "Source"},
                ],
            }
        ],
        "collections": [{"name": "Violations", "itemType": "Violation"}],
    }

def query(params):
    root_path = params.get("rootPath") or os.getcwd()

    result = subprocess.run(
        ["ruff", "check", "--output-format=json", "."],
        capture_output=True, text=True, encoding="utf-8",
        cwd=root_path,
    )

    if result.returncode not in (0, 1):
        return {"Violations": []}

    ruff_results = json.loads(result.stdout)
    violations = []

    for item in ruff_results:
        file_path = item.get("filename", "")
        if os.path.isabs(file_path):
            file_path = os.path.relpath(file_path, root_path)

        rule_code = item.get("code", "")
        severity = "error" if rule_code.startswith("E") else "warning"
        location = item.get("location", {})

        violations.append({
            "File": file_path.replace("\\", "/"),
            "Line": location.get("row", 0),
            "Severity": severity,
            "Message": f"{rule_code}: {item.get('message', '')}",
            "Source": "ruff",
        })

    return {"Violations": violations}

define_provider(schema=get_schema, query=query)
```

**src/python-ruff.cop:**
```ruby
import code-analysis

let data = provider('python-ruff', nic)

export let ruff-checks = data.Violations

command MAIN = CHECK(ruff-checks)
```

### Running It

```bash
# Run ruff checks on a Python project
cop python-ruff -t path/to/project

# Output:
# src/app.py(1): warning: F401: `os` imported but unused
# src/app.py(9): error: E711: Comparison to `None` should be `cond is None`
```

### Creating Your Own Analyzer Package

To wrap a different tool, follow the same pattern:

1. **Create the package directory** with `cop.json` declaring `"provider": "python"` (or `"node"`)
2. **Write the provider script** that:
   - Defines a schema with a `Violation` type (File, Line, Severity, Message, Source)
   - Runs the tool via `subprocess.run` with JSON/structured output
   - Parses the output and maps it to your schema
3. **Write the `.cop` file** that exports the collection and defines a `command MAIN`

Key considerations:

- **Use `encoding="utf-8"`** in subprocess calls (avoids encoding errors on Windows)
- **Use structured output** from the tool (JSON, SARIF) — don't parse human-readable text
- **Map severity** to `"error"`, `"warning"`, or `"info"` for consistency
- **Use relative paths** in `File` for portable output
- **Add `Source`** — the tool name (e.g., `"ruff"`, `"eslint"`) for filtering
- **Handle tool not installed** gracefully (return empty collection, log to stderr)

### Composing Multiple Analyzers

```ruby
import python-ruff
import python-checks
import code-analysis

# All findings from both ruff and native cop checks
let all-checks = ruff-checks + python-checks

command MAIN = CHECK(all-checks)
```

### Performance at Scale

External analyzer packages handle large repos well because the heavy lifting is done by purpose-built tools:

| Repo | Files | Findings | Time |
|------|-------|----------|------|
| azure-sdk-for-python | 55,000+ | 52,625 | ~21s |

The bottleneck is typically the external tool itself, not cop. Ruff is written in Rust and processes thousands of files per second.

---

## Protocol Reference

All external providers (Python, Node.js) communicate with the cop engine via **length-prefixed JSON messages** over stdin/stdout (the same framing used by LSP):

```
Content-Length: <byte_count>\r\n
\r\n
<json_payload>
```

The provider process stays alive across requests within a single Cop session.

### GetSchema

Sent once at load time. Provider must return its type and collection definitions.

**Request:**
```json
{"method": "getSchema"}
```

**Response:**
```json
{
  "types": [
    {
      "name": "Violation",
      "properties": [
        {"name": "File"},
        {"name": "Line", "type": "int"},
        {"name": "Severity"},
        {"name": "Message"},
        {"name": "Source"}
      ]
    }
  ],
  "collections": [
    {"name": "Violations", "itemType": "Violation"}
  ]
}
```

### Query

Sent when the engine needs collection data.

**Request:**
```json
{
  "method": "query",
  "params": {
    "rootPath": "/path/to/project",
    "requestedCollections": ["Violations"],
    "excludedDirectories": [".git", "node_modules", "bin"]
  }
}
```

**Response:**
```json
{
  "Violations": [
    {
      "File": "src/app.js",
      "Line": 10,
      "Severity": "warning",
      "Message": "no-unused-vars: 'x' is defined but never used",
      "Source": "eslint"
    }
  ]
}
```

### Error Handling

- If the provider process crashes, the engine reports the error and stderr output.
- If a response contains an `"error"` field, it's treated as a provider error.
- If the provider doesn't respond within 60 seconds, the engine times out.
- If the runtime (node/python) is not installed, the engine reports a helpful error.

### Debugging

Provider stderr is captured and included in error messages. Use stderr for debug logging:

```javascript
// Node.js
console.error('Debug: processing', filePath);
```

```python
# Python
import sys
print("Debug: processing", file_path, file=sys.stderr)
```

---

## Reference

### Key Namespaces (CLR)

| Namespace | Contains |
|-----------|----------|
| `Cop.Core` | `ObjectProvider`, `StreamProvider`, `SinkProvider`, `ProviderSchema`, `ProviderQuery`, `RuntimeBindings`, `ObjectFormat` |
| `Cop.Providers.SourceModel` | `SourceFile`, `TypeDeclaration`, `MethodDeclaration`, `ISourceParser` |
| `Cop.Providers.SourceParsers` | `CodeCollectionBuilder`, `CodeSchema`, `CodeBindings`, `TextFileParser` |

### Naming Conventions

- **Type names**: PascalCase (e.g., `Widget`, `TspModel`)
- **Property names**: PascalCase (e.g., `Name`, `IsActive`)
- **Collection names**: PascalCase (e.g., `Widgets`, `Code.Types`)
- **Predicates and functions** (in `.cop` files): camelCase (e.g., `isHeavy`, `computeScore`)

### Property Types

| Type String | CLR Type | JSON | Description |
|-------------|----------|------|-------------|
| `"string"` | `string` | string | Text (default if omitted) |
| `"int"` | `long` | number (integer) | 64-bit integer |
| `"float"` | `double` | number (float) | 64-bit floating-point |
| `"bool"` | `bool` | boolean | Boolean |
| `"byte"` | `byte` | — | Single byte |
| `"bytes"` | `byte[]` | — | Binary data |

Properties can also be `optional: true` or `collection: true` (array of items).

### SDKs

| Runtime | SDK | Install |
|---------|-----|---------|
| Python | `cop_provider_sdk` | `pip install ../../sdk/python` or add to `sys.path` |
| Node.js | `@aspect/cop-provider-sdk` | `npm install ../../sdk/node/cop-provider-sdk` |

### Canonical Examples

| Provider | Runtime | Location |
|----------|---------|----------|
| ESLint | Node.js | `providers/eslint-provider/` |
| Ruff | Python | `providers/ruff-provider/` |
