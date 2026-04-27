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

/// <summary>
/// A flags enum definition: flags Visibility = Public | Protected | Private | Internal
/// Members are auto-assigned power-of-2 values starting from 1.
/// </summary>
public record FlagsDefinition(
    string Name,
    List<string> Members,
    int Line,
    bool IsExported = false);