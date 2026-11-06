// Adds C#-specific method facts to the shared source model.

namespace Cop.Providers.SourceModel;

/// <summary>
/// Copies shared methods while adding C# modifier facts.
/// </summary>
public static class CSharpMethodDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="MethodDeclaration"/> as a C#-specific one.</summary>
    public static CSharpMethodDeclaration AsCSharp(this MethodDeclaration source)
        => new(source);
}
