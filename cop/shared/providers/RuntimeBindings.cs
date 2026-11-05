// CLR runtime bindings provided by a provider at registration time.

using System.Text.Json;
using Cop.Lang;

namespace Cop.Core;

/// <summary>
/// CLR runtime bindings provided by a provider at registration time.
/// Contains type mappings, lambda property accessors, collection extractors, and method evaluators.
/// </summary>
public class RuntimeBindings
{
    /// <summary>
    /// Maps CLR types to cop type names (e.g., typeof(DiskFileInfo) → "File").
    /// </summary>
    public Dictionary<Type, string> ClrTypeMappings { get; init; } = new();

    /// <summary>
    /// Lambda property accessors keyed by cop type name then property name.
    /// </summary>
    public Dictionary<string, Dictionary<string, Func<object, object?>>> Accessors { get; init; } = new();

    /// <summary>
    /// Per-document collection extractors (e.g., extract Types from a SourceFile document).
    /// Keyed by collection name. Only used by providers whose data comes from parsed documents.
    /// </summary>
    public Dictionary<string, Func<object, List<object>>>? CollectionExtractors { get; init; }

    /// <summary>
    /// Method evaluators keyed by (typeName, methodName).
    /// </summary>
    public Dictionary<(string TypeName, string MethodName), Func<object, List<object?>, object?>>? MethodEvaluators { get; init; }

    /// <summary>
    /// Per-type text converters (e.g., TypeReference → its OriginalText).
    /// </summary>
    public Dictionary<string, Func<object, string>>? TextConverters { get; init; }
}
