namespace Cop.Lang;

/// <summary>
/// A runtime object produced by a function transformation.
/// Carries its cop type name so the runtime can resolve properties and infer types.
/// </summary>
public class DataObject
{
    public string TypeName { get; }
    private readonly Dictionary<string, object?> _fields;

    /// <summary>
    /// Optional lazy field resolver. Called when a field is not found in the dictionary.
    /// The result is memoized — subsequent accesses return the cached value.
    /// This enables Haskell-style lazy evaluation: fields are thunks forced on first access.
    /// </summary>
    private Func<string, object?>? _fieldResolver;

    public DataObject(string typeName, Dictionary<string, object?> fields)
    {
        TypeName = typeName;
        // Ensure case-insensitive field lookup (consistent with Cop string semantics)
        if (fields.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            _fields = new Dictionary<string, object?>(fields, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            _fields = fields;
        }
    }

    public DataObject(string typeName) : this(typeName, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase))
    {
    }

    /// <summary>
    /// Sets a lazy field resolver for on-demand field evaluation.
    /// When GetField is called for a field not in the dictionary, the resolver is invoked
    /// and the result is memoized (stored in the dictionary for future access).
    /// </summary>
    public DataObject WithFieldResolver(Func<string, object?> resolver)
    {
        _fieldResolver = resolver;
        return this;
    }

    public void Set(string name, object? value) => _fields[name] = value;

    public object? GetField(string name)
    {
        if (_fields.TryGetValue(name, out var value)) return value;
        if (_fieldResolver is not null)
        {
            var resolved = _fieldResolver(name);
            _fields[name] = resolved; // memoize
            return resolved;
        }
        return null;
    }

    public bool HasField(string name) => _fields.ContainsKey(name) || _fieldResolver is not null;

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
            DataObject obj => obj.ToJson(indent),
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
