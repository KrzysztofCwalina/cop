// Represents parsed TypeSpec namespaces, declarations, decorators, and type references.

namespace TypeSpecProvider;

/// <summary>
/// Represents a complete TypeSpec compilation unit (one or more files).
/// </summary>
public class TspSpec
{
    /// <summary>Gets or sets the namespaces declared by the input files.</summary>
    public List<TspNamespace> Namespaces { get; set; } = [];
    /// <summary>Gets or sets the object models declared by the input files.</summary>
    public List<TspModel> Models { get; set; } = [];
    /// <summary>Gets or sets operations declared outside interfaces.</summary>
    public List<TspOperation> Operations { get; set; } = [];
    /// <summary>Gets or sets operation interfaces declared by the input files.</summary>
    public List<TspInterface> Interfaces { get; set; } = [];
    /// <summary>Gets or sets the declared enumerations.</summary>
    public List<TspEnum> Enums { get; set; } = [];
    /// <summary>Gets or sets the declared union types.</summary>
    public List<TspUnion> Unions { get; set; } = [];
    /// <summary>Gets or sets the declared scalar types.</summary>
    public List<TspScalar> Scalars { get; set; } = [];
}

/// <summary>Represents a TypeSpec namespace and its attached decorators.</summary>
public class TspNamespace
{
    /// <summary>Gets or sets the namespace's local name.</summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// Gets or sets the namespace name including its enclosing namespaces.
    /// </summary>
    public string FullName { get; set; } = "";
    /// <summary>Gets or sets decorators attached to the namespace.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}

/// <summary>
/// Represents a TypeSpec decorator invocation and its parsed arguments.
/// </summary>
public class TspDecorator
{
    /// <summary>Gets or sets the decorator name without the @ prefix.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the decorator argument expressions.</summary>
    public List<string> Arguments { get; set; } = [];
}

/// <summary>
/// Represents a TypeSpec object model, its properties, and inheritance.
/// </summary>
public class TspModel
{
    /// <summary>Gets or sets the model's declared name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets the model's declared properties.</summary>
    public List<TspProperty> Properties { get; set; } = [];
    /// <summary>Gets or sets the extended model's type expression, if any.</summary>
    public string? BaseModel { get; set; }
    /// <summary>Gets or sets decorators attached to the model.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
    /// <summary>Gets or sets the model's template parameter names.</summary>
    public List<string> TemplateParameters { get; set; } = [];
}

/// <summary>
/// Represents a model property or operation parameter in TypeSpec source.
/// </summary>
public class TspProperty
{
    /// <summary>Gets or sets the property or parameter name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the declared type expression.</summary>
    public string Type { get; set; } = "";
    /// <summary>Gets or sets whether the declaration has an optional marker.</summary>
    public bool Optional { get; set; }
    /// <summary>Gets or sets the default value expression, if present.</summary>
    public string? Default { get; set; }
    /// <summary>Gets or sets decorators attached to the declaration.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}

/// <summary>Represents a TypeSpec operation signature and its enclosing scope.</summary>
public class TspOperation
{
    /// <summary>Gets or sets the operation's declared name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets the enclosing interface name, if any.</summary>
    public string? Interface { get; set; }
    /// <summary>Gets or sets the operation's parameter declarations.</summary>
    public List<TspProperty> Parameters { get; set; } = [];
    /// <summary>Gets or sets the operation's return type expression.</summary>
    public string ReturnType { get; set; } = "void";
    /// <summary>Gets or sets decorators attached to the operation.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}

/// <summary>Represents a TypeSpec interface containing operation declarations.</summary>
public class TspInterface
{
    /// <summary>Gets or sets the interface's declared name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets operations declared by the interface.</summary>
    public List<TspOperation> Operations { get; set; } = [];
    /// <summary>Gets or sets decorators attached to the interface.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
    /// <summary>Gets or sets the extended interface type expressions.</summary>
    public List<string> Extends { get; set; } = [];
    /// <summary>Gets or sets the interface's template parameter names.</summary>
    public List<string> TemplateParameters { get; set; } = [];
}

/// <summary>Represents a TypeSpec enumeration and its members.</summary>
public class TspEnum
{
    /// <summary>Gets or sets the enumeration's declared name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets the enumeration's member declarations.</summary>
    public List<TspEnumMember> Members { get; set; } = [];
    /// <summary>Gets or sets decorators attached to the enumeration.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}

/// <summary>Represents a named TypeSpec enumeration value.</summary>
public class TspEnumMember
{
    /// <summary>Gets or sets the member's declared name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the explicit member value, if present.</summary>
    public string? Value { get; set; }
    /// <summary>Gets or sets decorators attached to the member.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}

/// <summary>Represents a TypeSpec union and its alternative types.</summary>
public class TspUnion
{
    /// <summary>Gets or sets the union's declared name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets the union's alternative declarations.</summary>
    public List<TspUnionVariant> Variants { get; set; } = [];
    /// <summary>Gets or sets decorators attached to the union.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}

/// <summary>Represents one named alternative of a TypeSpec union.</summary>
public class TspUnionVariant
{
    /// <summary>Gets or sets the alternative's declared name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the alternative's type expression.</summary>
    public string Type { get; set; } = "";
}

/// <summary>Represents a TypeSpec scalar and its optional base scalar.</summary>
public class TspScalar
{
    /// <summary>Gets or sets the scalar's declared name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the enclosing namespace, if any.</summary>
    public string? Namespace { get; set; }
    /// <summary>Gets or sets the extended scalar's type expression, if any.</summary>
    public string? BaseScalar { get; set; }
    /// <summary>Gets or sets decorators attached to the scalar.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}
