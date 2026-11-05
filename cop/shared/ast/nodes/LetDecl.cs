// Records a named value binding and optional type annotation.

namespace Cop.Lang.Ast;

/// <summary>
/// Let binding: let Name : Type = Expression
/// </summary>
public record LetDecl(
    string Name,
    TypeRef? TypeAnnotation,
    Expression Value,
    bool IsExported = false,
    string? DocComment = null,
    int Line = 0) : Declaration(Line);
