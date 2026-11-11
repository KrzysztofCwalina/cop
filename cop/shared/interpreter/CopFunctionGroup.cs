// A group of overloaded functions with the same name but different typed parameters.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A group of overloaded functions with the same name but different typed parameters.
/// Dispatches to the correct overload based on the first argument's type.
/// </summary>
public sealed class CopFunctionGroup : CopValue, ICopCallable
{
    /// <summary>
    /// The shared callable name used to resolve these overloads.
    /// </summary>
    public string Name { get; }
    private readonly List<CopFunction> _overloads = [];
    /// <summary>
    /// The overloads registered under the shared callable name.
    /// </summary>
    public IReadOnlyList<CopFunction> Functions => _overloads;

    public CopFunctionGroup(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Adds an overload while preserving its declaration and captured environment.
    /// </summary>
    public void Add(CopFunction func) => _overloads.Add(func);

    /// <summary>
    /// The number of arguments still required to invoke this callable.
    /// </summary>
    public int Arity => _overloads.Count > 0 ? _overloads[0].Arity : 0;

    /// <inheritdoc cref="ICopCallable.InvokeAsync"/>
    public async ValueTask<CopValue> InvokeAsync(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env, CancellationToken cancellationToken = default)
    {
        // Try to find matching overload by first parameter type (for predicates)
        if (args.Count > 0)
        {
            CopValue subject = args[0];
            while (subject is CopThunk thunk)
            {
                subject = await thunk.ForceAsync(cancellationToken);
            }
            bool subjectIsCollection = subject is CopList or CopLazyCollection or CopQueryable;
            bool ambiguousCollectionTypes = subjectIsCollection && _overloads
                .Where(o => o.Arity == args.Count && o.Declaration.Params.Count > 0
                    && o.Declaration.Params[0].Type is { IsCollection: true })
                .Select(o => o.Declaration.Params[0].Type!.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() > 1;

            // Constraint/guard dispatch: when overloads carry a first-parameter constraint predicate
            // (e.g. `writesOutput(Statement:isCSharp)` vs `(Statement:isPython)`), pick the overload
            // whose constraint holds for the argument. Without this, same-typed guarded overloads all
            // collapsed onto the first one and language-guarded overloads silently mis-dispatched —
            // e.g. Python statements ran the C# overload's body and never matched (issue #35).
            if (_overloads.Any(o => o.Declaration.Params.Count > 0 && o.Declaration.Params[0].Type?.Constraint is not null))
            {
                bool hasUnconstrained = false;
                foreach (CopFunction overload in _overloads)
                {
                    string? constraint = overload.Declaration.Params.Count > 0
                        ? overload.Declaration.Params[0].Type?.Constraint : null;
                    if (constraint is null) { hasUnconstrained = true; continue; }
                    if (env.TryLookup(constraint, out CopValue? guardVal) && guardVal is ICopCallable guard
                        && (await (await guard.InvokeAsync([subject], evaluator, env, cancellationToken: cancellationToken)).IsTruthyAsync(cancellationToken)))
                    {
                        return (await evaluator.CallUserFunctionAsync(overload, args, cancellationToken: cancellationToken));
                    }
                }
                // No constrained overload matched. When every overload is constrained (no default),
                // the guard genuinely excludes this argument — a guarded predicate returns false (so
                // e.g. `isMissingNamespace(Type:isCSharp)` simply doesn't apply to non-C# types,
                // rather than running its body and crashing). With an unconstrained overload present,
                // fall through to the normal dispatch below so it serves as the default fallback.
                if (!hasUnconstrained)
                {
                    return CopBool.False;
                }
            }

            string? subjectTypeName = (await GetTypeNameAsync(subject, cancellationToken: cancellationToken));

            // Arity-aware type dispatch: among overloads whose FIRST parameter type matches the
            // subject (preferring exact name, then subtype, then trait conformance — the same
            // priority used by the loops below), pick the one whose arity matches the supplied
            // argument count. This distinguishes same-first-typed overloads that differ only by
            // arity — e.g. `toWarning(TextFilePosition, message)` vs
            // `toWarning(TextFilePosition, message, check)` — so `:toWarning('m', nameof(x))`
            // resolves to the 3-arg overload instead of always the first-registered one. When no
            // arity-matching type overload exists, falls through to the type-only dispatch below
            // (unchanged behaviour for single-arity overload sets and param-array/variadic forms).
            if (subjectTypeName is not null)
            {
                TypeRegistry? arityRegistry = evaluator.TypeRegistry;
                CopFunction? best = null;
                int bestTier = int.MaxValue;
                foreach (CopFunction overload in _overloads)
                {
                    if (overload.Arity != args.Count || overload.Declaration.Params.Count == 0)
                    {
                        continue;
                    }

                    TypeRef? parameterType = overload.Declaration.Params[0].Type;
                    if (parameterType is null)
                    {
                        continue;
                    }

                    string paramType = parameterType.Name;
                    int tier = parameterType.IsCollection ? (subjectIsCollection && !ambiguousCollectionTypes ? 0 : -1) :
                        subjectIsCollection && paramType is not ("collection" or "object") ? -1 :
                        string.Equals(paramType, subjectTypeName, StringComparison.OrdinalIgnoreCase) ? 0 :
                        (arityRegistry is not null && arityRegistry.IsSubtypeOf(subjectTypeName, paramType)) ? 1 :
                        (arityRegistry is not null && arityRegistry.ConformsTo(subjectTypeName, paramType)) ? 2 :
                        -1;
                    if (tier >= 0 && tier < bestTier)
                    {
                        best = overload;
                        bestTier = tier;
                        if (tier == 0)
                        {
                            break;
                        }
                    }
                }
                if (best is not null)
                {
                    return (await evaluator.CallUserFunctionAsync(best, args, cancellationToken: cancellationToken));
                }
            }

            if (subjectTypeName is not null)
            {
                foreach (CopFunction overload in _overloads)
                {
                    if (overload.Declaration.Params.Count > 0)
                    {
                        Parameter firstParam = overload.Declaration.Params[0];
                        string? paramType = firstParam.Type?.Name;
                        if (paramType is not null && firstParam.Type!.IsCollection == subjectIsCollection &&
                            string.Equals(paramType, subjectTypeName, StringComparison.OrdinalIgnoreCase))
                        {
                            return (await evaluator.CallUserFunctionAsync(overload, args, cancellationToken: cancellationToken));
                        }
                    }
                }

                // Trait conformance fallback: if subject's type conforms to a trait used as param type
                TypeRegistry? registry = evaluator.TypeRegistry;
                if (registry is not null)
                {
                    // Subtype match: subject's type is a (transitive) subtype of an overload's
                    // param type (e.g. a CSharpType subject dispatches to an `(Type)` overload).
                    foreach (CopFunction overload in _overloads)
                    {
                        if (overload.Declaration.Params.Count > 0)
                        {
                            Parameter firstParam = overload.Declaration.Params[0];
                            string? paramType = firstParam.Type?.Name;
                            if (paramType is not null && registry.IsSubtypeOf(subjectTypeName, paramType))
                            {
                                return (await evaluator.CallUserFunctionAsync(overload, args, cancellationToken: cancellationToken));
                            }
                        }
                    }

                    foreach (CopFunction overload in _overloads)
                    {
                        if (overload.Declaration.Params.Count > 0)
                        {
                            Parameter firstParam = overload.Declaration.Params[0];
                            string? paramType = firstParam.Type?.Name;
                            if (paramType is not null && registry.ConformsTo(subjectTypeName, paramType))
                            {
                                return (await evaluator.CallUserFunctionAsync(overload, args, cancellationToken: cancellationToken));
                            }
                        }
                    }
                }
            }
        }

        // Try matching by arity (prefer last registered for same-arity overrides like command redefinitions)
        for (int i = _overloads.Count - 1; i >= 0; i--)
        {
            if (_overloads[i].Arity == args.Count)
            {
                return (await evaluator.CallUserFunctionAsync(_overloads[i], args, cancellationToken: cancellationToken));
            }
        }

        // Try matching overloads with param array (more args than params)
        if (args.Count > 0)
        {
            foreach (CopFunction overload in _overloads)
            {
                if (args.Count > overload.Arity && overload.Arity > 0 &&
                    overload.Declaration.Params[^1].Type is { IsCollection: true })
                {
                    return (await evaluator.CallUserFunctionAsync(overload, args, cancellationToken: cancellationToken));
                }
            }
        }

        // Fall back to last registered overload
        return (await evaluator.CallUserFunctionAsync(_overloads[^1], args, cancellationToken: cancellationToken));
    }

    private static async ValueTask<string?> GetTypeNameAsync(CopValue value, CancellationToken cancellationToken = default) => value switch
    {
        CopThunk thunk => await GetTypeNameAsync(
            await thunk.ForceAsync(cancellationToken), cancellationToken),
        CopDynamicObject dyn => dyn.TypeName,
        CopObject obj => obj.TypeName,
        CopString => "string",
        CopInt => "int",
        CopNumber => "float",
        CopBool => "bool",
        CopList => "collection",
        CopLazyCollection => "collection",
        CopQueryable => "collection",
        _ => null
    };

    /// <inheritdoc/>
    public override string Display() => $"<function-group {Name} ({_overloads.Count} overloads)>";
    /// <inheritdoc/>
    public override string ToString() => Display();
}
