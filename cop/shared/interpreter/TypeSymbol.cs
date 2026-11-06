// A named type (record-like structure).

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// A named type (record-like structure).
/// </summary>
public sealed class TypeSymbol : Symbol
{
    /// <summary>
    /// The optional named type extended by this type declaration.
    /// </summary>
    public string? BaseTypeName { get; }
    /// <summary>
    /// The property declarations belonging to this object type.
    /// </summary>
    public IReadOnlyList<PropertySymbol> Properties { get; }

    public TypeSymbol(string name, string? baseTypeName, IReadOnlyList<PropertySymbol> properties)
        : base(name, SymbolKind.Type)
    {
        BaseTypeName = baseTypeName;
        Properties = properties;
    }
}
