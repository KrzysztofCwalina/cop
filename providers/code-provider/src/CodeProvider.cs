using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Built-in provider for source code analysis data (Types, Methods, Statements, etc.).
/// Uses the Objects path for native CLR lambda accessors.
/// Unlike FilesystemProvider, this provider does not return global collections — it provides
/// per-document collection extractors that derive data from parsed source files.
/// </summary>
public class CodeProvider : DataProvider
{
    public override DataFormat SupportedFormats => DataFormat.InMemoryDatabase;

    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();

    private static readonly ProviderSchema _schema = BuildSchema();

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
            ],
            Collections =
            [
                new() { Name = "Types", ItemType = "Type" },
                new() { Name = "Statements", ItemType = "Statement" },
                new() { Name = "Lines", ItemType = "Line" },
                new() { Name = "Files", ItemType = "File" },
                new() { Name = "Members", ItemType = "Member" },
                new() { Name = "Api", ItemType = "Api" },
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

    public override RuntimeBindings GetRuntimeBindings()
    {
        return new RuntimeBindings
        {
            ClrTypeMappings = new()
            {
                [typeof(TypeDeclaration)] = "Type",
                [typeof(MethodDeclaration)] = "Method",
                [typeof(ParameterDeclaration)] = "Parameter",
                [typeof(TypeReference)] = "TypeReference",
                [typeof(StatementInfo)] = "Statement",
                [typeof(LineInfo)] = "Line",
                [typeof(SourceFile)] = "File",
                [typeof(MemberInfo)] = "Member",
                [typeof(ApiEntry)] = "Api",
                [typeof(FieldDeclaration)] = "Field",
                [typeof(PropertyDeclaration)] = "Property",
                [typeof(EventDeclaration)] = "Event",
            },
            Accessors = BuildAccessors(),
            CollectionExtractors = BuildExtractors(),
            MethodEvaluators = new()
            {
                [("Type", "inheritsFrom")] = (target, args) =>
                    ((TypeDeclaration)target).InheritsFrom(args[0]?.ToString() ?? ""),
            },
            TextConverters = new()
            {
                ["TypeReference"] = o => ((TypeReference)o).OriginalText,
                ["Line"] = o => ((LineInfo)o).Text,
                ["Api"] = o => ((ApiEntry)o).Signature,
            },
        };
    }

    private static Dictionary<string, Dictionary<string, Func<object, object?>>> BuildAccessors()
    {
        return new()
        {
            ["Type"] = new()
            {
                ["Name"] = o => ((TypeDeclaration)o).Name,
                ["Kind"] = o => ((TypeDeclaration)o).Kind.ToString(),
                ["Modifiers"] = o => (object)(int)((TypeDeclaration)o).Modifiers,
                ["Constructors"] = o => (object)((TypeDeclaration)o).Constructors,
                ["Methods"] = o => (object)((TypeDeclaration)o).Methods,
                ["NestedTypes"] = o => (object)((TypeDeclaration)o).NestedTypes,
                ["EnumValues"] = o => (object)((TypeDeclaration)o).EnumValues,
                ["BaseTypes"] = o => (object)((TypeDeclaration)o).BaseTypes,
                ["Decorators"] = o => (object)((TypeDeclaration)o).Decorators,
                ["Line"] = o => (object)((TypeDeclaration)o).Line,
                ["MethodNames"] = o => (object)((TypeDeclaration)o).Methods.Select(m => m.Name).ToList(),
                ["File"] = o => ((TypeDeclaration)o).File,
                ["Source"] = o => ((TypeDeclaration)o).Source,
                ["Documented"] = o => (object)((TypeDeclaration)o).HasDocComment,
                ["Fields"] = o => (object)((TypeDeclaration)o).Fields,
                ["Properties"] = o => (object)((TypeDeclaration)o).Properties,
                ["Events"] = o => (object)((TypeDeclaration)o).Events,
            },
            ["Method"] = new()
            {
                ["Name"] = o => ((MethodDeclaration)o).Name,
                ["Modifiers"] = o => (object)(int)((MethodDeclaration)o).Modifiers,
                ["Parameters"] = o => (object)((MethodDeclaration)o).Parameters,
                ["Statements"] = o => (object)((MethodDeclaration)o).Statements,
                ["Decorators"] = o => (object)((MethodDeclaration)o).Decorators,
                ["ReturnType"] = o => (object?)((MethodDeclaration)o).ReturnType,
                ["Line"] = o => (object)((MethodDeclaration)o).Line,
                ["Documented"] = o => (object)((MethodDeclaration)o).HasDocComment,
            },
            ["Parameter"] = new()
            {
                ["Name"] = o => ((ParameterDeclaration)o).Name,
                ["Type"] = o => (object?)((ParameterDeclaration)o).Type,
                ["Variadic"] = o => (object)((ParameterDeclaration)o).IsVariadic,
                ["Kwargs"] = o => (object)((ParameterDeclaration)o).IsKwargs,
                ["Defaulted"] = o => (object)((ParameterDeclaration)o).HasDefaultValue,
                ["DefaultValue"] = o => ((ParameterDeclaration)o).DefaultValueText,
            },
            ["Field"] = new()
            {
                ["Name"] = o => ((FieldDeclaration)o).Name,
                ["Type"] = o => (object?)((FieldDeclaration)o).Type,
                ["Modifiers"] = o => (object)(int)((FieldDeclaration)o).Modifiers,
                ["Line"] = o => (object)((FieldDeclaration)o).Line,
            },
            ["Property"] = new()
            {
                ["Name"] = o => ((PropertyDeclaration)o).Name,
                ["Type"] = o => (object?)((PropertyDeclaration)o).Type,
                ["Modifiers"] = o => (object)(int)((PropertyDeclaration)o).Modifiers,
                ["HasGetter"] = o => (object)((PropertyDeclaration)o).HasGetter,
                ["HasSetter"] = o => (object)((PropertyDeclaration)o).HasSetter,
                ["Documented"] = o => (object)((PropertyDeclaration)o).HasDocComment,
                ["Line"] = o => (object)((PropertyDeclaration)o).Line,
            },
            ["Event"] = new()
            {
                ["Name"] = o => ((EventDeclaration)o).Name,
                ["Type"] = o => (object?)((EventDeclaration)o).Type,
                ["Modifiers"] = o => (object)(int)((EventDeclaration)o).Modifiers,
                ["Line"] = o => (object)((EventDeclaration)o).Line,
            },
            ["TypeReference"] = new()
            {
                ["Name"] = o => ((TypeReference)o).Name,
                ["Namespace"] = o => ((TypeReference)o).Namespace,
                ["Generic"] = o => (object)((TypeReference)o).IsGeneric,
                ["GenericArguments"] = o => (object)((TypeReference)o).GenericArguments,
                ["Length"] = o => (object)((TypeReference)o).OriginalText.Length,
            },
            ["Statement"] = new()
            {
                ["Kind"] = o => ((StatementInfo)o).Kind,
                ["Keywords"] = o => (object)((StatementInfo)o).Keywords,
                ["TypeName"] = o => ((StatementInfo)o).TypeName,
                ["MemberName"] = o => ((StatementInfo)o).MemberName,
                ["Arguments"] = o => (object)((StatementInfo)o).Arguments,
                ["Line"] = o => (object)((StatementInfo)o).Line,
                ["InMethod"] = o => (object)((StatementInfo)o).IsInMethod,
                ["Rethrows"] = o => (object)((StatementInfo)o).HasRethrow,
                ["Generic"] = o => (object)((StatementInfo)o).IsGenericErrorHandler,
                ["ErrorHandler"] = o => (object)((StatementInfo)o).IsErrorHandler,
                ["File"] = o => ((StatementInfo)o).File,
                ["Source"] = o => ((StatementInfo)o).Source,
                ["Method"] = o => ((StatementInfo)o).Method,
                ["Parent"] = o => ((StatementInfo)o).Parent,
                ["Children"] = o => (object)((StatementInfo)o)._children,
                ["Ancestors"] = o => (object)((StatementInfo)o).GetAncestors(),
                ["Condition"] = o => ((StatementInfo)o).Condition,
                ["Expression"] = o => ((StatementInfo)o).Expression,
            },
            ["Line"] = new()
            {
                ["Text"] = o => ((LineInfo)o).Text,
                ["Number"] = o => (object)((LineInfo)o).Number,
                ["File"] = o => ((LineInfo)o).File,
                ["Source"] = o => ((LineInfo)o).Source,
            },
            ["File"] = new()
            {
                ["Path"] = o => ((SourceFile)o).Path,
                ["Language"] = o => ((SourceFile)o).Language,
                ["Namespace"] = o => ((SourceFile)o).Namespace,
                ["Usings"] = o => (object)((SourceFile)o).Usings,
                ["Types"] = o => (object)((SourceFile)o).Types,
            },
            ["Member"] = new()
            {
                ["Name"] = o => ((MemberInfo)o).Name,
                ["DeclaringType"] = o => ((MemberInfo)o).DeclaringType,
                ["Line"] = o => (object)((MemberInfo)o).Line,
            },
            ["Api"] = new()
            {
                ["Kind"] = o => ((ApiEntry)o).Kind,
                ["TypeName"] = o => ((ApiEntry)o).TypeName,
                ["MemberName"] = o => ((ApiEntry)o).MemberName,
                ["Signature"] = o => ((ApiEntry)o).Signature,
                ["ApiAsText"] = o => ((ApiEntry)o).ApiAsText,
                ["Line"] = o => (object)((ApiEntry)o).Line,
                ["File"] = o => ((ApiEntry)o).File,
                ["Source"] = o => ((ApiEntry)o).Source,
            },
        };
    }

    private static Dictionary<string, Func<object, List<object>>> BuildExtractors()
    {
        return new()
        {
            ["Types"] = doc => ((SourceFile)doc).Types.Cast<object>().ToList(),
            ["Statements"] = doc => ((SourceFile)doc).Statements.Cast<object>().ToList(),
            ["Lines"] = doc =>
            {
                var file = (SourceFile)doc;
                return file.Lines.Select((text, i) => (object)new LineInfo(text, i + 1) { File = file }).ToList();
            },
            ["Files"] = doc => [(object)(SourceFile)doc],
            ["Members"] = doc => ((SourceFile)doc).Types.SelectMany(t =>
            {
                var members = new List<object>();
                members.AddRange(t.Methods.Select(m => new MemberInfo(m.Name, t.Name, m.Line)));
                members.AddRange(t.Properties.Select(p => new MemberInfo(p.Name, t.Name, p.Line)));
                members.AddRange(t.Events.Select(e => new MemberInfo(e.Name, t.Name, e.Line)));
                members.AddRange(t.Fields.Select(f => new MemberInfo(f.Name, t.Name, f.Line)));
                return members;
            }).ToList(),
            ["Api"] = doc =>
            {
                var file = (SourceFile)doc;
                var entries = new List<object>();
                foreach (var type in file.Types)
                {
                    if (!type.IsPublic) continue;
                    entries.Add(ApiEntry.ForType(type));
                    if (type.Kind == TypeKind.Enum)
                    {
                        foreach (var value in type.EnumValues)
                            entries.Add(ApiEntry.ForEnumValue(type, value));
                        continue;
                    }
                    foreach (var ctor in type.Constructors)
                        if (ctor.IsPublic || ctor.IsProtected)
                            entries.Add(ApiEntry.ForConstructor(type, ctor));
                    foreach (var method in type.Methods)
                        if (method.IsPublic || method.IsProtected)
                            entries.Add(ApiEntry.ForMethod(type, method));
                    foreach (var prop in type.Properties)
                        if (prop.IsPublic || prop.IsProtected)
                            entries.Add(ApiEntry.ForProperty(type, prop));
                    foreach (var evt in type.Events)
                        if (evt.IsPublic || evt.IsProtected)
                            entries.Add(ApiEntry.ForEvent(type, evt));
                    foreach (var field in type.Fields)
                        if (field.IsPublic || field.IsProtected)
                            entries.Add(ApiEntry.ForField(type, field));
                }
                return entries;
            },
        };
    }

}
