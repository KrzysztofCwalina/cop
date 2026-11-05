// Command declaration (deprecated sugar for uppercase function with block body).

namespace Cop.Lang.Ast;

/// <summary>
/// Command declaration (deprecated sugar for uppercase function with block body).
/// Retained for backward compatibility with old parser; new code should use FunctionDecl with BlockBody.
/// </summary>
public record CommandDecl(
    string Name,
    List<Statement> Body,
    List<string>? Parameters = null,
    bool IsExported = false,
    string? DocComment = null,
    int Line = 0) : Declaration(Line);
