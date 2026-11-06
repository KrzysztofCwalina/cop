# Sample Provider (s18-Provider)

A minimal template demonstrating how to write a Cop data provider as a standalone plugin.

## Structure

```
s18-Provider/
├── sample-provider.csproj   # Provider C# project
├── src/
│   └── SampleProvider.cs    # DataProvider implementation
└── package/                 # Cop package (ready to distribute)
    ├── cop.json             # Package metadata
    ├── sample.md            # Package documentation
    ├── src/
    │   └── sample.cop       # Type definitions and predicates
    └── lib/                 # Built DLLs (populated by build)
```

## Build

```bash
dotnet build
```

This compiles the provider and copies the DLL to `package/lib/` automatically.

## Test

1. Create test data:

```bash
mkdir testdata
echo "name=Foo" > testdata/foo.widget
echo "name=Bar\ncategory=ui\nsize=200" > testdata/bar.widget
```

2. Copy (or symlink) the `package/` directory to your cop packages location, then run:

```bash
cop test.cop -t testdata
```

Where `test.cop` contains:

```cop
import sample
foreach Widgets => '{item.Name} ({item.Category}) size={item.Size}'
```

## Customizing

Use this template as a starting point for your own provider:

1. Rename the project, namespace, and class
2. Define your schema types in `GetSchema()`
3. Implement data collection in `QueryAsync(ProviderQuery, CancellationToken)`,
   awaiting I/O and forwarding the cancellation token. Return `DataObject` records
   with the declared type name so their fields are accessible without CLR bindings.
4. Update `package/cop.json` metadata, `package/*.md` documentation, and `package/src/*.cop` type definitions
5. Build and distribute the `package/` directory

See [Extensibility Guide](../../docs/extensibility.md) for full documentation.
