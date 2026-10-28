using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Shared runtime bindings for code analysis providers.
/// Maps CLR model types to cop type names, property accessors, and collection extractors.
/// </summary>
public static class CodeBindings
{
    public static IReadOnlyDictionary<string, object?> SourceMetadata(ProviderQuery query, params string[] extensions) =>
        new Dictionary<string, object?>
        {
            ["SourceScopes"] = query.RootPath is null ? new List<object>() : new List<object>
            {
                new DataObject("SourceScope", new Dictionary<string, object?>
                {
                    ["RootPath"] = Path.GetFullPath(query.RootPath),
                    ["Extensions"] = extensions
                })
            }
        };

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
                [typeof(ExpressionInfo)] = "CodeExpression",
                [typeof(LineInfo)] = "Line",
                [typeof(SourceFile)] = "File",
                [typeof(MemberInfo)] = "Member",
                [typeof(ApiEntry)] = "Api",
                [typeof(FieldDeclaration)] = "Field",
                [typeof(PropertyDeclaration)] = "Property",
                [typeof(EventDeclaration)] = "Event",
                [typeof(RegionInfo)] = "Region",
                [typeof(ProjectInfo)] = "Project",
                [typeof(ProjectProperty)] = "ProjectProperty",
                [typeof(ImportInfo)] = "Import",
                [typeof(CallableReference)] = "CallableReference",
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
                ["File"] = o => ((SourceFile)o).Path,
                ["Project"] = o => ((ProjectInfo)o).Name,
                ["ProjectProperty"] = o => $"{((ProjectProperty)o).Name}={((ProjectProperty)o).Value}",
            },
        };
    }

    internal static Dictionary<string, Dictionary<string, Func<object, object?>>> BuildAccessors()
    {
        return new()
        {
            ["Type"] = new()
            {
                ["Name"] = o => ((TypeDeclaration)o).Name,
                ["Namespace"] = o => ((TypeDeclaration)o).Namespace,
                ["AliasTarget"] = o => ((TypeDeclaration)o).AliasTarget,
                ["TypeParameters"] = o => ((TypeDeclaration)o).TypeParameters,
                ["Column"] = o => ((TypeDeclaration)o).Column,
                ["EndLine"] = o => ((TypeDeclaration)o).EndLine,
                ["EndColumn"] = o => ((TypeDeclaration)o).EndColumn,
                ["Kind"] = o => ((TypeDeclaration)o).Kind.ToString(),
                ["Modifiers"] = o => (object)(int)((TypeDeclaration)o).Modifiers,
                ["IsSealed"] = o => (object)((TypeDeclaration)o).IsSealed,
                ["IsAbstract"] = o => (object)((TypeDeclaration)o).IsAbstract,
                ["IsStatic"] = o => (object)((TypeDeclaration)o).IsStatic,
                ["IsPublic"] = o => (object)((TypeDeclaration)o).IsPublic,
                ["Constructors"] = o => (object)((TypeDeclaration)o).Constructors,
                ["Methods"] = o => (object)((TypeDeclaration)o).Methods,
                ["NestedTypes"] = o => (object)((TypeDeclaration)o).NestedTypes,
                ["EnumValues"] = o => (object)((TypeDeclaration)o).EnumValues,
                ["BaseTypes"] = o => (object)((TypeDeclaration)o).BaseTypes,
                ["Interfaces"] = o => (object)((TypeDeclaration)o).Interfaces,
                ["Decorators"] = o => (object)((TypeDeclaration)o).Decorators,
                ["Line"] = o => (object)((TypeDeclaration)o).Line,
                ["MethodNames"] = o => (object)((TypeDeclaration)o).Methods.Select(m => m.Name).ToList(),
                ["File"] = o => ((TypeDeclaration)o).File,
                ["Source"] = o => ((TypeDeclaration)o).Source,
                ["Documented"] = o => (object)((TypeDeclaration)o).HasDocComment,
                ["Documentation"] = o => (object?)((TypeDeclaration)o).DocComment,
                ["Fields"] = o => (object)((TypeDeclaration)o).Fields,
                ["Properties"] = o => (object)((TypeDeclaration)o).Properties,
                ["Events"] = o => (object)((TypeDeclaration)o).Events,
            },
            ["Method"] = new()
            {
                ["Name"] = o => ((MethodDeclaration)o).Name,
                ["ReceiverType"] = o => ((MethodDeclaration)o).ReceiverType,
                ["TypeParameters"] = o => ((MethodDeclaration)o).TypeParameters,
                ["Column"] = o => ((MethodDeclaration)o).Column,
                ["EndLine"] = o => ((MethodDeclaration)o).EndLine,
                ["EndColumn"] = o => ((MethodDeclaration)o).EndColumn,
                ["Modifiers"] = o => (object)(int)((MethodDeclaration)o).Modifiers,
                ["IsStatic"] = o => (object)((MethodDeclaration)o).IsStatic,
                ["IsAbstract"] = o => (object)((MethodDeclaration)o).IsAbstract,
                ["IsPublic"] = o => (object)((MethodDeclaration)o).IsPublic,
                ["Parameters"] = o => (object)((MethodDeclaration)o).Parameters,
                ["Statements"] = o => (object)((MethodDeclaration)o).Statements,
                ["Decorators"] = o => (object)((MethodDeclaration)o).Decorators,
                ["ReturnType"] = o => (object?)((MethodDeclaration)o).ReturnType,
                ["Line"] = o => (object)((MethodDeclaration)o).Line,
                ["Documented"] = o => (object)((MethodDeclaration)o).HasDocComment,
                ["Documentation"] = o => (object?)((MethodDeclaration)o).DocComment,
                ["File"] = o => (object?)((MethodDeclaration)o).File,
                ["Source"] = o => ((MethodDeclaration)o).Source,
            },
            ["Parameter"] = new()
            {
                ["Name"] = o => ((ParameterDeclaration)o).Name,
                ["Optional"] = o => ((ParameterDeclaration)o).IsOptional,
                ["Column"] = o => ((ParameterDeclaration)o).Column,
                ["EndLine"] = o => ((ParameterDeclaration)o).EndLine,
                ["EndColumn"] = o => ((ParameterDeclaration)o).EndColumn,
                ["Type"] = o => (object?)((ParameterDeclaration)o).Type,
                ["Variadic"] = o => (object)((ParameterDeclaration)o).IsVariadic,
                ["Kwargs"] = o => (object)((ParameterDeclaration)o).IsKwargs,
                ["Defaulted"] = o => (object)((ParameterDeclaration)o).HasDefaultValue,
                ["DefaultValue"] = o => ((ParameterDeclaration)o).DefaultValueText,
                ["Line"] = o => (object)((ParameterDeclaration)o).Line,
            },
            ["Field"] = new()
            {
                ["Name"] = o => ((FieldDeclaration)o).Name,
                ["Type"] = o => (object?)((FieldDeclaration)o).Type,
                ["Modifiers"] = o => (object)(int)((FieldDeclaration)o).Modifiers,
                ["IsStatic"] = o => (object)((FieldDeclaration)o).IsStatic,
                ["IsPublic"] = o => (object)((FieldDeclaration)o).IsPublic,
                ["Line"] = o => (object)((FieldDeclaration)o).Line,
            },
            ["Property"] = new()
            {
                ["Name"] = o => ((PropertyDeclaration)o).Name,
                ["Type"] = o => (object?)((PropertyDeclaration)o).Type,
                ["Modifiers"] = o => (object)(int)((PropertyDeclaration)o).Modifiers,
                ["IsStatic"] = o => (object)((PropertyDeclaration)o).IsStatic,
                ["IsAbstract"] = o => (object)((PropertyDeclaration)o).IsAbstract,
                ["IsPublic"] = o => (object)((PropertyDeclaration)o).IsPublic,
                ["HasGetter"] = o => (object)((PropertyDeclaration)o).HasGetter,
                ["HasSetter"] = o => (object)((PropertyDeclaration)o).HasSetter,
                ["Documented"] = o => (object)((PropertyDeclaration)o).HasDocComment,
                ["Documentation"] = o => (object?)((PropertyDeclaration)o).DocComment,
                ["Line"] = o => (object)((PropertyDeclaration)o).Line,
            },
            ["Event"] = new()
            {
                ["Name"] = o => ((EventDeclaration)o).Name,
                ["Type"] = o => (object?)((EventDeclaration)o).Type,
                ["Modifiers"] = o => (object)(int)((EventDeclaration)o).Modifiers,
                ["IsStatic"] = o => (object)((EventDeclaration)o).IsStatic,
                ["IsPublic"] = o => (object)((EventDeclaration)o).IsPublic,
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
                ["Column"] = o => ((StatementInfo)o).Column,
                ["EndLine"] = o => ((StatementInfo)o).EndLine,
                ["EndColumn"] = o => ((StatementInfo)o).EndColumn,
                ["Keywords"] = o => (object)((StatementInfo)o).Keywords,
                ["TypeName"] = o => ((StatementInfo)o).TypeName,
                ["ReceiverType"] = o => ((StatementInfo)o).ReceiverType,
                ["ValueType"] = o => ((StatementInfo)o).ValueType,
                ["ReferencedCallable"] = o => ((StatementInfo)o).ReferencedCallable,
                ["MemberName"] = o => ((StatementInfo)o).MemberName,
                ["Arguments"] = o => (object)((StatementInfo)o).Arguments,
                ["CallTarget"] = o => ((StatementInfo)o).CallTarget,
                ["ValueExpression"] = o => ((StatementInfo)o).ValueExpression,
                ["ArgumentExpressions"] = o => ((StatementInfo)o).ArgumentExpressions,
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
                ["Braced"] = o => (object)((StatementInfo)o).IsBraced,
                ["CopIgnore"] = o => ((StatementInfo)o).CopIgnore,
                ["ConstructedTypeInterfaces"] = o => (object)((StatementInfo)o).ConstructedTypeInterfaces,
            },
            ["CodeExpression"] = new()
            {
                ["Kind"] = o => ((ExpressionInfo)o).Kind,
                ["Name"] = o => ((ExpressionInfo)o).Name,
                ["StringValue"] = o => ((ExpressionInfo)o).StringValue,
                ["Target"] = o => ((ExpressionInfo)o).Target,
                ["Children"] = o => ((ExpressionInfo)o).Children,
            },
            ["CallableReference"] = new()
            {
                ["Name"] = o => ((CallableReference)o).Name,
                ["Signature"] = o => ((CallableReference)o).Signature,
                ["DeclaringType"] = o => ((CallableReference)o).DeclaringType,
                ["Namespace"] = o => ((CallableReference)o).Namespace,
                ["ReturnType"] = o => ((CallableReference)o).ReturnType,
                ["Parameters"] = o => ((CallableReference)o).Parameters,
                ["SourceFile"] = o => ((CallableReference)o).SourceFile,
                ["Line"] = o => ((CallableReference)o).Line,
                ["Column"] = o => ((CallableReference)o).Column,
                ["EndLine"] = o => ((CallableReference)o).EndLine,
                ["EndColumn"] = o => ((CallableReference)o).EndColumn,
            },
            ["Line"] = new()
            {
                ["Text"] = o => ((LineInfo)o).Text,
                ["Number"] = o => (object)((LineInfo)o).Number,
                ["Kind"] = o => ((LineInfo)o).Kind,
                ["File"] = o => ((LineInfo)o).File,
                ["Source"] = o => ((LineInfo)o).Source,
                ["CopIgnore"] = o => ((LineInfo)o).CopIgnore,
                ["PreviousText"] = o => ((LineInfo)o).PreviousText,
                ["NextText"] = o => ((LineInfo)o).NextText,
            },
            ["File"] = new()
            {
                ["Path"] = o => ((SourceFile)o).Path,
                ["FullPath"] = o => ((SourceFile)o).FullPath,
                ["SourceRoot"] = o => ((SourceFile)o).SourceRoot,
                ["Language"] = o => ((SourceFile)o).Language,
                ["Namespace"] = o => ((SourceFile)o).Namespace,
                ["Usings"] = o => (object)((SourceFile)o).Usings,
                ["Imports"] = o => ((SourceFile)o).Imports,
                ["Types"] = o => (object)((SourceFile)o).Types,
                ["Methods"] = o => (object)((SourceFile)o).Methods,
                ["Projects"] = o => (object)((SourceFile)o).Projects,
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
            ["Project"] = new()
            {
                ["Name"] = o => ((ProjectInfo)o).Name,
                ["Path"] = o => ((ProjectInfo)o).Path,
                ["Language"] = o => ((ProjectInfo)o).Language,
                ["References"] = o => (object)((ProjectInfo)o).References,
                ["Packages"] = o => (object)((ProjectInfo)o).Packages,
                ["Frameworks"] = o => (object)((ProjectInfo)o).Frameworks,
                ["SourceFiles"] = o => ((ProjectInfo)o).SourceFiles,
                ["Properties"] = o => (object)((ProjectInfo)o).Properties
                    .Select(kv => new ProjectProperty(kv.Key, kv.Value)).ToList(),
            },
            ["ProjectProperty"] = new()
            {
                ["Name"] = o => ((ProjectProperty)o).Name,
                ["Value"] = o => ((ProjectProperty)o).Value,
            },
            ["Import"] = new()
            {
                ["Module"] = o => ((ImportInfo)o).Module,
                ["Name"] = o => ((ImportInfo)o).Name,
                ["Alias"] = o => ((ImportInfo)o).Alias,
                ["IsWildcard"] = o => ((ImportInfo)o).IsWildcard,
                ["IsStatic"] = o => ((ImportInfo)o).IsStatic,
                ["RelativeLevel"] = o => ((ImportInfo)o).RelativeLevel,
                ["Line"] = o => ((ImportInfo)o).Line,
                ["Column"] = o => ((ImportInfo)o).Column,
                ["EndLine"] = o => ((ImportInfo)o).EndLine,
                ["EndColumn"] = o => ((ImportInfo)o).EndColumn,
            },
        };
    }

    public static Dictionary<string, Func<object, List<object>>> BuildExtractors()
    {
        return new()
        {
            ["Types"] = doc => ((SourceFile)doc).Types.Cast<object>().ToList(),
            ["Statements"] = doc => ((SourceFile)doc).Statements.Cast<object>().ToList(),
            ["Methods"] = doc =>
            {
                var file = (SourceFile)doc;
                var result = file.Methods.Cast<object>().ToList();
                CollectMethods(file.Types, result);
                return result.Distinct(ReferenceEqualityComparer.Instance).ToList();
            },
            ["Calls"] = doc => ((SourceFile)doc).Statements.Where(s => s.Kind == "call").Cast<object>().ToList(),
            ["Lines"] = doc =>
            {
                var file = (SourceFile)doc;
                var result = new List<object>();
                string pendingCopIgnore = "";
                for (int i = 0; i < file.Lines.Count; i++)
                {
                    var text = file.Lines[i];
                    var lineNum = i + 1;
                    var kind = file.CommentLines.Contains(lineNum) ? "comment"
                             : string.IsNullOrWhiteSpace(text) ? "blank"
                             : "code";
                    var lineInfo = new LineInfo(text, lineNum)
                    {
                        File = file,
                        Kind = kind,
                        CopIgnore = pendingCopIgnore,
                        PreviousText = i > 0 ? file.Lines[i - 1] : "",
                        NextText = i < file.Lines.Count - 1 ? file.Lines[i + 1] : ""
                    };
                    result.Add(lineInfo);

                    // Detect cop-ignore: in comment lines — applies to NEXT line
                    pendingCopIgnore = "";
                    if (kind == "comment")
                    {
                        var idx = text.IndexOf("cop-ignore:", StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            pendingCopIgnore = text[(idx + "cop-ignore:".Length)..].Trim();
                        }
                    }
                }
                return result;
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

    /// <summary>Recursively flattens all methods declared in the given types (including nested types).</summary>
    private static void CollectMethods(IEnumerable<TypeDeclaration> types, List<object> result)
    {
        foreach (var type in types)
        {
            result.AddRange(type.DeclaredMethods);
            CollectMethods(type.NestedTypes, result);
        }
    }
}
