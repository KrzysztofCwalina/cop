// Describes the types and collections declared by a data provider.

using System.Text.Json;
using Cop.Lang;

namespace Cop.Core;

/// <summary>
/// Deserialized schema returned by <see cref="DataProvider.GetSchema"/>.
/// Describes the types and collections a provider exposes.
/// </summary>
public class ProviderSchema
{
    /// <summary>
    /// The object type definitions exposed by the provider.
    /// </summary>
    public List<ProviderTypeSchema> Types { get; set; } = [];
    /// <summary>
    /// The named collections exposed by the provider.
    /// </summary>
    public List<ProviderCollectionSchema> Collections { get; set; } = [];

    /// <summary>
    /// Deserializes a provider schema from UTF-8 JSON bytes.
    /// </summary>
    public static ProviderSchema FromJson(ReadOnlyMemory<byte> utf8Json)
    {
        JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<ProviderSchema>(utf8Json.Span, options)
            ?? throw new InvalidOperationException("Failed to deserialize provider schema.");
    }

    /// <summary>
    /// Serializes this schema to UTF-8 JSON using <see cref="System.Text.Json.Utf8JsonWriter"/>.
    /// </summary>
    public ReadOnlyMemory<byte> ToJson()
    {
        using MemoryStream buffer = new System.IO.MemoryStream();
        using Utf8JsonWriter w = new System.Text.Json.Utf8JsonWriter(buffer);
        w.WriteStartObject();

        w.WriteStartArray("types"u8);
        foreach (ProviderTypeSchema type in Types)
        {
            w.WriteStartObject();
            w.WriteString("name"u8, type.Name);
            if (type.Base != null)
            {
                w.WriteString("base"u8, type.Base);
            }

            w.WriteStartArray("properties"u8);
            foreach (ProviderPropertySchema prop in type.Properties)
            {
                w.WriteStartObject();
                w.WriteString("name"u8, prop.Name);
                if (prop.Type != "string")
                {
                    w.WriteString("type"u8, prop.Type);
                }

                if (prop.IsOptional)
                {
                    w.WriteBoolean("optional"u8, true);
                }

                if (prop.IsCollection)
                {
                    w.WriteBoolean("collection"u8, true);
                }

                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("collections"u8);
        foreach (ProviderCollectionSchema coll in Collections)
        {
            w.WriteStartObject();
            w.WriteString("name"u8, coll.Name);
            w.WriteString("itemType"u8, coll.ItemType);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteEndObject();
        w.Flush();
        return buffer.TryGetBuffer(out ArraySegment<byte> segment)
            ? segment.AsMemory()
            : buffer.ToArray();
    }
}
