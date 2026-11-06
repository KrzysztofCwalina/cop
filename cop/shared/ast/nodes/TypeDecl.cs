// Type declaration with optional base type and properties.

namespace Cop.Lang.Ast;

/// <summary>
/// Type declaration with optional base type and properties.
/// </summary>
public record TypeDecl(
    string Name,
    string? BaseType,
    List<PropertyDecl> Properties,
    bool IsExported = false,
    string? DocComment = null,
    int Line = 0,
    List<string>? Traits = null) : Declaration(Line);
