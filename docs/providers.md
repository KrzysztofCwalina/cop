# Writing a Cop Provider

This guide explains how to write a new data provider for Cop as a standalone plugin. A provider supplies typed data collections to `.cop` programs — for example, parsed source code, API definitions, or any domain-specific data.

Provider packages are **true plugins**: you can build and distribute a provider without modifying the Cop repo.

## Overview

A provider consists of two parts:

1. **A .NET class library** (DLL) containing a C# class that extends `ObjectProvider`
2. **A Cop package** (directory) with metadata, `.cop` type definitions, and the provider DLL

At runtime, the Cop engine discovers your package, loads the DLL, calls your provider to get schema and data, and makes the data available to `.cop` programs via `import`.

## Package Structure

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

The metadata file declares the package as JSON:

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

let db = object('my-provider')
export let Widgets = db.Widgets

type Widget = {
    Name : string,
    Category : string,
    Weight : number,
    IsActive : bool
}

predicate isHeavy(Widget) = Widget.Weight:gt(100)
```

### Provider DLL (`lib/`)

Place your compiled DLL and its `deps.json` file in `lib/`. Any third-party dependencies should also go here — the engine loads them automatically via the deps manifest.

## Implementing an ObjectProvider

### Minimal Provider

The simplest provider returns a schema and object collections:

```csharp
using Cop.Core;

namespace MyNamespace;

public class MyProvider : ObjectProvider
{
    // Use ObjectCollections for CLR object data
    public override ObjectFormat SupportedFormats => ObjectFormat.ObjectCollections;

    // Return the schema describing your types and collections
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
                    new() { Name = "Weight", Type = "number" },
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

    // Return data as CLR object dictionaries
    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query)
    {
        var widgets = new List<object>();

        // Populate from files, APIs, databases, etc.
        // Use query.RootPath for the project directory
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
- `Type` — `"string"` (default), `"int"`, `"number"`, `"bool"`, `"byte"`, `"bytes"`, or another type name
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

For streaming (push-like) providers, use `SourceProvider` instead of `ObjectProvider` — see the Streaming Providers section below.

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

## Setting Up the .csproj

### In-Repo Development

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

### Standalone Development

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

### Auto-Copy to Package

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
    <!-- Include third-party dependencies if any -->
  </ItemGroup>
  <Copy SourceFiles="@(ProviderFiles)" DestinationFolder="$(PackageLibDir)" SkipUnchangedFiles="true" />
</Target>
```

## Writing a Source Code Provider

To add support for a new programming language (e.g., Haskell), extend the code model infrastructure.

### Implementing ISourceParser

```csharp
using Cop.Providers.SourceModel;

namespace MyNamespace;

public class HaskellParser : ISourceParser
{
    // File extensions this parser handles
    public string[] Extensions => [".hs"];

    // Language identifier
    public string Language => "haskell";

    // Parse a source file into the code model
    public SourceFile Parse(string filePath, string sourceText)
    {
        var sourceFile = new SourceFile { FilePath = filePath };

        // Parse type declarations
        sourceFile.Types.Add(new TypeDeclaration
        {
            Name = "MyType",
            Kind = "Class",
            Namespace = "MyModule",
            // ... set properties from parsed source
        });

        // Parse functions as methods
        sourceFile.Methods.Add(new MethodDeclaration
        {
            Name = "myFunction",
            ReturnType = "Int",
            // ...
        });

        // Parse lines for line-level analysis
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

### Code Provider with ISourceParser

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

### Available Code Model Types

The code model includes:

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
let cb : Codebase = object('haskell')
export let Types = cb.Types
export let Methods = cb.Methods
export let Lines = cb.Lines
export let Files = cb.Files
```

## Testing Your Provider

### Local Testing

1. Build your provider DLL
2. Copy it to your package's `lib/` directory
3. Create a test `.cop` file:

```cop
import my-provider

foreach Widgets   # Widgets is exported by the package via: export let Widgets = object('my-provider').Widgets
    '{Widget.Name} ({Widget.Category}): {Widget.Weight}'
```

4. Run: `cop test.cop -t /path/to/test/data`

### Unit Testing

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

## Publishing Your Provider

### As a GitHub Repository

Cop packages can be hosted as directories in GitHub repositories. Set up a feed:

```bash
cop package feed add https://github.com/your-org/your-packages
```

The repository should contain your package directory at the root or under a subdirectory. Users install with:

```bash
cop package restore
```

after adding `import my-provider` to their `.cop` files.

### Package Directory Layout

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

## Streaming Providers (Source & Sink)

For push-like providers that yield items indefinitely (e.g., HTTP servers, message queues, timers), use the separate `SourceProvider` and `SinkProvider` base classes instead of `ObjectProvider`.

### SourceProvider

```csharp
using System.Runtime.CompilerServices;
using Cop.Core;
using Cop.Lang;

public class MySource : SourceProvider
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

### SinkProvider

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

### Cop Package Usage

```cop
import my-streaming-package

export let Events : [Event] = source('my-streaming-package')
export let OUTPUT : [Result] = sink('my-streaming-package')
```

The engine discovers all `SourceProvider` and `SinkProvider` subclasses in the provider DLL automatically. They are registered under the package namespace.

## Reference

### Key Namespaces

| Namespace | Contains |
|-----------|----------|
| `Cop.Core` | `ObjectProvider`, `SourceProvider`, `SinkProvider`, `ProviderSchema`, `ProviderQuery`, `RuntimeBindings`, `ObjectFormat` |
| `Cop.Providers.SourceModel` | `SourceFile`, `TypeDeclaration`, `MethodDeclaration`, `ISourceParser` |
| `Cop.Providers.SourceParsers` | `CodeCollectionBuilder`, `CodeSchema`, `CodeBindings`, `TextFileParser` |

### Naming Conventions

- **Type names**: PascalCase (e.g., `Widget`, `TspModel`)
- **Property names**: PascalCase (e.g., `Name`, `IsActive`)
- **Collection names**: PascalCase (e.g., `Widgets`, `Code.Types`)
- **Predicates and functions** (in `.cop` files): camelCase (e.g., `isHeavy`, `computeScore`)

### Property Types

| Type String | CLR Type | Description |
|-------------|----------|-------------|
| `"string"` | `string` | Text (default if omitted) |
| `"int"` | `long` | 64-bit integer |
| `"number"` | `double` | 64-bit floating-point |
| `"bool"` | `bool` | Boolean |
| `"byte"` | `byte` | Single byte |
| `"bytes"` | `byte[]` | Binary data |
