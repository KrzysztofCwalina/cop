using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Shared runtime bindings for code analysis providers.
/// Maps CLR model types to cop type names, property accessors, and collection extractors.
/// </summary>
public static class CodeBindings
{
    public static RuntimeBindings Build()
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
                [typeof(RegionInfo)] = "Region",
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
            ["Region"] = new()
            {
                ["Name"] = o => ((RegionInfo)o).Name,
                ["StartLine"] = o => (object)((RegionInfo)o).StartLine,
                ["EndLine"] = o => (object)((RegionInfo)o).EndLine,
                ["Content"] = o => ((RegionInfo)o).Content,
                ["ContentHash"] = o => ((RegionInfo)o).ContentHash,
                ["File"] = o => ((RegionInfo)o).File,
                ["Source"] = o => ((RegionInfo)o).Source,
            },
        };
    }

    internal static Dictionary<string, Func<object, List<object>>> BuildExtractors()
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
            ["Regions"] = doc =>
            {
                var file = (SourceFile)doc;
                return file.Regions.Select(r => (object)(r with { File = file })).ToList();
            },
        };
    }
}
