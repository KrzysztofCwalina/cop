// Enumerates interpreter collections after resolving deferred values.

using Cop.Core;
using System.Runtime.CompilerServices;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Enumerates interpreter collections after resolving deferred values.
/// </summary>
internal static class CollectionValues
{
    /// <summary>
    /// Enumerates eager or deferred collection values without replacing scalar values.
    /// </summary>
    public static async IAsyncEnumerable<CopValue> EnumerateAsync(CopValue value,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (value is CopThunk thunk)
        {
            value = await thunk.ForceAsync(cancellationToken).ConfigureAwait(false);
        }

        IAsyncEnumerable<CopValue> items = value switch
        {
            CopList list => FromEnumerable(list.Items, cancellationToken),
            CopLazyCollection lazy => lazy.EnumerateAsync(cancellationToken),
            CopQueryable query => query.EnumerateAsync(cancellationToken),
            CopQueryableProperty property => property.Source.EnumerateAsync(cancellationToken),
            CopNull => FromEnumerable([], cancellationToken),
            _ => FromEnumerable([value], cancellationToken)
        };
        await foreach (CopValue item in items.WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    internal static async IAsyncEnumerable<CopValue> FromEnumerable(
        IEnumerable<CopValue> values,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ValueTask.CompletedTask;
        foreach (CopValue value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }
}
