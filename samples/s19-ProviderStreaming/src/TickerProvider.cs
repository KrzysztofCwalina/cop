using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cop.Core;
using Cop.Lang;

namespace StreamingProvider;

/// <summary>
/// A minimal streaming source provider that demonstrates the source/sink pattern.
/// Emits "Tick" events at a regular interval.
///
/// To use in a .cop file:
///   import ticker
///   async foreach Ticks => handle => Acks
/// </summary>
public class TickerSource : SourceProvider
{
    private int _tickCount;

    public override ReadOnlyMemory<byte> GetSchema()
    {
        var schema = new ProviderSchema
        {
            Types =
            [
                new ProviderTypeSchema
                {
                    Name = "Tick",
                    Properties =
                    [
                        new ProviderPropertySchema { Name = "Sequence", Type = "int" },
                        new ProviderPropertySchema { Name = "Timestamp" },
                        new ProviderPropertySchema { Name = "Message" },
                    ]
                },
            ],
            Collections =
            [
                new ProviderCollectionSchema { Name = "Ticks", ItemType = "Tick" }
            ]
        };
        return schema.ToJson();
    }

    public override async IAsyncEnumerable<object> QueryStream(
        ProviderQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var seq = Interlocked.Increment(ref _tickCount);
            var tick = new DataObject("Tick");
            tick.Set("Sequence", seq);
            tick.Set("Timestamp", DateTime.UtcNow.ToString("o"));
            tick.Set("Message", $"Tick #{seq}");
            yield return tick;

            try { await Task.Delay(1000, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>
/// Sink that receives processed results from the pipeline.
/// Prints acknowledgments to stdout.
/// </summary>
public class AckSink : SinkProvider
{
    public override string Name => "Acks";

    public override Task WriteAsync(object? originalItem, object result)
    {
        if (result is DataObject so)
        {
            var seq = so.GetField("Sequence") is int s ? s : 0;
            var status = so.GetField("Status")?.ToString() ?? "ok";
            Console.WriteLine($"[Ack] seq={seq} status={status}");
        }
        else if (result is ErrorValue err)
        {
            Console.Error.WriteLine($"[Ack ERROR] {err.GetField("Message")}");
        }
        else
        {
            Console.WriteLine($"[Ack] {result}");
        }
        return Task.CompletedTask;
    }
}
