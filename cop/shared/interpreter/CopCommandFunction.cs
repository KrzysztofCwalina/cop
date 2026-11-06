// Callable wrapper for command declarations (statement-block bodies).

using Cop.Core;

namespace Cop.Lang.Interpreter;

using System.Text;
using Cop.Lang.Ast;

/// <summary>
/// Callable wrapper for command declarations (statement-block bodies).
/// </summary>
internal sealed class CopCommandFunction : CopValue, ICopCallable
{
    private readonly CommandDecl _decl;
    private readonly Environment _closure;

    public CopCommandFunction(CommandDecl decl, Environment closure)
    {
        _decl = decl;
        _closure = closure;
    }

    /// <summary>
    /// The number of arguments still required to invoke this callable.
    /// </summary>
    public int Arity => _decl.Parameters?.Count ?? 0;

    /// <inheritdoc cref="ICopCallable.InvokeAsync"/>
    public async ValueTask<CopValue> InvokeAsync(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env, CancellationToken cancellationToken = default)
    {
        Environment cmdEnv = _closure.Extend();

        // Bind command parameters
        if (_decl.Parameters is not null)
        {
            for (int i = 0; i < _decl.Parameters.Count && i < args.Count; i++)
            {
                cmdEnv.Define(_decl.Parameters[i], args[i]);
            }
        }

        // Execute all but the last statement, then return last expression's value
        CopValue result = CopNull.Instance;
        foreach (Statement stmt in _decl.Body)
        {
            if (stmt is ExpressionStatement es)
            {
                result = (await evaluator.EvalAsync(es.Expr, cmdEnv, cancellationToken: cancellationToken));
            }
            else
            {
                await evaluator.ExecStatementAsync(stmt, cmdEnv, cancellationToken: cancellationToken);
                result = CopNull.Instance;
            }
        }
        return result;
    }

    /// <inheritdoc/>
    public override string Display() => $"<command {_decl.Name}>";
    /// <inheritdoc/>
    public override string ToString() => Display();
}
