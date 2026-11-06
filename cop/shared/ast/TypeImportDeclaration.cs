// Records an import that exposes enum or flags members as bare identifiers.

namespace Cop.Lang;

/// <summary>
/// Imports all members of an enum or flags type into the surrounding scope.
/// </summary>
public record TypeImportDeclaration(
    string TypeName,
    int Line,
    bool IsExported = false,
    string? DocComment = null);
