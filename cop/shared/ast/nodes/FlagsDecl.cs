// Records a flags declaration and its named members.

namespace Cop.Lang.Ast;

/// <summary>
/// Flags declaration: flags Name = Flag1 | Flag2 | ...
/// </summary>
public record FlagsDecl(
    string Name,
    List<string> Members,
    bool IsExported = false,
    string? DocComment = null,
    int Line = 0) : Declaration(Line);
