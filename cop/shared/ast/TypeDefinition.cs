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

public record PropertyDefinition(
    string Name,
    string TypeName,
    bool IsOptional,
    bool IsCollection,
    int Line,
    Cop.Lang.Ast.Expression? ComputedExpr = null);

/// <summary>
/// A flags enum definition: flags Visibility = Public | Protected | Private | Internal
/// Members are auto-assigned power-of-2 values starting from 1.
/// </summary>
public record FlagsDefinition(
    string Name,
    List<string> Members,
    int Line,
    bool IsExported = false,
    string? DocComment = null);

/// <summary>
/// An extensible enum definition: enum TypeKind = Class | Struct | Interface | Enum
/// Members resolve to their string name. Extensible: providers may return unlisted values.
/// </summary>
public record EnumDefinition(
    string Name,
    List<string> Members,
    int Line,
    bool IsExported = false,
    string? DocComment = null);

/// <summary>
/// A type import declaration: import Modifier (local) or export Modifier (exported).
/// Promotes all members of a flags or enum type to global scope as bare identifiers.
/// </summary>
public record TypeImportDeclaration(
    string TypeName,
    int Line,
    bool IsExported = false,
    string? DocComment = null);