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
    public static void Register(string tag, Func<TypeDeclaration, IReadOnlyDictionary<string, bool>, TypeDeclaration> factory)
        => LanguageCacheFactories<TypeDeclaration>.Register(tag, factory);

    public static TypeDeclaration Reconstruct(string tag, TypeDeclaration baseDecl, IReadOnlyDictionary<string, bool> flags)
        => Reconstruct(tag, baseDecl, flags, System.Runtime.Loader.AssemblyLoadContext.Default);

    internal static TypeDeclaration Reconstruct(string tag, TypeDeclaration baseDecl, IReadOnlyDictionary<string, bool> flags,
        System.Runtime.Loader.AssemblyLoadContext context) => LanguageCacheFactories<TypeDeclaration>.Reconstruct(tag, baseDecl, flags, context);
}

internal static class LanguageCacheFactories<T>
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Runtime.Loader.AssemblyLoadContext,
        System.Collections.Concurrent.ConcurrentDictionary<string, Func<T, IReadOnlyDictionary<string, bool>, T>>> Factories = new();

    public static void Register(string tag, Func<T, IReadOnlyDictionary<string, bool>, T> factory)
    {
        var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(factory.Method.Module.Assembly)
            ?? System.Runtime.Loader.AssemblyLoadContext.Default;
        Factories.GetOrCreateValue(context)[tag] = factory;
    }

    public static T Reconstruct(string tag, T value, IReadOnlyDictionary<string, bool> flags,
        System.Runtime.Loader.AssemblyLoadContext context) =>
        Factories.TryGetValue(context, out var factories) && factories.TryGetValue(tag, out var factory)
            ? factory(value, flags) : value;
}
