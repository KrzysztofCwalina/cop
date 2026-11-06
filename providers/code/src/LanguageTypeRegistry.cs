// Reconstructs language-specific declarations from cached source models.

using System.Collections.Concurrent;
using System.Runtime.Loader;

namespace Cop.Providers.SourceModel;

/// <summary>
/// Reconstructs language-specific <see cref="TypeDeclaration"/> subtypes when loading the
/// source cache. Language providers (e.g. Rust) register a factory keyed by their
/// <see cref="TypeDeclaration.LanguageTag"/> so a cached base type is upgraded back to its
/// subtype (carrying its <see cref="TypeDeclaration.LanguageFlags"/>) — keeping per-item
/// adapter selection correct on cache hits. Providers that don't cache (e.g. C#) don't need this.
/// </summary>
public static class LanguageTypeRegistry
{
    /// <summary>
    /// Registers reconstruction of language-specific types from shared cached
    /// declarations.
    /// </summary>
    public static void Register(string tag, Func<TypeDeclaration, IReadOnlyDictionary<string, bool>, TypeDeclaration> factory)
        => LanguageCacheFactories<TypeDeclaration>.Register(tag, factory);

    /// <summary>
    /// Restores a language-specific type from its shared declaration and cached flags.
    /// </summary>
    public static TypeDeclaration Reconstruct(string tag, TypeDeclaration baseDecl, IReadOnlyDictionary<string, bool> flags)
        => Reconstruct(tag, baseDecl, flags, System.Runtime.Loader.AssemblyLoadContext.Default);

    internal static TypeDeclaration Reconstruct(string tag, TypeDeclaration baseDecl, IReadOnlyDictionary<string, bool> flags,
        System.Runtime.Loader.AssemblyLoadContext context) => LanguageCacheFactories<TypeDeclaration>.Reconstruct(tag, baseDecl, flags, context);
}
