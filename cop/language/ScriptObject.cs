namespace Cop.Lang;

/// <summary>
/// A runtime object produced by a function transformation.
/// Carries its cop type name so the runtime can resolve properties and infer types.
/// </summary>
public class ScriptObject
{
    public string TypeName { get; }
    private readonly Dictionary<string, object?> _fields;

    public ScriptObject(string typeName, Dictionary<string, object?> fields)
    {
        TypeName = typeName;
        _fields = fields;
    }

    public object? GetField(string name) =>
        _fields.TryGetValue(name, out var value) ? value : null;

    public bool HasField(string name) => _fields.ContainsKey(name);

    public IReadOnlyDictionary<string, object?> Fields => _fields;

    /// <summary>
    /// Serializes this object to a JSON string.
    /// </summary>
    public string ToJson(int indent = 0)
    {
        var sb = new System.Text.StringBuilder();
        var pad = new string(' ', indent);
        var inner = new string(' ', indent + 4);
        sb.AppendLine("{");
        int i = 0;
        foreach (var (key, value) in _fields)
        {
            sb.Append(inner);
            sb.Append($"\"{key}\": ");
            sb.Append(FormatJsonValue(value, indent + 4));
            if (++i < _fields.Count) sb.Append(',');
            sb.AppendLine();
        }
        sb.Append(pad).Append('}');
        return sb.ToString();
    }

    private static string FormatJsonValue(object? value, int indent)
    {
        return value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            int n => n.ToString(),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string s => $"\"{EscapeJson(s)}\"",
            ScriptObject obj => obj.ToJson(indent),
            System.Collections.IList list => FormatJsonArray(list, indent),
            _ => $"\"{EscapeJson(value.ToString() ?? "")}\"",
        };
    }

    private static string FormatJsonArray(System.Collections.IList list, int indent)
    {
        if (list.Count == 0) return "[]";
        var sb = new System.Text.StringBuilder();
        var inner = new string(' ', indent + 4);
        sb.AppendLine("[");
        for (int i = 0; i < list.Count; i++)
        {
            sb.Append(inner);
            sb.Append(FormatJsonValue(list[i], indent + 4));
            if (i < list.Count - 1) sb.Append(',');
            sb.AppendLine();
        }
        sb.Append(new string(' ', indent)).Append(']');
        return sb.ToString();
    }

    private static string EscapeJson(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r")
        .Replace("\t", "\\t");

    public override string ToString() => $"{TypeName} {{ {string.Join(", ", _fields.Select(kv => $"{kv.Key} = {kv.Value}"))} }}";
}
