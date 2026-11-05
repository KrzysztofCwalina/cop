// Finite named collections whose items are produced only as the host requests them.

using System.Text.Json;
using Cop.Lang;

namespace Cop.Core;

/// <summary>Finite named collections whose items are produced only as the host requests them.</summary>
public sealed class DeferredProviderResult(IReadOnlyDictionary<string, IEnumerable<object>> collections)
{
    public IReadOnlyDictionary<string, IEnumerable<object>> Collections { get; } = collections;
}
