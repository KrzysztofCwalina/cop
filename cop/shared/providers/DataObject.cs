// Represents provider records and their dynamically accessible fields.

using System.Text;

namespace Cop.Lang;

/// <summary>
/// A runtime object produced by a function transformation.
/// Carries its cop type name so the runtime can resolve properties and infer types.
/// </summary>
public class DataObject
{
    /// <summary>
    /// The provider-declared Cop type used for member binding and type tests.
    /// </summary>
    public string TypeName { get; internal set; }
    private readonly Dictionary<string, object?> _fields;

    /// <summary>
    /// Optional lazy field resolver. Called when a field is not found in the dictionary.
    /// The result is memoized — subsequent accesses return the cached value.
    /// This enables Haskell-style lazy evaluation: fields are thunks forced on first access.
    /// </summary>
    private Func<string, object?>? _fieldResolver;
    private Func<string, CancellationToken, ValueTask<object?>>? _asyncFieldResolver;
    private readonly System.Threading.Lock _fieldGate = new();
    private readonly Dictionary<string, Lazy<Task<object?>>> _pendingFields =
        new(StringComparer.OrdinalIgnoreCase);

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
        ArgumentNullException.ThrowIfNull(resolver);
        _fieldResolver = resolver;
        _asyncFieldResolver = null;
        return this;
    }

    /// <summary>Sets an awaited, memoized resolver without preparing any members eagerly.</summary>
    public DataObject WithAsyncFieldResolver(
        Func<string, CancellationToken, ValueTask<object?>> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _asyncFieldResolver = resolver;
        _fieldResolver = null;
        return this;
    }

    /// <summary>
    /// Stores or replaces a provider member value by name.
    /// </summary>
    public void Set(string name, object? value)
    {
        lock (_fieldGate)
        {
            _fields[name] = value;
        }
    }

    /// <summary>
    /// Reads a stored member or resolves it through the optional lazy field resolver.
    /// </summary>
    public object? GetField(string name)
    {
        lock (_fieldGate)
        {
            if (_fields.TryGetValue(name, out object? value))
            {
                return value;
            }
        }
        if (_asyncFieldResolver is not null)
        {
            throw new InvalidOperationException($"Member '{name}' requires GetFieldAsync.");
        }

        if (_fieldResolver is not null)
        {
            object? resolved = _fieldResolver(name);
            Set(name, resolved);
            return resolved;
        }
        return null;
    }

    /// <summary>Awaits one demanded field, sharing in-flight resolution with concurrent readers.</summary>
    public async ValueTask<object?> GetFieldAsync(string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Lazy<Task<object?>>? pending = null;
        lock (_fieldGate)
        {
            if (_fields.TryGetValue(name, out object? value))
            {
                return value;
            }
            if (_asyncFieldResolver is not null)
            {
                if (!_pendingFields.TryGetValue(name, out pending))
                {
                    Func<string, CancellationToken, ValueTask<object?>> resolver = _asyncFieldResolver;
                    pending = new(() => ResolveFieldAsync(name, resolver, cancellationToken));
                    _pendingFields.Add(name, pending);
                }
            }
        }
        return pending is null ? GetField(name)
            : await pending.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> ResolveFieldAsync(string name,
        Func<string, CancellationToken, ValueTask<object?>> resolver,
        CancellationToken cancellationToken)
    {
        try
        {
            object? resolved = await resolver(name, cancellationToken).ConfigureAwait(false);
            lock (_fieldGate)
            {
                if (_fields.TryGetValue(name, out object? existing))
                {
                    return existing;
                }
                _fields[name] = resolved;
            }
            return resolved;
        }
        finally
        {
            lock (_fieldGate)
            {
                _pendingFields.Remove(name);
            }
        }
    }

    /// <summary>
    /// Tests whether a stored member or lazy resolver can provide the named field.
    /// </summary>
    public bool HasField(string name)
    {
        lock (_fieldGate)
        {
            return _fields.ContainsKey(name) || _fieldResolver is not null
                || _asyncFieldResolver is not null;
        }
    }

    /// <summary>
    /// The stored provider member values, excluding lazily resolved members.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Fields => _fields;

    /// <summary>
    /// Serializes this object to a JSON string.
    /// </summary>
    public string ToJson(int indent = 0)
    {
        StringBuilder sb = new System.Text.StringBuilder();
        string pad = new string(' ', indent);
        string inner = new string(' ', indent + 4);
        sb.AppendLine("{");
        int i = 0;
        foreach ((string? key, object? value) in _fields)
        {
            sb.Append(inner);
            sb.Append($"\"{key}\": ");
            sb.Append(FormatJsonValue(value, indent + 4));
            if (++i < _fields.Count)
            {
                sb.Append(',');
            }

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
        if (list.Count == 0)
        {
            return "[]";
        }

        StringBuilder sb = new System.Text.StringBuilder();
        string inner = new string(' ', indent + 4);
        sb.AppendLine("[");
        for (int i = 0; i < list.Count; i++)
        {
            sb.Append(inner);
            sb.Append(FormatJsonValue(list[i], indent + 4));
            if (i < list.Count - 1)
            {
                sb.Append(',');
            }

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

    /// <inheritdoc/>
    public override string ToString() => $"{TypeName} {{ {string.Join(", ", _fields.Select(kv => $"{kv.Key} = {kv.Value}"))} }}";
}
