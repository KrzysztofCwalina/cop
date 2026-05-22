using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// JSON file provider. Loads a JSON file and returns its contents as a collection.
/// If the root is an array, returns a single "Items" collection.
/// If the root is an object, returns it as a single-item "Items" collection.
/// Properties are resolved dynamically from JSON keys.
/// </summary>
public sealed class JsonProvider : DataProvider
{
    public override ReadOnlyMemory<byte> GetSchema()
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteStartArray("types");
        w.WriteEndArray();
        w.WriteStartArray("collections");
        w.WriteStartObject();
        w.WriteString("name", "Items");
        w.WriteString("itemType", "object");
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
        return ms.ToArray();
    }

    public override object? Query(ProviderQuery query)
    {
        if (string.IsNullOrEmpty(query.RootPath))
            return null;

        var filePath = query.RootPath;

        // If RootPath is a directory (startup query), return empty — JSON loads on-demand
        if (Directory.Exists(filePath))
            return null;

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"JSON file not found: '{filePath}'");

        var json = File.ReadAllBytes(filePath);
        var items = ParseJson(json);

        // Apply filter pushdown if present
        if (query.Filter is not null)
        {
            items = items.Where(item => FilterEvaluator.Matches(query.Filter,
                prop => GetFieldValue(item, prop))).ToList();
        }

        return new Dictionary<string, List<object>>(StringComparer.Ordinal)
        {
            ["Items"] = items
        };
    }

    private static object? GetFieldValue(object item, string propertyName)
    {
        if (item is DataObject dataObj && dataObj.Fields.TryGetValue(propertyName, out var value))
            return value;
        return null;
    }

    private static List<object> ParseJson(byte[] utf8Json)
    {
        using var doc = JsonDocument.Parse(utf8Json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var items = new List<object>();
            foreach (var elem in root.EnumerateArray())
            {
                if (elem.ValueKind == JsonValueKind.Object)
                    items.Add(ElementToDataObject(elem));
                else
                    items.Add(ElementToValue(elem));
            }
            return items;
        }

        if (root.ValueKind == JsonValueKind.Object)
            return [ElementToDataObject(root)];

        return [ElementToValue(root)];
    }

    private static DataObject ElementToDataObject(JsonElement elem)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in elem.EnumerateObject())
        {
            fields[prop.Name] = ElementToValue(prop.Value);
        }
        return new DataObject("object", fields);
    }

    private static object ElementToValue(JsonElement elem) => elem.ValueKind switch
    {
        JsonValueKind.String => elem.GetString() ?? "",
        JsonValueKind.Number when elem.TryGetInt32(out var i) => i,
        JsonValueKind.Number => elem.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => "",
        JsonValueKind.Array => ParseNestedArray(elem),
        JsonValueKind.Object => ElementToDataObject(elem),
        _ => elem.ToString() ?? ""
    };

    private static List<object> ParseNestedArray(JsonElement elem)
    {
        var items = new List<object>();
        foreach (var child in elem.EnumerateArray())
        {
            if (child.ValueKind == JsonValueKind.Object)
                items.Add(ElementToDataObject(child));
            else
                items.Add(ElementToValue(child));
        }
        return items;
    }
}
