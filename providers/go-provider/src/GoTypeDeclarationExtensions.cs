// Converts shared type declarations to Go-specific declarations.

namespace Cop.Providers.SourceModel;

/// <summary>Converts shared type declarations to Go-specific declarations.</summary>
public static class GoTypeDeclarationExtensions
{
    /// <summary>Wraps a shared type with Go type-category facts.</summary>
    public static GoTypeDeclaration AsGo(this TypeDeclaration source)
        => new(source);
}
