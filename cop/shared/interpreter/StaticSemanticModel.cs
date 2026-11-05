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

    public TypeInfo? InferType(Expression expression, IReadOnlyDictionary<string, TypeInfo?>? locals = null)
    {
        lock (_gate)
        {
            return _checker.InferStaticType(expression, locals);
        }
    }

    public StaticCallInfo ResolveCall(CallExpr call, IReadOnlyDictionary<string, TypeInfo?>? locals = null)
    {
        lock (_gate)
        {
            return _checker.ResolveStaticCall(call, locals);
        }
    }
}
