// Locates a declaration in either a local source file or an imported package.

namespace Cop.Lang.Interpreter;

/// <summary>A declaration's source path, optional package namespace, and line.</summary>
public sealed record StaticDeclarationLocation(string Path, string? Namespace, int Line);
