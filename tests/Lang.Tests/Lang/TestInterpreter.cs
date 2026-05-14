using Cop.Lang;
using Cop.Providers;
using Cop.Providers.Markdown;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Tests.Lang;

/// <summary>
/// Test helper that creates a properly configured ScriptInterpreter
/// with code type registrations and parses source files into Documents.
/// </summary>
internal static class TestInterpreter
{
    private static readonly Lazy<ScriptFile> _codeCop = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "code.cop");
        return ScriptParser.Parse(File.ReadAllText(path), "code.cop");
    });

    private static readonly Lazy<ScriptFile> _coreCop = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "core.cop");
        return ScriptParser.Parse(File.ReadAllText(path), "core.cop");
    });

    /// <summary>The parsed code.cop package (flags definitions + isX predicates).</summary>
    public static ScriptFile CodePackage => _codeCop.Value;

    /// <summary>The parsed core.cop package (intrinsic function declarations).</summary>
    public static ScriptFile CorePackage => _coreCop.Value;

    public static ScriptInterpreter Create() => Create(out _);

    /// <summary>
    /// Creates a configured interpreter with documents pre-registered as namespaced collections.
    /// This makes data('csharp').Types etc. resolvable in tests.
    /// </summary>
    public static (ScriptInterpreter Interpreter, List<Document> Documents) CreateWithDocuments(params string[] filePaths)
    {
        var interp = Create(out var registry);
        var docs = ParseSourceFiles(filePaths);
        RegisterDocumentsAsNamespaced(registry, docs);
        return (interp, docs);
    }

    public static ScriptInterpreter Create(out TypeRegistry registry)
    {
        registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        ProviderLoader.RegisterSchema(new MarkdownProvider(), registry);
        var codeFile = CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);
        if (codeFile.EnumDefinitions != null)
            registry.LoadEnumDefinitions(codeFile.EnumDefinitions);
        if (codeFile.TypeImports != null)
            registry.LoadTypeImports(codeFile.TypeImports);
        registry.RegisterProgramType();
        return new ScriptInterpreter(registry);
    }

    /// <summary>
    /// Registers document collection data into namespaced collections in the TypeRegistry.
    /// Mimics what ProviderLoader.QueryAndRegister does in production — makes
    /// data('python').Types etc. resolvable in tests.
    /// </summary>
    public static void RegisterDocumentsAsNamespaced(TypeRegistry registry, List<Document> documents)
    {
        foreach (var collName in registry.GetDocumentCollectionNames())
        {
            // Group items by document language
            var byLanguage = new Dictionary<string, List<object>>();
            foreach (var doc in documents)
            {
                var items = registry.GetCollectionItems(collName, doc);
                if (items is null || items.Count == 0) continue;
                if (!byLanguage.TryGetValue(doc.Language, out var langItems))
                {
                    langItems = new List<object>();
                    byLanguage[doc.Language] = langItems;
                }
                langItems.AddRange(items);
            }
            foreach (var (lang, items) in byLanguage)
            {
                registry.AppendNamespacedCollection(lang, collName, items);
            }
        }
    }

    public static List<Document> ParseSourceFiles(params string[] filePaths)
    {
        // Find common root to compute relative paths that preserve directory structure
        var commonRoot = filePaths.Length > 1
            ? FindCommonRoot(filePaths)
            : Path.GetDirectoryName(filePaths[0]) ?? "";

        var parserRegistry = new SourceParserRegistry();
        parserRegistry.Register(new CSharpSourceParser());
        parserRegistry.Register(new TextFileParser());
        parserRegistry.Register(new PythonSourceParser());
        parserRegistry.Register(new JavaScriptSourceParser());
        var documents = new List<Document>();
        foreach (var filePath in filePaths)
        {
            var ext = Path.GetExtension(filePath);
            var parser = parserRegistry.GetParser(ext);
            if (parser == null) continue;
            try
            {
                var text = File.ReadAllText(filePath);
                var sourceFile = parser.Parse(filePath, text);
                if (sourceFile == null) continue;
                var relativePath = string.IsNullOrEmpty(commonRoot)
                    ? Path.GetFileName(filePath)
                    : Path.GetRelativePath(commonRoot, filePath);
                var normalized = sourceFile with { Path = relativePath };
                for (int i = 0; i < normalized.Statements.Count; i++)
                    normalized.Statements[i].File = normalized;
                for (int i = 0; i < normalized.Types.Count; i++)
                    normalized.Types[i] = normalized.Types[i] with { File = normalized };
                documents.Add(new Document(relativePath, normalized.Language, normalized));
            }
            catch { }
        }
        return documents;
    }

    private static string FindCommonRoot(string[] paths)
    {
        var dirs = paths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p)) ?? "").ToArray();
        if (dirs.Length == 0) return "";
        var common = dirs[0];
        foreach (var dir in dirs.Skip(1))
        {
            while (!dir.StartsWith(common, StringComparison.OrdinalIgnoreCase) && common.Length > 0)
            {
                common = Path.GetDirectoryName(common) ?? "";
            }
        }
        return common;
    }
}