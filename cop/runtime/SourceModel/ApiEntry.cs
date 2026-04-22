namespace Cop.Providers.SourceModel;

/// <summary>
/// A flat, canonical public API entry for API surface comparison.
/// Each entry represents one API element (type, method, property, event, enum value, constructor).
/// The Signature property is the stable comparison key.
/// </summary>
public record ApiEntry(
    string Kind,
    string TypeName,
    string MemberName,
    string Signature,
    int Line)
{
    public SourceFile? File { get; init; }

    public string Source => Signature;

    /// <summary>
    /// Build a canonical signature for a type-level entry.
    /// Format: "class TypeName" / "interface TypeName" / "struct TypeName" / "enum TypeName"
    /// </summary>
    public static ApiEntry ForType(TypeDeclaration type) => new(
        Kind: type.Kind.ToString().ToLowerInvariant(),
        TypeName: type.Name,
        MemberName: "",
        Signature: $"{type.Kind.ToString().ToLowerInvariant()} {type.Name}",
        Line: type.Line)
    { File = type.File };

    /// <summary>
    /// Build a canonical signature for an enum value.
    /// Format: "enumvalue TypeName.ValueName"
    /// </summary>
    public static ApiEntry ForEnumValue(TypeDeclaration enumType, string value) => new(
        Kind: "enumvalue",
        TypeName: enumType.Name,
        MemberName: value,
        Signature: $"enumvalue {enumType.Name}.{value}",
        Line: enumType.Line)
    { File = enumType.File };

    /// <summary>
    /// Build a canonical signature for a constructor.
    /// Format: "ctor TypeName(param1, param2)"
    /// </summary>
    public static ApiEntry ForConstructor(TypeDeclaration type, MethodDeclaration ctor) => new(
        Kind: "ctor",
        TypeName: type.Name,
        MemberName: ".ctor",
        Signature: $"ctor {type.Name}({FormatParameters(ctor.Parameters)})",
        Line: ctor.Line)
    { File = type.File };

    /// <summary>
    /// Build a canonical signature for a method.
    /// Format: "method TypeName.MethodName(param1, param2) : ReturnType"
    /// </summary>
    public static ApiEntry ForMethod(TypeDeclaration type, MethodDeclaration method) => new(
        Kind: "method",
        TypeName: type.Name,
        MemberName: method.Name,
        Signature: $"method {type.Name}.{method.Name}({FormatParameters(method.Parameters)}) : {method.ReturnType?.OriginalText ?? "void"}",
        Line: method.Line)
    { File = type.File };

    /// <summary>
    /// Build a canonical signature for a property.
    /// Format: "property TypeName.PropertyName { get; set; } : PropertyType"
    /// </summary>
    public static ApiEntry ForProperty(TypeDeclaration type, PropertyDeclaration prop) => new(
        Kind: "property",
        TypeName: type.Name,
        MemberName: prop.Name,
        Signature: $"property {type.Name}.{prop.Name} {{ {FormatAccessors(prop)} }} : {prop.Type?.OriginalText ?? "object"}",
        Line: prop.Line)
    { File = type.File };

    /// <summary>
    /// Build a canonical signature for an event.
    /// Format: "event TypeName.EventName : EventType"
    /// </summary>
    public static ApiEntry ForEvent(TypeDeclaration type, EventDeclaration evt) => new(
        Kind: "event",
        TypeName: type.Name,
        MemberName: evt.Name,
        Signature: $"event {type.Name}.{evt.Name} : {evt.Type?.OriginalText ?? "EventHandler"}",
        Line: evt.Line)
    { File = type.File };

    private static string FormatParameters(List<ParameterDeclaration> parameters) =>
        string.Join(", ", parameters.Select(p => p.Type?.OriginalText ?? "object"));

    private static string FormatAccessors(PropertyDeclaration prop) =>
        (prop.HasGetter, prop.HasSetter) switch
        {
            (true, true) => "get; set;",
            (true, false) => "get;",
            (false, true) => "set;",
            _ => ""
        };
}
