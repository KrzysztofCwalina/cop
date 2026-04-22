namespace Cop.Lang;

/// <summary>
/// Runtime bridge between an cop type schema and CLR objects.
/// </summary>
public class TypeDescriptor
{
    public string Name { get; }
    public TypeDescriptor? BaseType { get; set; }
    public Dictionary<string, PropertyDescriptor> Properties { get; } = new();
    public Func<object, string>? TextConverter { get; set; }

    /// <summary>
    /// Registered method evaluators for this type. Key is method name.
    /// Method evaluators receive (target, args) and return a result.
    /// </summary>
    public Dictionary<string, Func<object, List<object?>, object?>> MethodEvaluators { get; } = new();

    public TypeDescriptor(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Gets a property by name, checking this type and then walking up the base type chain.
    /// </summary>
    public PropertyDescriptor? GetProperty(string name)
    {
        if (Properties.TryGetValue(name, out var prop))
            return prop;
        return BaseType?.GetProperty(name);
    }

    /// <summary>
    /// Gets all properties including inherited ones.
    /// </summary>
    public IEnumerable<PropertyDescriptor> GetAllProperties()
    {
        var seen = new HashSet<string>();
        var current = this;
        while (current is not null)
        {
            foreach (var prop in current.Properties.Values)
            {
                if (seen.Add(prop.Name))
                    yield return prop;
            }
            current = current.BaseType;
        }
    }
}

/// <summary>
/// Describes a single property on a type, with an optional CLR accessor delegate.
/// </summary>
public class PropertyDescriptor
{
    public string Name { get; }
    public string TypeName { get; }
    public bool IsOptional { get; }
    public bool IsCollection { get; }
    public Func<object, object?>? Accessor { get; set; }

    public PropertyDescriptor(string name, string typeName, bool isOptional = false, bool isCollection = false)
    {
        Name = name;
        TypeName = typeName;
        IsOptional = isOptional;
        IsCollection = isCollection;
    }
}
