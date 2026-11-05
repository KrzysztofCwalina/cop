// Enumerates interpreter collections after resolving deferred values.

using Cop.Core;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Enumerates interpreter collections after resolving deferred values.
/// </summary>
internal static class CollectionValues
{
    public static IEnumerable<CopValue> Enumerate(CopValue value)
    {
        while (value is CopThunk thunk)
        {
            value = thunk.Force();
        }

        return value switch
        {
            CopList list => list.Items,
            CopLazyCollection lazy => lazy.Enumerate(),
            CopQueryable query => query.Enumerate(),
            CopQueryableProperty property => property.Source.Enumerate(),
            CopNull => [],
            _ => [value]
        };
    }
}
