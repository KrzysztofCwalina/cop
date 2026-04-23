using System.Text.Json;

namespace Cop.Core;

/// <summary>
/// Flags indicating which query formats a provider supports.
/// The engine checks this before calling any QueryXxx method.
/// New formats can be added without breaking existing providers.
/// </summary>
[Flags]
public enum ProviderFormat
{
    Json = 1,
    // Future:
    // Binary = 2,   // e.g. MessagePack
    // Structs = 4,  // direct struct arrays
}

/// <summary>
/// Abstract base class for extensible Cop data providers.
/// Provider DLLs contain a subclass of this and are loaded dynamically by the engine.
/// </summary>
public abstract class CopProvider
{
    /// <summary>
    /// Discovers what query formats the provider supports.
    /// The engine checks this before calling any QueryXxx method.
    /// </summary>
    public virtual ProviderFormat SupportedFormats => ProviderFormat.Json;

    /// <summary>
    /// Returns the provider schema as UTF-8 JSON.
    /// Describes the types and collections this provider exposes.
    /// </summary>
    public abstract byte[] GetSchema();

    /// <summary>
    /// Queries for collection data as UTF-8 JSON.
    /// Only callable if <see cref="SupportedFormats"/> includes <see cref="ProviderFormat.Json"/>.
    /// </summary>
    public virtual byte[] QueryJson(ProviderQuery query)
        => throw new NotSupportedException("This provider does not support JSON queries.");

    // Future protocol methods — added without breaking existing providers:
    // public virtual byte[] QueryBinary(ProviderQuery query) => throw new NotSupportedException();
    // public virtual ReadOnlySpan<byte> QueryStructs(ProviderQuery query) => throw new NotSupportedException();
}

/// <summary>
/// Describes what the engine is requesting from a provider.
/// </summary>
public class ProviderQuery
{
    /// <summary>
    /// Root path of the codebase, or null for non-file-backed providers.
    /// </summary>
    public string? CodebasePath { get; init; }

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
    /// Extensible options bag for future needs (e.g. query language parameters).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

/// <summary>
/// Deserialized schema returned by <see cref="CopProvider.GetSchema"/>.
/// Describes the types and collections a provider exposes.
/// </summary>
public class ProviderSchema
{
    public List<ProviderTypeSchema> Types { get; set; } = [];
    public List<ProviderCollectionSchema> Collections { get; set; } = [];

    /// <summary>
    /// Deserializes a provider schema from UTF-8 JSON bytes.
    /// </summary>
    public static ProviderSchema FromJson(byte[] utf8Json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<ProviderSchema>(utf8Json, options)
            ?? throw new InvalidOperationException("Failed to deserialize provider schema.");
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
