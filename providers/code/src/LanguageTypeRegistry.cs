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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<TypeDeclaration, IReadOnlyDictionary<string, bool>, TypeDeclaration>> _factories = new(StringComparer.Ordinal);

    public static void Register(string tag, Func<TypeDeclaration, IReadOnlyDictionary<string, bool>, TypeDeclaration> factory)
        => _factories[tag] = factory;

    public static TypeDeclaration Reconstruct(string tag, TypeDeclaration baseDecl, IReadOnlyDictionary<string, bool> flags)
        => _factories.TryGetValue(tag, out var factory) ? factory(baseDecl, flags) : baseDecl;
}
