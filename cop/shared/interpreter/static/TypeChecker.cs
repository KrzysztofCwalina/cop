// Static inference and call selection for the partial Cop type checker.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// Static inference and call selection for the partial Cop type checker.
/// </summary>
public sealed partial class TypeChecker
{
    private bool _staticInference;
    private readonly HashSet<FunctionDecl> _inferringFunctions = new(ReferenceEqualityComparer.Instance);

    internal static TypeChecker ForStaticModel(IEnumerable<ModuleNode> modules)
    {
        TypeChecker checker = new TypeChecker { _staticInference = true };
        List<ModuleNode> all = modules.ToList();
        checker.BuildModel(all, all);
        return checker;
    }

    private T WithStaticLocals<T>(IReadOnlyDictionary<string, TypeInfo?>? locals, Func<T> query)
    {
        Dictionary<string, IT?> previous = new Dictionary<string, IT?>(_locals, StringComparer.Ordinal);
        _locals.Clear();
        if (locals is not null)
        {
            foreach ((string? name, TypeInfo? type) in locals)
            {
                _locals[name] = type is { } known && IsStaticTypeKnown(known.Name) ? new(known.Name, known.IsCollection) : null;
            }
        }

        try { return query(); }
        finally
        {
            _locals.Clear();
            foreach ((string? name, IT? type) in previous)
            {
                _locals[name] = type;
            }
        }
    }

    internal TypeInfo? InferStaticType(Expression expression, IReadOnlyDictionary<string, TypeInfo?>? locals) =>
        WithStaticLocals(locals, () => ToStaticType(InferExpr(expression)));

    internal StaticCallInfo ResolveStaticCall(CallExpr call, IReadOnlyDictionary<string, TypeInfo?>? locals) =>
        WithStaticLocals(locals, () => SelectStaticCall(call));

    private TypeInfo? ToStaticType(IT? type) => type is null || !IsStaticTypeKnown(type.Name)
        ? null : new(type.Name, type.IsCollection);
    private bool IsStaticTypeKnown(string name) =>
        IsPrimitive(name.ToLowerInvariant()) || name is "object" or "any" || _knownTypes.Contains(name);

    private IT? InferStaticExpression(Expression? expression)
    {
        switch (expression)
        {
            case LiteralExpr literal:
                return literal.Value switch
                {
                    int or long => new("int", false),
                    double or float => new("float", false),
                    string => new("string", false),
                    bool => new("bool", false),
                    _ => null
                };
            case InterpolatedStringExpr: return new("string", false);
            case IdentifierExpr identifier:
                return _locals.TryGetValue(identifier.Name, out IT? local) ? local
                    : _topLevelLets.GetValueOrDefault(identifier.Name);
            case MemberExpr member: return InferMember(member);
            case CallExpr call:
                TypeInfo? result = SelectStaticCall(call).ResultType;
                return result is { } type ? new(type.Name, type.IsCollection) : null;
            case ListExpr list:
                List<IT?> items = list.Elements.Select(InferExpr).ToList();
                return items.Count > 0 && items[0] is { } first && items.All(item => item == first)
                    ? new(first.Name, true) : null;
            case ObjectExpr { TypeHint: not null } record:
                return _knownTypes.Contains(record.TypeHint) ? new(record.TypeHint, false) : null;
            case BinaryExpr binary:
                if (IsBooleanOp(binary.Op))
                {
                    return new("bool", false);
                }

                IT? left = InferExpr(binary.Left);
                IT? right = InferExpr(binary.Right);
                return binary.Op == BinaryOp.Add && left is not null && left == right ? left : null;
            case UnaryExpr unary:
                return unary.Op == UnaryOp.Not ? new("bool", false) : null;
            case ConditionalExpr conditional:
                IT? thenType = InferExpr(conditional.Then);
                return thenType is not null && thenType == InferExpr(conditional.Else) ? thenType : null;
            default: return null;
        }
    }

    private StaticCallInfo SelectStaticCall(CallExpr call)
    {
        StaticCallInfo Unknown(string reason) => new(null, [], null, [reason]);
        string? name = call.Callee switch
        {
            IdentifierExpr identifier when !_locals.ContainsKey(identifier.Name)
                && !_topLevelLets.ContainsKey(identifier.Name) => identifier.Name,
            MemberExpr { Object: IdentifierExpr ns } member when !_locals.ContainsKey(ns.Name)
                && !_topLevelLets.ContainsKey(ns.Name) => $"{ns.Name}.{member.Member}",
            _ => null
        };
        if (name is null || !_funcs.TryGetValue(name, out List<Sig>? overloads))
        {
            return Unknown("Call target is not a statically available source function.");
        }

        List<Sig> arity = overloads.Where(sig => sig.Params.Count == call.Args.Count).ToList();
        if (arity.Count == 0)
        {
            return Unknown($"'{name}' requires unsupported partial or variadic application.");
        }

        if (arity.Any(sig => sig.Declaration.Guard is not null || sig.Params.Any(p => p?.Constraint is not null)))
        {
            return Unknown($"'{name}' requires runtime guard or constraint evaluation.");
        }

        List<IT?> actuals = call.Args.Select(InferExpr).ToList();
        Sig? selected;
        if (arity.Count == 1)
        {
            selected = arity[0];
        }
        else
        {
            IT? subject = actuals.FirstOrDefault();
            if (subject is null || subject.IsCollection)
            {
                return Unknown($"'{name}' overload selection requires a known scalar subject.");
            }

            int Tier(Sig sig)
            {
                TypeRef? parameter = sig.Params.FirstOrDefault();
                if (parameter is null || parameter.IsCollection)
                {
                    return -1;
                }

                return string.Equals(parameter.Name, subject.Name, StringComparison.OrdinalIgnoreCase) ? 0
                    : IsSubtypeOrConforms(subject.Name, parameter.Name) ? 1 : -1;
            }
            List<(Sig Sig, int Tier)> candidates = arity.Select(sig => (Sig: sig, Tier: Tier(sig))).Where(c => c.Tier >= 0).ToList();
            if (candidates.Count == 0)
            {
                return Unknown($"'{name}' has no statically matching overload.");
            }

            int best = candidates.Min(c => c.Tier);
            List<(Sig Sig, int Tier)> matches = candidates.Where(c => c.Tier == best).ToList();
            if (matches.Count != 1)
            {
                return Unknown($"'{name}' has ambiguous source overloads.");
            }

            selected = matches[0].Sig;
        }
        Dictionary<string, string> substitutions = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < selected.Params.Count; i++)
        {
            TypeRef? parameter = selected.Params[i];
            IT? actual = actuals[i];
            if (parameter is null || actual is null)
            {
                continue;
            }

            if (GenericInference.IsTypeVariable(parameter.Name) && !_knownTypes.Contains(parameter.Name))
            {
                if (actual.IsCollection != parameter.IsCollection)
                {
                    return Unknown($"'{name}' generic argument shape is unknown.");
                }

                if (substitutions.TryGetValue(parameter.Name, out string? bound) && bound != actual.Name)
                {
                    return Unknown($"'{name}' has conflicting generic arguments.");
                }

                substitutions[parameter.Name] = actual.Name;
            }
            else if (!Assignable(actual, parameter, out _))
            {
                return Unknown($"'{name}' has an incompatible argument.");
            }
        }
        TypeInfo? Instantiate(TypeRef? type)
        {
            if (type is null)
            {
                return null;
            }

            TypeRef substituted = GenericInference.SubstituteTypeRef(type, substitutions);
            if (GenericInference.IsTypeVariable(substituted.Name) && !_knownTypes.Contains(substituted.Name))
            {
                return null;
            }

            if (!IsStaticTypeKnown(substituted.Name))
            {
                return null;
            }

            return new(substituted.Name, substituted.IsCollection);
        }
        List<TypeInfo?> parameters = selected.Params.Select(Instantiate).ToList();
        TypeInfo? returnType = Instantiate(selected.Return);
        if (returnType is null && selected.Return is null)
        {
            if (selected.IsPredicate)
            {
                returnType = new("bool", false);
            }
            else if (selected.Declaration.Body is ExpressionBody body && _inferringFunctions.Add(selected.Declaration))
            {
                try
                {
                    Dictionary<string, TypeInfo?> locals = selected.Declaration.Params.Select((parameter, i) => (parameter.Name, Type: parameters[i]))
                        .ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
                    returnType = WithStaticLocals(locals, () => ToStaticType(InferExpr(body.Expr)));
                }
                finally { _inferringFunctions.Remove(selected.Declaration); }
            }
        }
        return new(selected.Declaration, parameters, returnType, []);
    }
}
