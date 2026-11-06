// Converts shared method declarations to Java-specific declarations.

namespace Cop.Providers.SourceModel;

/// <summary>Converts shared method declarations to Java-specific declarations.</summary>
public static class JavaMethodDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="MethodDeclaration"/> as a Java-specific one.</summary>
    public static JavaMethodDeclaration AsJava(this MethodDeclaration source)
        => new(source);
}
