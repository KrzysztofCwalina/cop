// A single enum member value.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// A single enum member value.
/// </summary>
public sealed class EnumMemberSymbol : Symbol
{
    public string OwningEnumName { get; }

    public EnumMemberSymbol(string name, string owningEnumName)
        : base(name, SymbolKind.EnumMember)
    {
        OwningEnumName = owningEnumName;
    }
}
