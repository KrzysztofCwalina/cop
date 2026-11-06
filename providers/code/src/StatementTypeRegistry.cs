// Reconstructs language-specific methods and statements from source caches.

namespace Cop.Providers.SourceModel;

/// <summary>
/// Reconstructs language-specific <see cref="StatementInfo"/> subtypes when loading the source
/// cache. Mirrors <see cref="MethodTypeRegistry"/>. Providers that don't cache don't need this.
/// </summary>
public static class StatementTypeRegistry
{
    /// <summary>
    /// Registers reconstruction of language-specific statements from shared cached facts.
    /// </summary>
    public static void Register(string tag, Func<StatementInfo, IReadOnlyDictionary<string, bool>, StatementInfo> factory)
        => LanguageCacheFactories<StatementInfo>.Register(tag, factory);

    /// <summary>
    /// Restores a language-specific statement from shared cached facts and flags.
    /// </summary>
    public static StatementInfo Reconstruct(string tag, StatementInfo baseDecl, IReadOnlyDictionary<string, bool> flags)
        => Reconstruct(tag, baseDecl, flags, System.Runtime.Loader.AssemblyLoadContext.Default);

    internal static StatementInfo Reconstruct(string tag, StatementInfo baseDecl, IReadOnlyDictionary<string, bool> flags,
        System.Runtime.Loader.AssemblyLoadContext context) => LanguageCacheFactories<StatementInfo>.Reconstruct(tag, baseDecl, flags, context);
}
