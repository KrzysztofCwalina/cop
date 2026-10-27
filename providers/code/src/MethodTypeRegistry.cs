namespace Cop.Providers.SourceModel;

/// <summary>
/// Reconstructs language-specific <see cref="MethodDeclaration"/> subtypes when loading the
/// source cache. Language providers register a factory keyed by their
/// <see cref="MethodDeclaration.LanguageTag"/> so a cached base method is upgraded back to its
/// subtype (carrying its <see cref="MethodDeclaration.LanguageFlags"/>). Mirrors
/// <see cref="LanguageTypeRegistry"/>. Providers that don't cache (e.g. C#) don't need this.
/// </summary>
public static class MethodTypeRegistry
{
    public static void Register(string tag, Func<MethodDeclaration, IReadOnlyDictionary<string, bool>, MethodDeclaration> factory)
        => LanguageCacheFactories<MethodDeclaration>.Register(tag, factory);

    public static MethodDeclaration Reconstruct(string tag, MethodDeclaration baseDecl, IReadOnlyDictionary<string, bool> flags)
        => Reconstruct(tag, baseDecl, flags, System.Runtime.Loader.AssemblyLoadContext.Default);

    internal static MethodDeclaration Reconstruct(string tag, MethodDeclaration baseDecl, IReadOnlyDictionary<string, bool> flags,
        System.Runtime.Loader.AssemblyLoadContext context) => LanguageCacheFactories<MethodDeclaration>.Reconstruct(tag, baseDecl, flags, context);
}

/// <summary>
/// Reconstructs language-specific <see cref="StatementInfo"/> subtypes when loading the source
/// cache. Mirrors <see cref="MethodTypeRegistry"/>. Providers that don't cache don't need this.
/// </summary>
public static class StatementTypeRegistry
{
    public static void Register(string tag, Func<StatementInfo, IReadOnlyDictionary<string, bool>, StatementInfo> factory)
        => LanguageCacheFactories<StatementInfo>.Register(tag, factory);

    public static StatementInfo Reconstruct(string tag, StatementInfo baseDecl, IReadOnlyDictionary<string, bool> flags)
        => Reconstruct(tag, baseDecl, flags, System.Runtime.Loader.AssemblyLoadContext.Default);

    internal static StatementInfo Reconstruct(string tag, StatementInfo baseDecl, IReadOnlyDictionary<string, bool> flags,
        System.Runtime.Loader.AssemblyLoadContext context) => LanguageCacheFactories<StatementInfo>.Reconstruct(tag, baseDecl, flags, context);
}
