using System.Text.Json;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Deserializes UTF-8 JSON from a <see cref="CopProvider.QueryJson"/> response
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
            throw new InvalidOperationException("Provider QueryJson must return a JSON object.");

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
    /// </summary>
    public static void RegisterSchema(TypeRegistry registry, ProviderSchema schema)
    {
        var typeMap = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // First pass: register type descriptors for types not already in the registry
        foreach (var ts in schema.Types)
        {
            if (registry.HasType(ts.Name))
                continue;

            var desc = new TypeDescriptor(ts.Name);
            foreach (var ps in ts.Properties)
            {
                desc.Properties[ps.Name] = new PropertyDescriptor(ps.Name, ps.Type, ps.Optional, ps.Collection);
            }
            registry.Register(desc);
        }

        // Second pass: resolve base types
        foreach (var ts in schema.Types)
        {
            if (ts.Base is null) continue;
            var desc = registry.GetType(ts.Name);
            var baseDesc = registry.GetType(ts.Base);
            if (desc is not null && baseDesc is not null && desc.BaseType is null)
                desc.BaseType = baseDesc;
        }

        // Third pass: register ScriptObject-based property accessors
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

        // Register ScriptObject CLR type mapping for all provider types
        // (TypeRegistry.InferTypeName already handles ScriptObject via TypeName property)

        // Register collections
        foreach (var cs in schema.Collections)
        {
            if (!registry.HasCollection(cs.Name))
            {
                registry.RegisterCollection(new CollectionDeclaration(cs.Name, cs.ItemType, 0, true));
            }
        }
    }
}
