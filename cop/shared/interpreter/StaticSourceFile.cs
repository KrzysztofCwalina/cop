// Associates a parsed module with its original path and source buffer.

using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>A parsed source module with its path and complete source text.</summary>
public sealed record StaticSourceFile(ModuleNode Module, string Path, string Source);
