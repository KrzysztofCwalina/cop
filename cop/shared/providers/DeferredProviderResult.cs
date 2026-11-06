// Finite named collections whose items are produced only as the host requests them.

using System.Text.Json;
using Cop.Lang;

namespace Cop.Core;

/// <summary>Finite named collections whose items are produced only as the host requests them.</summary>
public sealed class DeferredProviderResult(
    IReadOnlyDictionary<string, IAsyncEnumerable<object>> collections)
{
    /// <summary>
    /// The collection streams available for deferred provider enumeration.
    /// </summary>
    public IReadOnlyDictionary<string, IAsyncEnumerable<object>> Collections { get; } = collections;
}
