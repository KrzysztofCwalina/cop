// Converts shared types to JavaScript-specific declarations.

namespace Cop.Providers.SourceModel;

/// <summary>Converts shared types to JavaScript-specific declarations.</summary>
public static class JavaScriptTypeDeclarationExtensions
{
    /// <summary>Wraps a shared type with JavaScript declaration-specific facts.</summary>
    public static JavaScriptTypeDeclaration AsJavaScript(this TypeDeclaration source)
        => new(source);
}
