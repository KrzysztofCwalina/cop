// Converts shared method declarations to Python-specific declarations.

namespace Cop.Providers.SourceModel;

/// <summary>
/// Converts shared method declarations to Python-specific declarations.
/// </summary>
public static class PythonMethodDeclarationExtensions
{
    /// <summary>Wraps a shared method with its Python generator classification.</summary>
    public static PythonMethodDeclaration AsPython(this MethodDeclaration source, bool isGenerator = false)
        => new(source, isGenerator);
}
