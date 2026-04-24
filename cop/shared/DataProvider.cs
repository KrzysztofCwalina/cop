using System.Text.Json;

namespace Cop.Core;

/// <summary>
/// Flags indicating which query formats a provider supports.
/// The engine checks this before calling any QueryXxx method.
/// New formats can be added without breaking existing providers.
/// </summary>
[Flags]
public enum DataFormat
{
    Json = 1,
    // Binary = 2,   // e.g. MessagePack
    InMemoryDatabase = 4,
}

/// <summary>
/// Abstract base class for extensible Cop data providers.
/// Provider DLLs contain a subclass of this and are loaded dynamically by the engine.
///
/// Two main methods:
///   GetSchema() — returns the provider schema as UTF-8 JSON (always implemented).
///   Query()     — returns collection data as UTF-8 JSON (the canonical format).
///
/// Performance-optimized alternative:
///   QueryData() — returns a DataStore (in-memory database) for built-in providers.
///                 Providers that support this set SupportedFormats to include InMemoryDatabase.
/// </summary>
public abstract class DataProvider
{
    /// <summary>
    /// Human-readable provider name, used in diagnostics.
    /// Default strips the "Provider" suffix from the class name.
    /// </summary>
    public override string ToString()
    {
        var name = GetType().Name;
        return name.EndsWith("Provider", StringComparison.Ordinal)
            ? name[..^"Provider".Length]
            : name;
    }
    /// <summary>
    /// Discovers what query formats the provider supports.
    /// The engine checks this before calling <see cref="Query"/> or <see cref="QueryData"/>.
    /// </summary>
    public virtual DataFormat SupportedFormats => DataFormat.Json;

    /// <summary>
    /// Returns the provider schema as UTF-8 JSON.
    /// Describes the types and collections this provider exposes.
    /// All providers must implement this with a real schema (use <see cref="System.Text.Json.Utf8JsonWriter"/>).
    /// </summary>
    public abstract ReadOnlyMemory<byte> GetSchema();

    /// <summary>
    /// Returns CLR runtime bindings: type mappings, lambda property accessors,
    /// collection extractors, and method evaluators. Called once at registration time.
    /// Providers that return InMemoryDatabase format should override this to provide native accessors.
    /// </summary>
    public virtual RuntimeBindings? GetRuntimeBindings() => null;

    /// <summary>
    /// Queries for collection data as UTF-8 JSON.
    /// Only callable if <see cref="SupportedFormats"/> includes <see cref="DataFormat.Json"/>.
    /// </summary>
    public virtual byte[] Query(ProviderQuery query)
        => throw new NotSupportedException("This provider does not support JSON queries.");

    /// <summary>
    /// Queries for collection data as a <see cref="DataStore"/> — an in-memory database
    /// of stride-based <see cref="DataTable"/> records with a shared UTF-8 string heap.
    /// Only callable if <see cref="SupportedFormats"/> includes <see cref="DataFormat.InMemoryDatabase"/>.
    /// This is the fast in-proc path — no serialization overhead.
    /// </summary>
    public virtual DataStore QueryData(ProviderQuery query)
        => throw new NotSupportedException("This provider does not support object queries.");
}

/// <summary>
/// CLR runtime bindings provided by a provider at registration time.
/// Contains type mappings, lambda property accessors, collection extractors, and method evaluators.
/// </summary>
public class RuntimeBindings
{
    /// <summary>
    /// Maps CLR types to cop type names (e.g., typeof(DiskFileInfo) → "DiskFile").
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

/// <summary>
/// Describes what the engine is requesting from a provider.
/// </summary>
public class ProviderQuery
{
    /// <summary>
    /// Root path of the project, or null for non-file-backed providers.
    /// </summary>
    public string? RootPath { get; init; }

    /// <summary>
    /// Which collections the engine needs (null = all).
    /// Allows providers to skip expensive computation for unneeded collections.
    /// </summary>
    public IReadOnlyList<string>? RequestedCollections { get; init; }

    /// <summary>
    /// Pushdown filter expression. Providers that support query optimization
    /// can inspect this to avoid materializing items that will be filtered out.
    /// Providers that don't support pushdown can ignore this — the engine
    /// will apply filters locally as a fallback.
    /// </summary>
    public FilterExpression? Filter { get; init; }

    /// <summary>
    /// Directory names to skip during recursive filesystem scanning.
    /// The caller owns this policy (e.g., .git, node_modules, bin, obj).
    /// Providers that walk directories should prune these during traversal.
    /// </summary>
    public IReadOnlySet<string>? ExcludedDirectories { get; init; }

    /// <summary>
    /// Extensible options bag for future needs (e.g. query language parameters).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

/// <summary>
/// Deserialized schema returned by <see cref="DataProvider.GetSchema"/>.
/// Describes the types and collections a provider exposes.
/// </summary>
public class ProviderSchema
{
    public List<ProviderTypeSchema> Types { get; set; } = [];
    public List<ProviderCollectionSchema> Collections { get; set; } = [];

    /// <summary>
    /// Deserializes a provider schema from UTF-8 JSON bytes.
    /// </summary>
    public static ProviderSchema FromJson(ReadOnlyMemory<byte> utf8Json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<ProviderSchema>(utf8Json.Span, options)
            ?? throw new InvalidOperationException("Failed to deserialize provider schema.");
    }

    /// <summary>
    /// Serializes this schema to UTF-8 JSON using <see cref="System.Text.Json.Utf8JsonWriter"/>.
    /// </summary>
    public ReadOnlyMemory<byte> ToJson()
    {
        var buffer = new System.IO.MemoryStream();
        using var w = new System.Text.Json.Utf8JsonWriter(buffer);
        w.WriteStartObject();

        w.WriteStartArray("types"u8);
        foreach (var type in Types)
        {
            w.WriteStartObject();
            w.WriteString("name"u8, type.Name);
            if (type.Base != null) w.WriteString("base"u8, type.Base);
            w.WriteStartArray("properties"u8);
            foreach (var prop in type.Properties)
            {
                w.WriteStartObject();
                w.WriteString("name"u8, prop.Name);
                if (prop.Type != "string") w.WriteString("type"u8, prop.Type);
                if (prop.Optional) w.WriteBoolean("optional"u8, true);
                if (prop.Collection) w.WriteBoolean("collection"u8, true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("collections"u8);
        foreach (var coll in Collections)
        {
            w.WriteStartObject();
            w.WriteString("name"u8, coll.Name);
            w.WriteString("itemType"u8, coll.ItemType);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteEndObject();
        w.Flush();
        return buffer.TryGetBuffer(out var segment)
            ? segment.AsMemory()
            : buffer.ToArray();
    }
}

/// <summary>
/// Describes a single type in the provider schema.
/// </summary>
public class ProviderTypeSchema
{
    public string Name { get; set; } = "";
    public string? Base { get; set; }
    public List<ProviderPropertySchema> Properties { get; set; } = [];
}

/// <summary>
/// Describes a property within a provider type.
/// </summary>
public class ProviderPropertySchema
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Optional { get; set; }
    public bool Collection { get; set; }
}

/// <summary>
/// Describes a collection exposed by the provider.
/// </summary>
public class ProviderCollectionSchema
{
    public string Name { get; set; } = "";
    public string ItemType { get; set; } = "";
}
