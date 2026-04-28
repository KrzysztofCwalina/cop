using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Built-in provider for source code analysis data (Types, Methods, Statements, etc.).
/// Uses ObjectCollections format — scans and parses source files, returns flat CLR object collections.
/// Self-initializes with all built-in source parsers and registers document loading capability.
/// </summary>
public class CodeProvider : DataProvider, ICapabilityProvider
{
    public override DataFormat SupportedFormats => DataFormat.ObjectCollections;

    /// <summary>
    /// Source parser registry. Auto-initialized with all built-in parsers.
    /// </summary>
    public SourceParserRegistry Parsers { get; } = CreateDefaultParsers();

    private static SourceParserRegistry CreateDefaultParsers()
    {
        var registry = new SourceParserRegistry();
        registry.Register(new CSharpSourceParser());
        registry.Register(new TextFileParser());
        registry.Register(new PythonSourceParser());
        registry.Register(new JavaScriptSourceParser());
        return registry;
    }

    /// <summary>
    /// Registers the document loader for Load('path') — loads DLL assemblies
    /// into Documents for collection extraction.
    /// </summary>
    public void RegisterCapabilities(TypeRegistry registry, string rootPath)
    {
        registry.RegisterDocumentLoader(path =>
        {
            var sourceFile = AssemblyApiReader.ReadAssembly(path);
            for (int i = 0; i < sourceFile.Types.Count; i++)
                sourceFile.Types[i] = sourceFile.Types[i] with { File = sourceFile };
            return [new Document(path, sourceFile.Language, sourceFile)];
        });
    }

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

    /// <summary>
    /// Scans source files, parses them, and returns flat collections as global data.
    /// Moves file scanning/parsing logic that was previously in Engine.
    /// </summary>
    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query)
    {
        if (Parsers is null)
            throw new InvalidOperationException("CodeProvider.Parsers must be set before querying.");
        if (query.RootPath is null)
            return new();

        var rootPath = query.RootPath;
        var excluded = query.ExcludedDirectories;

        // Collect source file paths
        var filePaths = new List<string>();
        CollectSourceFiles(rootPath, Parsers, excluded, filePaths);

        // Parse files in parallel
        var sourceFiles = new System.Collections.Concurrent.ConcurrentBag<SourceFile>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                var ext = Path.GetExtension(filePath);
                var parser = Parsers.GetParser(ext);
                if (parser == null) return;

                SourceFile? sourceFile;
                try
                {
                    var text = File.ReadAllText(filePath);
                    sourceFile = parser.Parse(filePath, text);
                }
                catch
                {
                    return;
                }

                if (sourceFile == null) return;

                var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                var normalizedFile = sourceFile with { Path = relativePath };

                // Stamp StatementInfo.File references
                for (int i = 0; i < normalizedFile.Statements.Count; i++)
                    normalizedFile.Statements[i].File = normalizedFile;

                // Stamp TypeDeclaration.File references
                for (int i = 0; i < normalizedFile.Types.Count; i++)
                    normalizedFile.Types[i] = normalizedFile.Types[i] with { File = normalizedFile };

                sourceFiles.Add(normalizedFile);
            });

        // Sort for deterministic order
        var sorted = sourceFiles.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        // Extract flat collections from all source files
        var extractors = BuildExtractors();
        var collections = new Dictionary<string, List<object>>();

        // Only extract requested collections if specified, otherwise extract all
        var requested = query.RequestedCollections;
        foreach (var (name, extractor) in extractors)
        {
            if (requested != null && !requested.Contains(name))
                continue;

            var items = new List<object>();
            foreach (var file in sorted)
                items.AddRange(extractor(file));
            collections[name] = items;
        }

        return collections;
    }

    private static void CollectSourceFiles(string dir, SourceParserRegistry parsers, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (parsers.GetParser(ext) != null)
                    result.Add(file);
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectSourceFiles(subDir, parsers, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
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
            ["Regions"] = doc =>
            {
                var file = (SourceFile)doc;
                return file.Regions.Select(r => (object)(r with { File = file })).ToList();
            },
        };
    }

}
