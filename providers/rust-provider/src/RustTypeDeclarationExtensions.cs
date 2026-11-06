// Converts shared type declarations to Rust-specific declarations.

namespace Cop.Providers.SourceModel;

/// <summary>Converts shared type declarations to Rust-specific declarations.</summary>
public static class RustTypeDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="TypeDeclaration"/> as a Rust-specific one.</summary>
    public static RustTypeDeclaration AsRust(this TypeDeclaration source)
        => new(source);
}
