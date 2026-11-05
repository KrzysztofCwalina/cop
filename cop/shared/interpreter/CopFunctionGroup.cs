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
    public string Name { get; }
    private readonly List<CopFunction> _overloads = [];
    public IReadOnlyList<CopFunction> Functions => _overloads;

    public CopFunctionGroup(string name)
    {
        Name = name;
    }

    public void Add(CopFunction func) => _overloads.Add(func);

    public int Arity => _overloads.Count > 0 ? _overloads[0].Arity : 0;

    public CopValue Invoke(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env)
    {
        // Try to find matching overload by first parameter type (for predicates)
        if (args.Count > 0)
        {
            CopValue subject = args[0];

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
                        && guard.Invoke([subject], evaluator, env).IsTruthy)
                    {
                        return evaluator.CallUserFunction(overload, args);
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

            string? subjectTypeName = GetTypeName(subject);

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

                    string? paramType = overload.Declaration.Params[0].Type?.Name;
                    if (paramType is null)
                    {
                        continue;
                    }

                    int tier =
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
                    return evaluator.CallUserFunction(best, args);
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
                        if (paramType is not null &&
                            string.Equals(paramType, subjectTypeName, StringComparison.OrdinalIgnoreCase))
                        {
                            return evaluator.CallUserFunction(overload, args);
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
                                return evaluator.CallUserFunction(overload, args);
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
                                return evaluator.CallUserFunction(overload, args);
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
                return evaluator.CallUserFunction(_overloads[i], args);
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
                    return evaluator.CallUserFunction(overload, args);
                }
            }
        }

        // Fall back to last registered overload
        return evaluator.CallUserFunction(_overloads[^1], args);
    }

    private static string? GetTypeName(CopValue value) => value switch
    {
        CopThunk thunk => GetTypeName(thunk.Force()),
        CopDynamicObject dyn => dyn.TypeName,
        CopObject obj => obj.TypeName,
        CopString => "string",
        CopInt => "int",
        CopNumber => "float",
        CopBool => "bool",
        CopList => "collection",
        CopLazyCollection => "collection",
        _ => null
    };

    public override string Display() => $"<function-group {Name} ({_overloads.Count} overloads)>";
    public override string ToString() => Display();
}
