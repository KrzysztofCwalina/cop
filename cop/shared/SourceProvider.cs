using System.Runtime.CompilerServices;

namespace Cop.Core;

/// <summary>
/// Abstract base class for streaming source providers.
/// A source provider yields items asynchronously (potentially infinite stream).
/// Examples: HTTP server yielding incoming requests, timer yielding tick events.
///
/// Provider packages contain subclasses of this. The engine discovers them at load time
/// and registers them as streaming sources accessible via source('namespace') in cop scripts.
/// </summary>
public abstract class SourceProvider
{
    /// <summary>
    /// Returns the provider schema as UTF-8 JSON.
    /// Describes the types this source produces.
    /// </summary>
    public abstract ReadOnlyMemory<byte> GetSchema();

    /// <summary>
    /// Returns an async enumerable of items. The stream may be infinite.
    /// Items should be <see cref="Cop.Lang.DataObject"/> instances or dictionaries.
    /// </summary>
    public abstract IAsyncEnumerable<object> QueryStream(
        ProviderQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Human-readable provider name, used in diagnostics.
    /// Default strips the "Source" suffix from the class name.
    /// </summary>
    public override string ToString()
    {
        var name = GetType().Name;
        return name.EndsWith("Source", StringComparison.Ordinal)
            ? name[..^"Source".Length]
            : name;
    }

    /// <summary>
    /// Returns namespace-scoped functions exposed by this source provider.
    /// Functions are async and accept a list of evaluated arguments.
    /// </summary>
    public virtual Dictionary<string, Func<List<object?>, Task<object?>>>? GetProviderFunctions() => null;
}
