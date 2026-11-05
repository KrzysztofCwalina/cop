// A property within a type declaration.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// A property within a type declaration.
/// </summary>
public sealed class PropertySymbol
{
    public string Name { get; }
    public TypeRef? DeclaredType { get; }
    public bool IsOptional { get; }

    public PropertySymbol(string name, TypeRef? declaredType, bool isOptional)
    {
        Name = name;
        DeclaredType = declaredType;
        IsOptional = isOptional;
    }
}
