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
    private static readonly Dictionary<string, Func<MethodDeclaration, IReadOnlyDictionary<string, bool>, MethodDeclaration>> _factories = new(StringComparer.Ordinal);

    public static void Register(string tag, Func<MethodDeclaration, IReadOnlyDictionary<string, bool>, MethodDeclaration> factory)
        => _factories[tag] = factory;

    public static MethodDeclaration Reconstruct(string tag, MethodDeclaration baseDecl, IReadOnlyDictionary<string, bool> flags)
        => _factories.TryGetValue(tag, out var factory) ? factory(baseDecl, flags) : baseDecl;
}

/// <summary>
/// Reconstructs language-specific <see cref="StatementInfo"/> subtypes when loading the source
/// cache. Mirrors <see cref="MethodTypeRegistry"/>. Providers that don't cache don't need this.
/// </summary>
public static class StatementTypeRegistry
{
    private static readonly Dictionary<string, Func<StatementInfo, IReadOnlyDictionary<string, bool>, StatementInfo>> _factories = new(StringComparer.Ordinal);

    public static void Register(string tag, Func<StatementInfo, IReadOnlyDictionary<string, bool>, StatementInfo> factory)
        => _factories[tag] = factory;

    public static StatementInfo Reconstruct(string tag, StatementInfo baseDecl, IReadOnlyDictionary<string, bool> flags)
        => _factories.TryGetValue(tag, out var factory) ? factory(baseDecl, flags) : baseDecl;
}
