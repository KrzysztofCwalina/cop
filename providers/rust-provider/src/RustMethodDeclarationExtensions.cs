// Converts shared method declarations to Rust-specific declarations.

namespace Cop.Providers.SourceModel;

/// <summary>Converts shared method declarations to Rust-specific declarations.</summary>
public static class RustMethodDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="MethodDeclaration"/> as a Rust-specific one.</summary>
    public static RustMethodDeclaration AsRust(
        this MethodDeclaration source,
        bool isUnsafe = false,
        bool isConst = false,
        bool isExtern = false)
        => new(source, isUnsafe, isConst, isExtern);
}
