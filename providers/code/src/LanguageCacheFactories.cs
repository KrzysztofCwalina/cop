// Reconstructs language-specific declarations from cached source models.

using System.Collections.Concurrent;
using System.Runtime.Loader;

namespace Cop.Providers.SourceModel;

internal static class LanguageCacheFactories<T>
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Runtime.Loader.AssemblyLoadContext,
        System.Collections.Concurrent.ConcurrentDictionary<string, Func<T, IReadOnlyDictionary<string, bool>, T>>> s_factories = new();

    /// <summary>
    /// Registers reconstruction of language-specific declarations from cached shared
    /// facts.
    /// </summary>
    public static void Register(string tag, Func<T, IReadOnlyDictionary<string, bool>, T> factory)
    {
        AssemblyLoadContext context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(factory.Method.Module.Assembly)
            ?? System.Runtime.Loader.AssemblyLoadContext.Default;
        s_factories.GetOrCreateValue(context)[tag] = factory;
    }

    /// <summary>
    /// Restores language-specific declarations using the registered cache factory.
    /// </summary>
    public static T Reconstruct(string tag, T value, IReadOnlyDictionary<string, bool> flags,
        System.Runtime.Loader.AssemblyLoadContext context) =>
        s_factories.TryGetValue(context, out ConcurrentDictionary<string, Func<T, IReadOnlyDictionary<string, bool>, T>>? factories) && factories.TryGetValue(tag, out Func<T, IReadOnlyDictionary<string, bool>, T>? factory)
            ? factory(value, flags) : value;
}
