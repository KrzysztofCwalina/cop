# Streaming Provider Sample (s19-ProviderStreaming)

A minimal template demonstrating how to write a **streaming** Cop provider with `source()` and `sink()`.

## Structure

```
s19-ProviderStreaming/
├── streaming-provider.csproj   # Provider C# project
├── src/
│   └── TickerProvider.cs       # Streaming DataProvider implementation
└── package/                    # Cop package (ready to distribute)
    ├── ticker.md               # Package metadata
    ├── src/
    │   └── ticker.cop          # Type definitions with typed source/sink bindings
    └── lib/                    # Built DLLs (populated by build)
```

## Build

```bash
dotnet build
```

This compiles the provider and copies the DLL to `package/lib/` automatically.

## Test

Create a simple pipeline that processes ticks:

```cop
# test.cop
import ticker

function handle(Tick) => Ack {
    Sequence = Tick.Sequence,
    Status = 'processed'
}

async foreach Ticks => handle => Acks
```

Run with:

```bash
cop run test.cop
```

The ticker emits a Tick event every second. The pipeline transforms each Tick into an Ack and sends it to the sink. Press Ctrl+C to stop.

## Key Concepts

This sample illustrates:

1. **`source('ticker')`** — returns a `Source` handle (async stream of items)
2. **`sink('ticker')`** — returns a `Sink` handle (output target for processed items)
3. **Typed bindings** — `export let Ticks : [Tick] = source('ticker')` declares that the source produces `Tick` items
4. **Typed sink** — `export let Acks : [Ack] = sink('ticker')` declares that the sink accepts `Ack` items
5. **`async foreach`** — processes the infinite stream with a transform pipeline

## Customizing

Use this template to build your own streaming provider:

1. Implement `SourceProvider` and override `GetSchema()` + `QueryStream()`
2. Implement `SinkProvider` and override `Name` + `WriteAsync()`
3. Define types and typed source/sink bindings in your `.cop` package

See [Provider Guide](../../docs/provider-guide.md) for full documentation.
