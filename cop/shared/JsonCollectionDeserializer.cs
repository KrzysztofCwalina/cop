using System.Text.Json;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Deserializes UTF-8 JSON from a <see cref="DataProvider.Query"/> response
/// into DataObject instances using the provider's type schema.
/// Also provides direct array deserialization for Parse('file.json', [Type]).
/// </summary>
public static class JsonCollectionDeserializer
{
    /// <summary>
    /// Deserializes a top-level JSON array into a list of DataObjects
    /// using the specified type name and its schema for field mapping.
    /// Used by Parse('file.json', [Type]) to load user-defined typed collections.
    /// </summary>
    public static List<object> DeserializeArray(byte[] utf8Json, string typeName, ProviderSchema schema)
    {
        var typeMap = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(utf8Json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Parse() expects a JSON array, but got {root.ValueKind}.");

        var items = new List<object>();
        foreach (var elem in root.EnumerateArray())
        {
            items.Add(DeserializeElement(elem, typeName, typeMap));
        }
        return items;
    }

    /// <summary>
    /// Deserializes the top-level JSON object (collection name → array of items)
    /// into a dictionary of collection name → list of DataObjects.
    /// </summary>
    public static Dictionary<string, List<object>> Deserialize(byte[] utf8Json, ProviderSchema schema)
    {
        var typeMap = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var collMap = schema.Collections.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var result = new Dictionary<string, List<object>>();

        using var doc = JsonDocument.Parse(utf8Json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Provider Query must return a JSON object.");

        foreach (var prop in root.EnumerateObject())
        {
            if (!collMap.TryGetValue(prop.Name, out var collSchema))
                continue;

            if (prop.Value.ValueKind != JsonValueKind.Array)
                continue;

            var items = new List<object>();
            foreach (var elem in prop.Value.EnumerateArray())
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
            JsonValueKind.Number => element.TryGetInt32(out var i) ? i : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            JsonValueKind.Array => DeserializeArray(element, typeName, typeMap),
            _ => element.ToString()
        };
    }

    private static DataObject DeserializeObject(JsonElement element, string typeName, Dictionary<string, ProviderTypeSchema> typeMap)
    {
        var fields = new Dictionary<string, object?>();
        ProviderTypeSchema? typeSchema = typeMap.GetValueOrDefault(typeName);

        foreach (var prop in element.EnumerateObject())
        {
            var propSchema = typeSchema?.Properties.Find(p => p.Name == prop.Name);
            var propTypeName = propSchema?.Type ?? "string";

            if (propSchema is { Collection: true })
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    fields[prop.Name] = DeserializeArray(prop.Value, propTypeName, typeMap);
                else
                    fields[prop.Name] = new List<object>();
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
        var items = new List<object>();
        foreach (var elem in element.EnumerateArray())
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
        foreach (var ts in schema.Types)
        {
            var accessors = new Dictionary<string, Func<object, object?>>();
            foreach (var ps in ts.Properties)
            {
                var propName = ps.Name;
                accessors[propName] = obj => obj is DataObject so ? so.GetField(propName) : null;
            }
            registry.RegisterAccessors(ts.Name, accessors);
        }
    }
}
