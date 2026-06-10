using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Shared schema definition for code analysis providers.
/// All language providers return this same schema so their types are unified.
/// </summary>
public static class CodeSchema
{
    private static readonly ProviderSchema _schema = BuildSchema();
    private static readonly ProviderSchema _typesOnly = BuildTypesOnlySchema();

    public static ProviderSchema Get() => _schema;

    public static ReadOnlyMemory<byte> GetJson() => _schema.ToJson();

    /// <summary>
    /// Returns schema with only type definitions (no collections).
    /// Used by CodeSchemaProvider which registers types but has no data.
    /// </summary>
    public static ReadOnlyMemory<byte> GetTypesOnlyJson() => _typesOnly.ToJson();

    private static ProviderSchema BuildTypesOnlySchema() => new()
    {
        Types = _schema.Types,
        Collections = []
    };

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                TypeDef("Type", null,
                    Prop("Name"), Prop("Kind"),
                    Prop("Modifiers", "int"),
                    Bool("IsSealed"), Bool("IsAbstract"), Bool("IsStatic"), Bool("IsPublic"),
                    Coll("BaseTypes"), Coll("Interfaces"), Coll("Constructors", "Method"), Coll("Methods", "Method"),
                    Coll("MethodNames"), Coll("NestedTypes", "Type"),
                    Coll("EnumValues"), Coll("Decorators"),
                    Prop("Line", "int"), Opt("File", "File"), Prop("Source"),
                    Bool("Documented"), Opt("Documentation"),
                    Coll("Fields", "Field"), Coll("Properties", "Property"), Coll("Events", "Event")),

                TypeDef("Method", null,
                    Prop("Name"),
                    Prop("Modifiers", "int"),
                    Bool("IsStatic"), Bool("IsAbstract"), Bool("IsPublic"),
                    Opt("ReturnType", "TypeReference"),
                    Coll("Parameters", "Parameter"), Coll("Statements", "Statement"), Coll("Decorators"),
                    Prop("Line", "int"), Bool("Documented"), Opt("Documentation")),

                TypeDef("Constructor", "Method"),

                TypeDef("Parameter", null,
                    Prop("Name"), Opt("Type", "TypeReference"),
                    Bool("Variadic"), Bool("Kwargs"), Bool("Defaulted"),
                    Opt("DefaultValue")),

                TypeDef("Field", null,
                    Prop("Name"), Opt("Type", "TypeReference"),
                    Prop("Modifiers", "int"),
                    Bool("IsStatic"), Bool("IsPublic"),
                    Prop("Line", "int")),

                TypeDef("Property", null,
                    Prop("Name"), Opt("Type", "TypeReference"),
                    Prop("Modifiers", "int"),
                    Bool("IsStatic"), Bool("IsAbstract"), Bool("IsPublic"),
                    Bool("HasGetter"), Bool("HasSetter"), Bool("Documented"), Opt("Documentation"),
                    Prop("Line", "int")),

                TypeDef("Event", null,
                    Prop("Name"), Opt("Type", "TypeReference"),
                    Prop("Modifiers", "int"),
                    Bool("IsStatic"), Bool("IsPublic"),
                    Prop("Line", "int")),

                TypeDef("TypeReference", null,
                    Prop("Name"), Opt("Namespace"),
                    Bool("Generic"), Coll("GenericArguments", "TypeReference"),
                    Prop("Length", "int")),

                TypeDef("Statement", null,
                    Prop("Kind"), Coll("Keywords"),
                    Opt("TypeName"), Opt("MemberName"),
                    Coll("Arguments"), Prop("Line", "int"),
                    Bool("InMethod"), Bool("Rethrows"), Bool("Generic"), Bool("ErrorHandler"),
                    Bool("Braced"),
                    Opt("File", "File"), Prop("Source"),
                    Opt("Method", "Method"), Opt("Parent", "Statement"),
                    Coll("Children", "Statement"), Coll("Ancestors", "Statement"),
                    Opt("Condition"), Opt("Expression"),
                    Coll("ConstructedTypeInterfaces")),

                TypeDef("Line", null,
                    Prop("Text"), Prop("Number", "int"), Prop("Kind"), Opt("File", "File"), Prop("Source"),
                    Prop("PreviousText"), Prop("NextText")),

                TypeDef("File", null,
                    Prop("Path"), Opt("Language"), Opt("Namespace"),
                    Coll("Usings"), Coll("Types", "Type"), Coll("Projects")),

                TypeDef("Api", null,
                    Prop("Kind"), Prop("TypeName"), Prop("MemberName"),
                    Prop("Signature"), Prop("ApiAsText"),
                    Prop("Line", "int"), Opt("File", "File"), Prop("Source")),

                TypeDef("Member", null,
                    Prop("Name"), Prop("DeclaringType"), Prop("Line", "int")),

                TypeDef("Region", null,
                    Prop("Name"), Prop("StartLine", "int"), Prop("EndLine", "int"),
                    Prop("Content"), Prop("ContentHash"),
                    Opt("File", "File"), Prop("Source")),

                TypeDef("Project", null,
                    Prop("Name"), Prop("Path"), Opt("Language"),
                    Coll("References"), Coll("Packages"), Coll("Frameworks"),
                    Coll("Properties", "ProjectProperty")),

                TypeDef("ProjectProperty", null,
                    Prop("Name"), Prop("Value")),
            ],
            Collections =
            [
                new() { Name = "Types", ItemType = "Type" },
                new() { Name = "Statements", ItemType = "Statement" },
                new() { Name = "Calls", ItemType = "Statement" },
                new() { Name = "Lines", ItemType = "Line" },
                new() { Name = "Files", ItemType = "File" },
                new() { Name = "Members", ItemType = "Member" },
                new() { Name = "Api", ItemType = "Api" },
                new() { Name = "Regions", ItemType = "Region" },
                new() { Name = "Projects", ItemType = "Project" },
            ]
        };
    }

    private static ProviderTypeSchema TypeDef(string name, string? baseType, params ProviderPropertySchema[] props)
        => new() { Name = name, Base = baseType, Properties = [.. props] };
    private static ProviderPropertySchema Prop(string name, string type = "string")
        => new() { Name = name, Type = type };
    private static ProviderPropertySchema Opt(string name, string type = "string")
        => new() { Name = name, Type = type, Optional = true };
    private static ProviderPropertySchema Bool(string name)
        => new() { Name = name, Type = "bool" };
    private static ProviderPropertySchema Coll(string name, string type = "string")
        => new() { Name = name, Type = type, Collection = true };
}
