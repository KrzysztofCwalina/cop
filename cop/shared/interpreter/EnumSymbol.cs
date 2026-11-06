// An enum type.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// An enum type.
/// </summary>
public sealed class EnumSymbol : Symbol
{
    /// <summary>
    /// The optional type restricting the values carried by enum members.
    /// </summary>
    public TypeRef? MemberType { get; }
    /// <summary>
    /// The member symbols declared by the enum.
    /// </summary>
    public IReadOnlyList<EnumMemberSymbol> Members { get; }

    public EnumSymbol(string name, TypeRef? memberType, IReadOnlyList<EnumMemberSymbol> members)
        : base(name, SymbolKind.Enum)
    {
        MemberType = memberType;
        Members = members;
    }
}
