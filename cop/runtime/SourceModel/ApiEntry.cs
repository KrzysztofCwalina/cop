namespace Cop.Providers.SourceModel;

/// <summary>
/// A flat, canonical public API entry for API surface comparison.
/// Each entry represents one API element (type, method, property, event, enum value, constructor).
/// The Signature property is the stable comparison key.
/// The StubLine property is the C# stub representation for API listing output.
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
    /// The C# stub line for this API entry (e.g., "public virtual string Name { get { throw null; } }").
    /// Does NOT include indentation — the generator adds that based on nesting level.
    /// </summary>
    public string StubLine { get; init; } = "";

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
    {
        File = type.File,
        StubLine = FormatTypeStub(type)
    };

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
    {
        File = enumType.File,
        StubLine = $"{value},"
    };

    /// <summary>
    /// Build a canonical signature for a constructor.
    /// Format: "ctor TypeName(param1, param2)"
    /// </summary>
    public static ApiEntry ForConstructor(TypeDeclaration type, MethodDeclaration ctor) => new(
        Kind: "ctor",
        TypeName: type.Name,
        MemberName: ".ctor",
        Signature: $"ctor {type.Name}({FormatParameterTypes(ctor.Parameters)})",
        Line: ctor.Line)
    {
        File = type.File,
        StubLine = $"{FormatModifiers(ctor.Modifiers)}{type.Name}({FormatParametersFull(ctor.Parameters)}) {{ }}"
    };

    /// <summary>
    /// Build a canonical signature for a method.
    /// Format: "method TypeName.MethodName(param1, param2) : ReturnType"
    /// </summary>
    public static ApiEntry ForMethod(TypeDeclaration type, MethodDeclaration method) => new(
        Kind: "method",
        TypeName: type.Name,
        MemberName: method.Name,
        Signature: $"method {type.Name}.{method.Name}({FormatParameterTypes(method.Parameters)}) : {method.ReturnType?.OriginalText ?? "void"}",
        Line: method.Line)
    {
        File = type.File,
        StubLine = FormatMethodStub(method)
    };

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
    {
        File = type.File,
        StubLine = FormatPropertyStub(prop)
    };

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
    {
        File = type.File,
        StubLine = $"{FormatModifiers(evt.Modifiers)}event {evt.Type?.OriginalText ?? "EventHandler"} {evt.Name} {{ add {{ }} remove {{ }} }}"
    };

    /// <summary>
    /// Build a canonical signature for a field.
    /// Format: "field TypeName.FieldName : FieldType"
    /// </summary>
    public static ApiEntry ForField(TypeDeclaration type, FieldDeclaration field) => new(
        Kind: "field",
        TypeName: type.Name,
        MemberName: field.Name,
        Signature: $"field {type.Name}.{field.Name} : {field.Type?.OriginalText ?? "object"}",
        Line: field.Line)
    {
        File = type.File,
        StubLine = $"{FormatModifiers(field.Modifiers)}{field.Type?.OriginalText ?? "object"} {field.Name};"
    };

    // --- Signature helpers (types only, for comparison key) ---

    private static string FormatParameterTypes(List<ParameterDeclaration> parameters) =>
        string.Join(", ", parameters.Select(p => p.Type?.OriginalText ?? "object"));

    private static string FormatAccessors(PropertyDeclaration prop) =>
        (prop.HasGetter, prop.HasSetter) switch
        {
            (true, true) => "get; set;",
            (true, false) => "get;",
            (false, true) => "set;",
            _ => ""
        };

    // --- StubLine helpers (full C# stub representation) ---

    private static string FormatModifiers(Modifier mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(Modifier.Public)) parts.Add("public");
        else if (mods.HasFlag(Modifier.Protected) && mods.HasFlag(Modifier.Internal)) parts.Add("protected internal");
        else if (mods.HasFlag(Modifier.Protected)) parts.Add("protected");
        else if (mods.HasFlag(Modifier.Internal)) parts.Add("internal");
        if (mods.HasFlag(Modifier.Static)) parts.Add("static");
        if (mods.HasFlag(Modifier.Const)) parts.Add("const");
        if (mods.HasFlag(Modifier.Readonly)) parts.Add("readonly");
        if (mods.HasFlag(Modifier.Abstract)) parts.Add("abstract");
        if (mods.HasFlag(Modifier.Virtual)) parts.Add("virtual");
        if (mods.HasFlag(Modifier.Override)) parts.Add("override");
        if (mods.HasFlag(Modifier.Sealed) && mods.HasFlag(Modifier.Override)) { } // sealed override handled above
        else if (mods.HasFlag(Modifier.Sealed)) parts.Add("sealed");
        if (mods.HasFlag(Modifier.Async)) parts.Add("async");
        return parts.Count > 0 ? string.Join(" ", parts) + " " : "";
    }

    private static string FormatParametersFull(List<ParameterDeclaration> parameters) =>
        string.Join(", ", parameters.Select(p =>
        {
            var type = p.Type?.OriginalText ?? "object";
            var result = $"{type} {p.Name}";
            if (p.HasDefaultValue)
                result += $" = {p.DefaultValueText ?? $"default({type})"}";
            return result;
        }));

    private static string FormatTypeStub(TypeDeclaration type)
    {
        var mods = FormatModifiers(type.Modifiers);
        var kind = type.Kind.ToString().ToLowerInvariant();
        var partial = type.Kind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface ? "partial " : "";
        var name = type.Name;
        var baseList = type.BaseTypes.Count > 0 ? $" : {string.Join(", ", type.BaseTypes)}" : "";
        return $"{mods}{partial}{kind} {name}{baseList}";
    }

    private static string FormatMethodStub(MethodDeclaration method)
    {
        var mods = FormatModifiers(method.Modifiers);
        var returnType = method.ReturnType?.OriginalText ?? "void";
        var name = method.Name;
        var parameters = FormatParametersFull(method.Parameters);
        var isVoid = returnType is "void" or "System.Void";
        var body = isVoid ? "{ }" : "{ throw null; }";
        return $"{mods}{returnType} {name}({parameters}) {body}";
    }

    private static string FormatPropertyStub(PropertyDeclaration prop)
    {
        var mods = FormatModifiers(prop.Modifiers);
        var type = prop.Type?.OriginalText ?? "object";
        var accessors = (prop.HasGetter, prop.HasSetter) switch
        {
            (true, true) => "{ get { throw null; } set { } }",
            (true, false) => "{ get { throw null; } }",
            (false, true) => "{ set { } }",
            _ => "{ }"
        };
        return $"{mods}{type} {prop.Name} {accessors}";
    }
}
