// Adds C#-specific type facts to the shared source model.

namespace Cop.Providers.SourceModel;

/// <summary>
/// Copies shared types while adding C# declaration facts.
/// </summary>
public static class CSharpTypeDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="TypeDeclaration"/> as a C#-specific one.</summary>
    public static CSharpTypeDeclaration AsCSharp(this TypeDeclaration source)
        => new(source);
}
