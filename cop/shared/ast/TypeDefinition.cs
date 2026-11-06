// Describes declared types and their fields for source-model consumers.

namespace Cop.Lang;

/// <summary>
/// A formal type declaration: type Foo = { ... } or type Foo = Bar &amp; { ... }
/// </summary>
public record TypeDefinition(
    string Name,
    string? BaseType,
    List<PropertyDefinition> Properties,
    int Line,
    bool IsExported = false,
    string? DocComment = null,
    List<string>? Traits = null);
