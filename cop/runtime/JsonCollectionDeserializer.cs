using System.Text.Json;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Deserializes UTF-8 JSON from a <see cref="DataProvider.Query"/> response
/// into ScriptObject instances using the provider's type schema.
/// </summary>
public static class JsonCollectionDeserializer
{
    /// <summary>
    /// Deserializes the top-level JSON object (collection name → array of items)
    /// into a dictionary of collection name → list of ScriptObjects.
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

    private static ScriptObject DeserializeObject(JsonElement element, string typeName, Dictionary<string, ProviderTypeSchema> typeMap)
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

        return new ScriptObject(typeName, fields);
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
    /// along with ScriptObject-based property accessors.
    /// Skips types that are already registered (e.g. from .cop file definitions).
    /// Used by JSON-format providers whose data arrives as ScriptObjects.
    /// </summary>
    public static void RegisterSchema(TypeRegistry registry, ProviderSchema schema)
    {
        // Register type descriptors and collections via the shared helper
        registry.RegisterProviderSchema(schema);

        // Register ScriptObject-based property accessors (JSON providers only)
        RegisterScriptObjectAccessors(registry, schema);
    }

    /// <summary>
    /// Registers ScriptObject-based property accessors for all types in a provider schema.
    /// Only needed for JSON-format providers whose data is deserialized into ScriptObjects.
    /// Objects-format providers supply their own CLR lambda accessors via GetRuntimeBindings().
    /// </summary>
    public static void RegisterScriptObjectAccessors(TypeRegistry registry, ProviderSchema schema)
    {
        foreach (var ts in schema.Types)
        {
            var accessors = new Dictionary<string, Func<object, object?>>();
            foreach (var ps in ts.Properties)
            {
                var propName = ps.Name;
                accessors[propName] = obj => obj is ScriptObject so ? so.GetField(propName) : null;
            }
            registry.RegisterAccessors(ts.Name, accessors);
        }
    }
}
