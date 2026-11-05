// For-each iteration: foreach item in collection { body }

namespace Cop.Lang.Ast;

/// <summary>
/// For-each iteration: foreach item in collection { body }
/// </summary>
public record ForEachStatement(
    string Variable,
    Expression Collection,
    List<Statement> Body,
    int Line = 0,
    bool IsAsync = false) : Statement(Line);
