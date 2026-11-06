// Declaration-only analysis.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// Declaration-only analysis. It never evaluates expressions, guards, providers, or commands.
/// Ambiguous dispatch, runtime guards and unavailable type information remain unknown.
/// </summary>
public sealed class StaticSemanticModel
{
    private readonly TypeChecker _checker;
    private readonly object _gate = new();

    public StaticSemanticModel(IEnumerable<ModuleNode> modules) => _checker = TypeChecker.ForStaticModel(modules);

    /// <summary>
    /// Infers an expression's static type using optional local type bindings.
    /// </summary>
    public TypeInfo? InferType(Expression expression, IReadOnlyDictionary<string, TypeInfo?>? locals = null)
    {
        lock (_gate)
        {
            return _checker.InferStaticType(expression, locals);
        }
    }

    /// <summary>
    /// Resolves a call's target and argument types without executing it.
    /// </summary>
    public StaticCallInfo ResolveCall(CallExpr call, IReadOnlyDictionary<string, TypeInfo?>? locals = null)
    {
        lock (_gate)
        {
            return _checker.ResolveStaticCall(call, locals);
        }
    }
}
