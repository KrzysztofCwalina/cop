namespace Cop.Lang;

/// <summary>
/// A formal type declaration: type Foo = { ... } or type Foo = Bar &amp; { ... }
/// </summary>
public record TypeDefinition(
    string Name,
    string? BaseType,
    List<PropertyDefinition> Properties,
    int Line,
    bool IsExported = false);

public record PropertyDefinition(
    string Name,
    string TypeName,
    bool IsOptional,
    bool IsCollection,
    int Line);