// Records a source module import and its declaration line.

namespace Cop.Lang.Ast;

/// <summary>
/// Import declaration: import ModuleName
/// </summary>
public record ImportDecl(string ModuleName, int Line = 0) : Declaration(Line);
