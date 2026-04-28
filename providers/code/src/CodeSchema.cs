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

    public static ProviderSchema Get() => _schema;

    public static ReadOnlyMemory<byte> GetJson() => _schema.ToJson();

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                TypeDef("Type", null,
                    Prop("Name"), Prop("Kind"),
                    Prop("Modifiers", "int"),
                    Coll("BaseTypes"), Coll("Constructors", "Method"), Coll("Methods", "Method"),
                    Coll("MethodNames"), Coll("NestedTypes", "Type"),
                    Coll("EnumValues"), Coll("Decorators"),
                    Prop("Line", "int"), Opt("File", "File"), Prop("Source"),
                    Bool("Documented"),
                    Coll("Fields", "Field"), Coll("Properties", "Property"), Coll("Events", "Event")),

                TypeDef("Method", null,
                    Prop("Name"),
                    Prop("Modifiers", "int"),
                    Opt("ReturnType", "TypeReference"),
                    Coll("Parameters", "Parameter"), Coll("Statements", "Statement"), Coll("Decorators"),
                    Prop("Line", "int"), Bool("Documented")),

                TypeDef("Constructor", "Method"),

                TypeDef("Parameter", null,
                    Prop("Name"), Opt("Type", "TypeReference"),
                    Bool("Variadic"), Bool("Kwargs"), Bool("Defaulted"),
                    Opt("DefaultValue")),

                TypeDef("Field", null,
                    Prop("Name"), Opt("Type", "TypeReference"),
                    Prop("Modifiers", "int"),
                    Prop("Line", "int")),

                TypeDef("Property", null,
                    Prop("Name"), Opt("Type", "TypeReference"),
                    Prop("Modifiers", "int"),
                    Bool("HasGetter"), Bool("HasSetter"), Bool("Documented"),
                    Prop("Line", "int")),

                TypeDef("Event", null,
                    Prop("Name"), Opt("Type", "TypeReference"),
                    Prop("Modifiers", "int"),
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
                    Opt("File", "File"), Prop("Source"),
                    Opt("Method", "Method"), Opt("Parent", "Statement"),
                    Coll("Children", "Statement"), Coll("Ancestors", "Statement"),
                    Opt("Condition"), Opt("Expression")),

                TypeDef("Line", null,
                    Prop("Text"), Prop("Number", "int"), Opt("File", "File"), Prop("Source")),

                TypeDef("File", null,
                    Prop("Path"), Opt("Language"), Opt("Namespace"),
                    Coll("Usings"), Coll("Types", "Type")),

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
            ],
            Collections =
            [
                new() { Name = "Types", ItemType = "Type" },
                new() { Name = "Statements", ItemType = "Statement" },
                new() { Name = "Lines", ItemType = "Line" },
                new() { Name = "Files", ItemType = "File" },
                new() { Name = "Members", ItemType = "Member" },
                new() { Name = "Api", ItemType = "Api" },
                new() { Name = "Regions", ItemType = "Region" },
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
