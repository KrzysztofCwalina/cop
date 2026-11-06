// Adds C#-specific type facts to the shared source model.

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
    internal int SyntaxStart { get; set; } = -1;
    /// <summary>
    /// Copies a shared type before C#-specific facts are initialized.
    /// </summary>
    public CSharpTypeDeclaration(TypeDeclaration source)
        : base(source)
    {
    }

    /// <summary>True for <c>record</c> / <c>record struct</c> declarations.</summary>
    public bool IsRecord { get; init; }

    /// <summary>True for <c>record struct</c> declarations specifically.</summary>
    public bool IsRecordStruct { get; init; }

    /// <summary>True for <c>readonly struct</c> declarations.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>True for <c>ref struct</c> declarations.</summary>
    public bool IsRef { get; init; }

    /// <summary>True for <c>file</c>-scoped (file-local) type declarations.</summary>
    public bool IsFileLocal { get; init; }

    /// <summary>True for <c>partial</c> type declarations.</summary>
    public bool IsPartial { get; init; }

    /// <summary>True when the type declares a primary constructor.</summary>
    public bool HasPrimaryConstructor { get; init; }

    /// <summary>True when the type declares generic type parameters.</summary>
    public bool IsGeneric { get; init; }
}
