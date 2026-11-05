// Records an enum declaration and its named members.

namespace Cop.Lang.Ast;

/// <summary>
/// Enum declaration: enum Name = Member1 | Member2 | ...
/// </summary>
public record EnumDecl(
    string Name,
    TypeRef? MemberType,
    List<string> Members,
    bool IsExported = false,
    string? DocComment = null,
    int Line = 0) : Declaration(Line);
