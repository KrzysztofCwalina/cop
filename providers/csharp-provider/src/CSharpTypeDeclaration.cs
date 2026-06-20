namespace Cop.Providers.SourceModel;

/// <summary>
/// A C#-specific <see cref="TypeDeclaration"/> carrying language-specific facts that
/// have no place in the language-agnostic common model (e.g. records, partial types).
///
/// Only the C# provider emits these. The runtime maps this CLR type to the cop type
/// <c>CSharpType</c> (declared in the csharp package), so checks that narrow with
/// <c>:asCSharp</c> can read these fields while the common <c>Type</c> stays clean.
/// </summary>
public sealed record CSharpTypeDeclaration : TypeDeclaration
{
    public CSharpTypeDeclaration(TypeDeclaration source, bool isRecord, bool isPartial)
        : base(source)
    {
        IsRecord = isRecord;
        IsPartial = isPartial;
    }

    /// <summary>True for <c>record</c> / <c>record struct</c> declarations.</summary>
    public bool IsRecord { get; init; }

    /// <summary>True for <c>partial</c> type declarations.</summary>
    public bool IsPartial { get; init; }
}

public static class CSharpTypeDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="TypeDeclaration"/> as a C#-specific one.</summary>
    public static CSharpTypeDeclaration AsCSharp(this TypeDeclaration source, bool isRecord, bool isPartial)
        => new(source, isRecord, isPartial);
}
