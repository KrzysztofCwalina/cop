// A property within a type declaration.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// A property within a type declaration.
/// </summary>
public sealed class PropertySymbol
{
    /// <summary>
    /// The member name declared by an object type.
    /// </summary>
    public string Name { get; }
    /// <summary>
    /// The property's source type annotation, when present.
    /// </summary>
    public TypeRef? DeclaredType { get; }
    /// <summary>
    /// Whether objects may omit this declared property.
    /// </summary>
    public bool IsOptional { get; }

    public PropertySymbol(string name, TypeRef? declaredType, bool isOptional)
    {
        Name = name;
        DeclaredType = declaredType;
        IsOptional = isOptional;
    }
}
