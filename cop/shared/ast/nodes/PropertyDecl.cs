// A single property within a type declaration.

namespace Cop.Lang.Ast;

/// <summary>
/// A single property within a type declaration.
/// For computed properties (e.g., filePath => item.File.Path), ComputedExpr is set
/// and Type holds an inferred placeholder.
/// </summary>
public record PropertyDecl(
    string Name,
    TypeRef Type,
    bool IsOptional = false,
    int Line = 0,
    Expression? ComputedExpr = null) : AstNode(Line);
