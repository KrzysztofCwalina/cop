// Function declaration.

namespace Cop.Lang.Ast;

/// <summary>
/// Function declaration. Predicates are functions returning bool.
/// Supports both expression body and field-mapping body (for transforms).
/// </summary>
public record FunctionDecl(
    string Name,
    List<Parameter> Params,
    TypeRef? ReturnType,
    FunctionBody Body,
    bool IsExported = false,
    Expression? Guard = null,
    string? DocComment = null,
    int Line = 0,
    bool IsPredicate = false) : Declaration(Line)
{
    /// <summary>
    /// True when this function is a runnable command: an uppercase-named function with a block body.
    /// This is the single source of truth for "is a command" — tooling (REPL, help, list, run) must
    /// consult this instead of re-deriving <c>char.IsUpper(Name[0]) &amp;&amp; Body is BlockBody</c>.
    /// </summary>
    public bool IsCommand => Name.Length > 0 && char.IsUpper(Name[0]) && Body is BlockBody;
}
