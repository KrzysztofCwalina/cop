// An enum type.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// An enum type.
/// </summary>
public sealed class EnumSymbol : Symbol
{
    public TypeRef? MemberType { get; }
    public IReadOnlyList<EnumMemberSymbol> Members { get; }

    public EnumSymbol(string name, TypeRef? memberType, IReadOnlyList<EnumMemberSymbol> members)
        : base(name, SymbolKind.Enum)
    {
        MemberType = memberType;
        Members = members;
    }
}
