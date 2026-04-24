using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Built-in provider for source code analysis data (Types, Methods, Statements, etc.).
/// Uses the Objects path for native CLR lambda accessors.
/// Unlike FilesystemProvider, this provider does not return global collections — it provides
/// per-document collection extractors that derive data from parsed source files.
/// </summary>
public class CodeProvider : CopProvider
{
    public override ProviderFormat SupportedFormats => ProviderFormat.Objects;

    public override byte[] GetSchema()
    {
        var schema = new
        {
            types = new object[]
            {
                TypeSchema("Type", null,
                    Prop("Name"), Prop("Kind"),
                    BoolProp("Public"), BoolProp("Sealed"), BoolProp("Abstract"), BoolProp("Static"),
                    CollProp("BaseTypes"), CollProp("Constructors", "Method"), CollProp("Methods", "Method"),
                    CollProp("MethodNames"), CollProp("NestedTypes", "Type"),
                    CollProp("EnumValues"), CollProp("Decorators"),
                    Prop("Line", "int"), OptProp("File", "File"), Prop("Source"),
                    BoolProp("Documented"),
                    CollProp("Fields", "Field"), CollProp("Properties", "Property"), CollProp("Events", "Event")),

                TypeSchema("Method", null,
                    Prop("Name"),
                    BoolProp("Public"), BoolProp("Protected"), BoolProp("Private"), BoolProp("Internal"),
                    BoolProp("Async"), BoolProp("Static"), BoolProp("Abstract"), BoolProp("Virtual"), BoolProp("Override"),
                    OptProp("ReturnType", "TypeReference"),
                    CollProp("Parameters", "Parameter"), CollProp("Statements", "Statement"), CollProp("Decorators"),
                    Prop("Line", "int"), BoolProp("Documented")),

                TypeSchema("Constructor", "Method"),

                TypeSchema("Parameter", null,
                    Prop("Name"), OptProp("Type", "TypeReference"),
                    BoolProp("Variadic"), BoolProp("Kwargs"), BoolProp("Defaulted"),
                    OptProp("DefaultValue")),

                TypeSchema("Field", null,
                    Prop("Name"), OptProp("Type", "TypeReference"),
                    BoolProp("Public"), BoolProp("Private"), BoolProp("Protected"), BoolProp("Internal"),
                    BoolProp("Static"), BoolProp("Readonly"), BoolProp("Const"),
                    Prop("Line", "int")),

                TypeSchema("Property", null,
                    Prop("Name"), OptProp("Type", "TypeReference"),
                    BoolProp("Public"), BoolProp("Protected"), BoolProp("Private"), BoolProp("Internal"),
                    BoolProp("Static"), BoolProp("Abstract"), BoolProp("Virtual"), BoolProp("Override"),
                    BoolProp("HasGetter"), BoolProp("HasSetter"), BoolProp("Documented"),
                    Prop("Line", "int")),

                TypeSchema("Event", null,
                    Prop("Name"), OptProp("Type", "TypeReference"),
                    BoolProp("Public"), BoolProp("Protected"), BoolProp("Private"), BoolProp("Internal"),
                    BoolProp("Static"), Prop("Line", "int")),

                TypeSchema("TypeReference", null,
                    Prop("Name"), OptProp("Namespace"),
                    BoolProp("Generic"), CollProp("GenericArguments", "TypeReference"),
                    Prop("Length", "int")),

                TypeSchema("Statement", null,
                    Prop("Kind"), CollProp("Keywords"),
                    OptProp("TypeName"), OptProp("MemberName"),
                    CollProp("Arguments"), Prop("Line", "int"),
                    BoolProp("InMethod"), BoolProp("Rethrows"), BoolProp("Generic"), BoolProp("ErrorHandler"),
                    OptProp("File", "File"), Prop("Source"),
                    OptProp("Method", "Method"), OptProp("Parent", "Statement"),
                    CollProp("Children", "Statement"), CollProp("Ancestors", "Statement"),
                    OptProp("Condition"), OptProp("Expression")),

                TypeSchema("Line", null,
                    Prop("Text"), Prop("Number", "int"), OptProp("File", "File"), Prop("Source")),

                TypeSchema("File", null,
                    Prop("Path"), OptProp("Language"), OptProp("Namespace"),
                    CollProp("Usings"), CollProp("Types", "Type")),

                TypeSchema("Api", null,
                    Prop("Kind"), Prop("TypeName"), Prop("MemberName"),
                    Prop("Signature"), Prop("ApiAsText"),
                    Prop("Line", "int"), OptProp("File", "File"), Prop("Source")),

                TypeSchema("Member", null,
                    Prop("Name"), Prop("DeclaringType"), Prop("Line", "int")),
            },
            collections = new object[]
            {
                new { name = "Types", itemType = "Type" },
                new { name = "Statements", itemType = "Statement" },
                new { name = "Lines", itemType = "Line" },
                new { name = "Files", itemType = "File" },
                new { name = "Members", itemType = "Member" },
                new { name = "Api", itemType = "Api" },
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(schema);
    }

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
                ["Public"] = o => (object)((TypeDeclaration)o).IsPublic,
                ["Sealed"] = o => (object)((TypeDeclaration)o).IsSealed,
                ["Abstract"] = o => (object)((TypeDeclaration)o).IsAbstract,
                ["Static"] = o => (object)((TypeDeclaration)o).IsStatic,
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
                ["Public"] = o => (object)((MethodDeclaration)o).IsPublic,
                ["Protected"] = o => (object)((MethodDeclaration)o).IsProtected,
                ["Private"] = o => (object)((MethodDeclaration)o).IsPrivate,
                ["Internal"] = o => (object)((MethodDeclaration)o).IsInternal,
                ["Async"] = o => (object)((MethodDeclaration)o).IsAsync,
                ["Static"] = o => (object)((MethodDeclaration)o).IsStatic,
                ["Abstract"] = o => (object)((MethodDeclaration)o).IsAbstract,
                ["Virtual"] = o => (object)((MethodDeclaration)o).IsVirtual,
                ["Override"] = o => (object)((MethodDeclaration)o).IsOverride,
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
                ["Public"] = o => (object)((FieldDeclaration)o).IsPublic,
                ["Private"] = o => (object)((FieldDeclaration)o).IsPrivate,
                ["Protected"] = o => (object)((FieldDeclaration)o).IsProtected,
                ["Internal"] = o => (object)((FieldDeclaration)o).IsInternal,
                ["Static"] = o => (object)((FieldDeclaration)o).IsStatic,
                ["Readonly"] = o => (object)((FieldDeclaration)o).IsReadonly,
                ["Const"] = o => (object)((FieldDeclaration)o).IsConst,
                ["Line"] = o => (object)((FieldDeclaration)o).Line,
            },
            ["Property"] = new()
            {
                ["Name"] = o => ((PropertyDeclaration)o).Name,
                ["Type"] = o => (object?)((PropertyDeclaration)o).Type,
                ["Public"] = o => (object)((PropertyDeclaration)o).IsPublic,
                ["Protected"] = o => (object)((PropertyDeclaration)o).IsProtected,
                ["Private"] = o => (object)((PropertyDeclaration)o).IsPrivate,
                ["Internal"] = o => (object)((PropertyDeclaration)o).IsInternal,
                ["Static"] = o => (object)((PropertyDeclaration)o).IsStatic,
                ["Abstract"] = o => (object)((PropertyDeclaration)o).IsAbstract,
                ["Virtual"] = o => (object)((PropertyDeclaration)o).IsVirtual,
                ["Override"] = o => (object)((PropertyDeclaration)o).IsOverride,
                ["HasGetter"] = o => (object)((PropertyDeclaration)o).HasGetter,
                ["HasSetter"] = o => (object)((PropertyDeclaration)o).HasSetter,
                ["Documented"] = o => (object)((PropertyDeclaration)o).HasDocComment,
                ["Line"] = o => (object)((PropertyDeclaration)o).Line,
            },
            ["Event"] = new()
            {
                ["Name"] = o => ((EventDeclaration)o).Name,
                ["Type"] = o => (object?)((EventDeclaration)o).Type,
                ["Public"] = o => (object)((EventDeclaration)o).IsPublic,
                ["Protected"] = o => (object)((EventDeclaration)o).IsProtected,
                ["Private"] = o => (object)((EventDeclaration)o).IsPrivate,
                ["Internal"] = o => (object)((EventDeclaration)o).IsInternal,
                ["Static"] = o => (object)((EventDeclaration)o).IsStatic,
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

    // Schema helper methods
    private static object TypeSchema(string name, string? baseType, params object[] properties)
    {
        if (baseType != null)
            return new { name, @base = baseType, properties = Array.Empty<object>() };
        return new { name, properties };
    }

    private static object Prop(string name, string type = "string")
        => new { name, type };

    private static object OptProp(string name, string type = "string")
        => new { name, type, optional = true };

    private static object BoolProp(string name)
        => new { name, type = "bool" };

    private static object CollProp(string name, string type = "string")
        => new { name, type, collection = true };
}
