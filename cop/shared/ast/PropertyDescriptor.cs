// Describes runtime property access and the shape of the exposed Cop value.

namespace Cop.Lang;

/// <summary>
/// Describes a single property on a type, with an optional CLR accessor delegate.
/// </summary>
public class PropertyDescriptor
{
    /// <summary>The property name exposed to Cop member access.</summary>
    public string Name { get; }

    /// <summary>The Cop type name of the property or its collection elements.</summary>
    public string TypeName { get; }

    /// <summary>Whether the property's value may be absent.</summary>
    public bool IsOptional { get; }

    /// <summary>Whether the property contains multiple values.</summary>
    public bool IsCollection { get; }

    /// <summary>
    /// The optional delegate that reads the property from a CLR instance.
    /// </summary>
    public Func<object, object?>? Accessor { get; set; }

    /// <summary>
    /// Creates a property schema with its value type and collection shape.
    /// </summary>
    public PropertyDescriptor(string name, string typeName, bool isOptional = false, bool isCollection = false)
    {
        Name = name;
        TypeName = typeName;
        IsOptional = isOptional;
        IsCollection = isCollection;
    }
}
