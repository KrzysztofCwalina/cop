// Converts shared methods to JavaScript-specific declarations.

namespace Cop.Providers.SourceModel;

/// <summary>Converts shared methods to JavaScript-specific declarations.</summary>
public static class JavaScriptMethodDeclarationExtensions
{
    /// <summary>Wraps a shared method with JavaScript function-form facts.</summary>
    public static JavaScriptMethodDeclaration AsJavaScript(
        this MethodDeclaration source,
        bool isGenerator = false,
        bool isArrow = false,
        bool isGetter = false,
        bool isSetter = false)
        => new(source, isGenerator, isArrow, isGetter, isSetter);
}
