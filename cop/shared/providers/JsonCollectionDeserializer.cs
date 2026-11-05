// Converts provider JSON collections into typed runtime records.

using System.Text.Json;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Deserializes UTF-8 JSON from a <see cref="ObjectProvider.Query"/> response
/// into DataObject instances using the provider's type schema.
/// Also provides direct deserialization for json.Parse('file.json', 'TypeName').
/// </summary>
public static class JsonCollectionDeserializer
{
    /// <summary>
    /// Deserializes a JSON file into either a typed collection (if root is array)
    /// or a single typed object (if root is object). Primitives are returned as-is.
    /// </summary>
    public static object DeserializeAny(byte[] utf8Json, string typeName, ProviderSchema schema)
    {
        Dictionary<string, ProviderTypeSchema> typeMap = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(utf8Json);
        JsonElement root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            List<object> items = new List<object>();
            foreach (JsonElement elem in root.EnumerateArray())
            {
                items.Add(DeserializeElement(elem, typeName, typeMap));
            }
            return items;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            return DeserializeObject(root, typeName, typeMap);
        }

        return DeserializeElement(root, typeName, typeMap);
    }

    /// <summary>
    /// Deserializes the top-level JSON object (collection name → array of items)
    /// into a dictionary of collection name → list of DataObjects.
    /// </summary>
    public static Dictionary<string, List<object>> Deserialize(byte[] utf8Json, ProviderSchema schema)
    {
        Dictionary<string, ProviderTypeSchema> typeMap = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Dictionary<string, ProviderCollectionSchema> collMap = schema.Collections.ToDictionary(c => c.Name, StringComparer.Ordinal);
        Dictionary<string, List<object>> result = new Dictionary<string, List<object>>();

        using JsonDocument doc = JsonDocument.Parse(utf8Json);
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Provider Query must return a JSON object.");
        }

        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (!collMap.TryGetValue(prop.Name, out ProviderCollectionSchema? collSchema))
            {
                continue;
            }

            if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            List<object> items = new List<object>();
            foreach (JsonElement elem in prop.Value.EnumerateArray())
            {
                items.Add(DeserializeElement(elem, collSchema.ItemType, typeMap));
            }
            result[prop.Name] = items;
        }

        return result;
    }

    private static object DeserializeElement(JsonElement element, string typeName, Dictionary<string, ProviderTypeSchema> typeMap)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => DeserializeObject(element, typeName, typeMap),
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.TryGetInt32(out int i) ? i : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            JsonValueKind.Array => DeserializeArray(element, typeName, typeMap),
            _ => element.ToString()
        };
    }

    private static DataObject DeserializeObject(JsonElement element, string typeName, Dictionary<string, ProviderTypeSchema> typeMap)
    {
        Dictionary<string, object?> fields = new Dictionary<string, object?>();
        ProviderTypeSchema? typeSchema = typeMap.GetValueOrDefault(typeName);

        foreach (JsonProperty prop in element.EnumerateObject())
        {
            ProviderPropertySchema? propSchema = typeSchema?.Properties.Find(p => p.Name == prop.Name);
            string propTypeName = propSchema?.Type ?? "string";

            if (propSchema is { Collection: true })
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    fields[prop.Name] = DeserializeArray(prop.Value, propTypeName, typeMap);
                }
                else
                {
                    fields[prop.Name] = new List<object>();
                }
            }
            else
            {
                fields[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.Object => DeserializeObject(prop.Value, propTypeName, typeMap),
                    _ => DeserializeElement(prop.Value, propTypeName, typeMap)
                };
            }
        }

        return new DataObject(typeName, fields);
    }

    private static List<object> DeserializeArray(JsonElement element, string itemTypeName, Dictionary<string, ProviderTypeSchema> typeMap)
    {
        List<object> items = new List<object>();
        foreach (JsonElement elem in element.EnumerateArray())
        {
            items.Add(DeserializeElement(elem, itemTypeName, typeMap));
        }
        return items;
    }

    /// <summary>
    /// Registers types from a <see cref="ProviderSchema"/> into the <see cref="TypeRegistry"/>,
    /// along with DataObject-based property accessors.
    /// Skips types that are already registered (e.g. from .cop file definitions).
    /// Used by JSON-format providers whose data arrives as DataObjects.
    /// </summary>
    public static void RegisterSchema(TypeRegistry registry, ProviderSchema schema)
    {
        // Register type descriptors and collections via the shared helper
        registry.RegisterProviderSchema(schema);

        // Register DataObject-based property accessors (JSON providers only)
        RegisterDataObjectAccessors(registry, schema);
    }

    /// <summary>
    /// Registers DataObject-based property accessors for all types in a provider schema.
    /// Only needed for JSON-format providers whose data is deserialized into DataObjects.
    /// Objects-format providers supply their own CLR lambda accessors via GetRuntimeBindings().
    /// </summary>
    public static void RegisterDataObjectAccessors(TypeRegistry registry, ProviderSchema schema)
    {
        foreach (ProviderTypeSchema ts in schema.Types)
        {
            Dictionary<string, Func<object, object?>> accessors = new Dictionary<string, Func<object, object?>>();
            foreach (ProviderPropertySchema ps in ts.Properties)
            {
                string propName = ps.Name;
                accessors[propName] = obj => obj is DataObject so ? so.GetField(propName) : null;
            }
            registry.RegisterAccessors(ts.Name, accessors);
        }
    }
}
