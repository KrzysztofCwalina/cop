using Cop.Lang;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Tests.Lang;

/// <summary>
/// Test helper that creates a properly configured ScriptInterpreter
/// with code type registrations and parses source files into Documents.
/// </summary>
internal static class TestInterpreter
{
    public static ScriptInterpreter Create()
    {
        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeProvider(), registry);
        registry.RegisterProgramType();
        return new ScriptInterpreter(registry);
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