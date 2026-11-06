// Converts shared type declarations to Java-specific declarations.

namespace Cop.Providers.SourceModel;

/// <summary>Converts shared type declarations to Java-specific declarations.</summary>
public static class JavaTypeDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="TypeDeclaration"/> as a Java-specific one.</summary>
    public static JavaTypeDeclaration AsJava(this TypeDeclaration source)
        => new(source);
}
