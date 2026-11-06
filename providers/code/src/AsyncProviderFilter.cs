// Evaluates pushed-down filters while awaiting only the model fields they demand.

using System.Collections;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>Evaluates provider filters without blocking process-backed field access.</summary>
public sealed class AsyncProviderFilter(RuntimeBindings bindings)
{
    /// <summary>Preserves boolean short-circuiting while awaiting demanded source facts.</summary>
    public async ValueTask<bool> MatchesAsync(
        object item, FilterExpression? filter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (filter)
        {
            case null:
                return true;
            case AndFilter conjunction:
                foreach (FilterExpression condition in conjunction.Conditions)
                {
                    if (!await MatchesAsync(item, condition, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
                return true;
            case OrFilter disjunction:
                foreach (FilterExpression condition in disjunction.Conditions)
                {
                    if (await MatchesAsync(item, condition, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }
                }
                return false;
            case NotFilter negation:
                return !await MatchesAsync(item, negation.Inner, cancellationToken)
                    .ConfigureAwait(false);
        }

        string? property = filter switch
        {
            PropertyFilter value => value.Property,
            StringOpFilter value => value.Property,
            ComparisonFilter value => value.Property,
            ContainsAnyFilter value => value.Property,
            InFilter value => value.Property,
            CollectionContainsFilter value => value.Property,
            CollectionAnyFilter value => value.Property,
            CollectionCountFilter value => value.Property,
            FlagsFilter value => value.Property,
            _ => null
        };
        if (property is null)
        {
            return true;
        }
        (bool known, object? field) = await ReadPathAsync(item, property, cancellationToken)
            .ConfigureAwait(false);
        if (!known)
        {
            // Unsupported pushdown stays eligible for the interpreter's full predicate.
            return true;
        }
        if (filter is CollectionAnyFilter any)
        {
            if (field is IEnumerable values)
            {
                foreach (object? value in values)
                {
                    if (value is not null
                        && await MatchesAsync(value, any.ItemFilter, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        Dictionary<string, Func<object, object?>> prepared = new()
        {
            [property] = _ => field
        };
        return FilterCompiler.Compile(filter, prepared)(item);
    }

    private async ValueTask<(bool Known, object? Value)> ReadPathAsync(
        object item, string path, CancellationToken cancellationToken)
    {
        object? current = item;
        foreach (string field in path.Split('.'))
        {
            if (current is null)
            {
                return (true, null);
            }
            (bool known, object? value) = await ReadFieldAsync(current, field, cancellationToken)
                .ConfigureAwait(false);
            if (!known)
            {
                return (false, null);
            }
            current = value;
        }
        return (true, current);
    }

    private async ValueTask<(bool Known, object? Value)> ReadFieldAsync(
        object item, string field, CancellationToken cancellationToken)
    {
        for (Type? type = item.GetType(); type is not null; type = type.BaseType)
        {
            if (!bindings.ClrTypeMappings.TryGetValue(type, out string? name))
            {
                continue;
            }
            if (bindings.AsyncAccessors.TryGetValue(name, out Dictionary<string,
                    Func<object, CancellationToken, ValueTask<object?>>>? asyncFields)
                && asyncFields.TryGetValue(field,
                    out Func<object, CancellationToken, ValueTask<object?>>? asyncRead))
            {
                return (true, await asyncRead(item, cancellationToken).ConfigureAwait(false));
            }
        }
        for (Type? type = item.GetType(); type is not null; type = type.BaseType)
        {
            if (bindings.ClrTypeMappings.TryGetValue(type, out string? name)
                && bindings.Accessors.TryGetValue(name,
                    out Dictionary<string, Func<object, object?>>? fields)
                && fields.TryGetValue(field, out Func<object, object?>? read))
            {
                return (true, read(item));
            }
        }
        if (item is DataObject value && value.Fields.TryGetValue(field, out object? member))
        {
            return (true, member);
        }
        return (false, null);
    }
}
