namespace TypeSpecProvider;

/// <summary>
/// Represents a complete TypeSpec compilation unit (one or more files).
/// </summary>
public class TspSpec
{
    public List<TspNamespace> Namespaces { get; set; } = [];
    public List<TspModel> Models { get; set; } = [];
    public List<TspOperation> Operations { get; set; } = [];
    public List<TspInterface> Interfaces { get; set; } = [];
    public List<TspEnum> Enums { get; set; } = [];
    public List<TspUnion> Unions { get; set; } = [];
    public List<TspScalar> Scalars { get; set; } = [];
}

public class TspNamespace
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public List<TspDecorator> Decorators { get; set; } = [];
}

public class TspDecorator
{
    public string Name { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
}

public class TspModel
{
    public string Name { get; set; } = "";
    public string? Namespace { get; set; }
    public List<TspProperty> Properties { get; set; } = [];
    public string? BaseModel { get; set; }
    public List<TspDecorator> Decorators { get; set; } = [];
    public List<string> TemplateParameters { get; set; } = [];
}

public class TspProperty
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Optional { get; set; }
    public string? Default { get; set; }
    public List<TspDecorator> Decorators { get; set; } = [];
}

public class TspOperation
{
    public string Name { get; set; } = "";
    public string? Namespace { get; set; }
    public string? Interface { get; set; }
    public List<TspProperty> Parameters { get; set; } = [];
    public string ReturnType { get; set; } = "void";
    public List<TspDecorator> Decorators { get; set; } = [];
}

public class TspInterface
{
    public string Name { get; set; } = "";
    public string? Namespace { get; set; }
    public List<TspOperation> Operations { get; set; } = [];
    public List<TspDecorator> Decorators { get; set; } = [];
    public List<string> Extends { get; set; } = [];
    public List<string> TemplateParameters { get; set; } = [];
}

public class TspEnum
{
    public string Name { get; set; } = "";
    public string? Namespace { get; set; }
    public List<TspEnumMember> Members { get; set; } = [];
    public List<TspDecorator> Decorators { get; set; } = [];
}

public class TspEnumMember
{
    public string Name { get; set; } = "";
    public string? Value { get; set; }
    public List<TspDecorator> Decorators { get; set; } = [];
}

public class TspUnion
{
    public string Name { get; set; } = "";
    public string? Namespace { get; set; }
    public List<TspUnionVariant> Variants { get; set; } = [];
    public List<TspDecorator> Decorators { get; set; } = [];
}

public class TspUnionVariant
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}

public class TspScalar
{
    public string Name { get; set; } = "";
    public string? Namespace { get; set; }
    public string? BaseScalar { get; set; }
    public List<TspDecorator> Decorators { get; set; } = [];
}
