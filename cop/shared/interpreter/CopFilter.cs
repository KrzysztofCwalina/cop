// Evaluates provider filters after awaiting only the members demanded by each branch.

using Cop.Core;

namespace Cop.Lang.Interpreter;

/// <summary>Evaluates provider filters against asynchronously prepared language values.</summary>
internal static class CopFilter
{
    /// <summary>
    /// Tests a provider filter against a value, awaiting only fields and collection
    /// elements demanded by the filter and short-circuiting logical branches.
    /// </summary>
    /// <param name="filter">The provider filter to evaluate.</param>
    /// <param name="item">The language value to test.</param>
    /// <param name="cancellationToken">Cancels field preparation and collection enumeration.</param>
    /// <returns>Whether the value satisfies the filter.</returns>
    public static async ValueTask<bool> MatchesAsync(FilterExpression filter, CopValue item,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (filter)
        {
            case AndFilter and:
                foreach (FilterExpression condition in and.Conditions)
                {
                    if (!await MatchesAsync(condition, item, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
                return true;
            case OrFilter or:
                foreach (FilterExpression condition in or.Conditions)
                {
                    if (await MatchesAsync(condition, item, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }
                }
                return false;
            case NotFilter not:
                return !await MatchesAsync(not.Inner, item, cancellationToken).ConfigureAwait(false);
            case CollectionAnyFilter any:
                CopValue collection = await FieldAsync(item, any.Property, cancellationToken)
                    .ConfigureAwait(false);
                await foreach (CopValue child in CollectionValues.EnumerateAsync(collection,
                    cancellationToken).ConfigureAwait(false))
                {
                    if (await MatchesAsync(any.ItemFilter, child, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }
                }
                return false;
        }

        string? property = filter switch
        {
            PropertyFilter value => value.Property,
            StringOpFilter value => value.Property,
            ComparisonFilter value => value.Property,
            InFilter value => value.Property,
            ContainsAnyFilter value => value.Property,
            CollectionContainsFilter value => value.Property,
            CollectionCountFilter value => value.Property,
            FlagsFilter value => value.Property,
            _ => null
        };
        if (property is null)
        {
            return false;
        }

        object? raw = await RawAsync(
            await FieldAsync(item, property, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return FilterEvaluator.Matches(filter, _ => raw);
    }

    private static async ValueTask<CopValue> FieldAsync(CopValue value, string path,
        CancellationToken cancellationToken)
    {
        foreach (string name in path.Split('.'))
        {
            while (value is CopThunk thunk)
            {
                value = await thunk.ForceAsync(cancellationToken).ConfigureAwait(false);
            }
            value = value switch
            {
                CopDynamicObject obj => await obj.GetFieldAsync(name, cancellationToken)
                    .ConfigureAwait(false),
                CopObject obj => obj.GetField(name),
                CopNull => CopNull.Instance,
                _ => throw new CopEvaluationException($"Unknown query property '{path}'.")
            };
        }
        while (value is CopThunk thunk)
        {
            value = await thunk.ForceAsync(cancellationToken).ConfigureAwait(false);
        }
        return value;
    }

    private static async ValueTask<object?> RawAsync(CopValue value,
        CancellationToken cancellationToken)
    {
        if (value is CopList or CopLazyCollection or CopQueryable)
        {
            List<object?> items = [];
            await foreach (CopValue item in CollectionValues.EnumerateAsync(value,
                cancellationToken).ConfigureAwait(false))
            {
                items.Add(await RawAsync(item, cancellationToken).ConfigureAwait(false));
            }
            return items;
        }
        return value switch
        {
            CopNull => null,
            CopString text => text.Value,
            CopInt integer => integer.Value,
            CopNumber number => number.Value,
            CopBool boolean => boolean.IsTrue,
            _ => await value.DisplayAsync(cancellationToken).ConfigureAwait(false)
        };
    }
}
