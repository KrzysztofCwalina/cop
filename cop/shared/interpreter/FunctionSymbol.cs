// A function, predicate, or command.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// A function, predicate, or command.
/// </summary>
public sealed class FunctionSymbol : Symbol
{
    /// <summary>
    /// Whether the declaration defines a function, predicate, or command.
    /// </summary>
    public CallableKind CallableKind { get; }
    /// <summary>
    /// The ordered parameter symbols declared by the callable.
    /// </summary>
    public IReadOnlyList<ParameterSymbol> Parameters { get; }
    /// <summary>
    /// The callable's declared result type, when explicitly annotated.
    /// </summary>
    public TypeRef? ReturnType { get; }

    /// <summary>
    /// For predicates, the narrowing type (e.g., `predicate isCall(Statement) : Call`).
    /// </summary>
    public TypeRef? NarrowingType { get; init; }

    /// <summary>
    /// Reference to the AST declaration (null for external/intrinsic functions).
    /// </summary>
    public FunctionDecl? Declaration { get; init; }

    public FunctionSymbol(string name, CallableKind callableKind,
        IReadOnlyList<ParameterSymbol> parameters, TypeRef? returnType = null)
        : base(name, SymbolKind.Function)
    {
        CallableKind = callableKind;
        Parameters = parameters;
        ReturnType = returnType;
    }
}
