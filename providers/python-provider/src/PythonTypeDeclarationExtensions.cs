// Converts shared type declarations to Python-specific declarations.

namespace Cop.Providers.SourceModel;

/// <summary>Converts shared type declarations to Python-specific declarations.</summary>
public static class PythonTypeDeclarationExtensions
{
    /// <summary>Wraps a shared type with Python type-category facts.</summary>
    public static PythonTypeDeclaration AsPython(this TypeDeclaration source, bool isAbstract = false)
        => new(source, isAbstract);
}
