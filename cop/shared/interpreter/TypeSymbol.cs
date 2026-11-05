// A named type (record-like structure).

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// A named type (record-like structure).
/// </summary>
public sealed class TypeSymbol : Symbol
{
    public string? BaseTypeName { get; }
    public IReadOnlyList<PropertySymbol> Properties { get; }

    public TypeSymbol(string name, string? baseTypeName, IReadOnlyList<PropertySymbol> properties)
        : base(name, SymbolKind.Type)
    {
        BaseTypeName = baseTypeName;
        Properties = properties;
    }
}
