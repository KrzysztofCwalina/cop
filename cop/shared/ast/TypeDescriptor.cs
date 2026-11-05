// Represents type names and type relationships used during query evaluation.

namespace Cop.Lang;

/// <summary>
/// Runtime bridge between an cop type schema and CLR objects.
/// </summary>
public class TypeDescriptor
{
    /// <summary>The Cop name used to resolve this runtime type.</summary>
    public string Name { get; }

    /// <summary>The base type searched for inherited properties.</summary>
    public TypeDescriptor? BaseType { get; set; }

    /// <summary>Properties declared directly on this type, indexed by name.</summary>
    public Dictionary<string, PropertyDescriptor> Properties { get; } = new();

    /// <summary>An optional converter from a CLR value to its display text.</summary>
    public Func<object, string>? TextConverter { get; set; }

    /// <summary>
    /// True if this type was registered by a built-in provider schema.
    /// Package .cop type definitions merge into provider types rather than conflicting.
    /// </summary>
    public bool IsProviderType { get; set; }

    /// <summary>
    /// Registered method evaluators for this type. Key is method name.
    /// Method evaluators receive (target, args) and return a result.
    /// </summary>
    public Dictionary<string, Func<object, List<object?>, object?>> MethodEvaluators { get; } = new();

    /// <summary>
    /// Creates a runtime type descriptor with no registered properties.
    /// </summary>
    public TypeDescriptor(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Gets a property by name, checking this type and then walking up the base type chain.
    /// </summary>
    public PropertyDescriptor? GetProperty(string name)
    {
        if (Properties.TryGetValue(name, out PropertyDescriptor? prop))
        {
            return prop;
        }

        return BaseType?.GetProperty(name);
    }

    /// <summary>
    /// Gets all properties including inherited ones.
    /// </summary>
    public IEnumerable<PropertyDescriptor> GetAllProperties()
    {
        HashSet<string> seen = new HashSet<string>();
        TypeDescriptor? current = this;
        while (current is not null)
        {
            foreach (PropertyDescriptor prop in current.Properties.Values)
            {
                if (seen.Add(prop.Name))
                {
                    yield return prop;
                }
            }
            current = current.BaseType;
        }
    }
}
