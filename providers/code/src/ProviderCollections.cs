// Materializes pull-based provider collections without synchronous enumeration.

using Cop.Core;

namespace Cop.Providers;

/// <summary>Consumes deferred provider collections with cancellation-aware asynchronous reads.</summary>
public static class ProviderCollections
{
    /// <summary>Collects each named asynchronous stream in its original item order.</summary>
    public static async ValueTask<Dictionary<string, List<object>>> MaterializeAsync(
        DeferredProviderResult result, CancellationToken cancellationToken = default)
    {
        Dictionary<string, List<object>> collections = new(StringComparer.Ordinal);
        foreach ((string name, IAsyncEnumerable<object> stream) in result.Collections)
        {
            List<object> values = [];
            await foreach (object value in stream.WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                values.Add(value);
            }
            collections.Add(name, values);
        }
        return collections;
    }
}
