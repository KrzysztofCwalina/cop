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
        CodeTypeRegistrar.Register(registry);
        registry.RegisterProgramType();
        return new ScriptInterpreter(registry);
    }

    public static List<Document> ParseSourceFiles(params string[] filePaths)
    {
        var parserRegistry = SourceParserRegistry.CreateDefault();
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
                var relativePath = Path.GetFileName(filePath);
                var normalized = sourceFile with { Path = relativePath };
                for (int i = 0; i < normalized.Statements.Count; i++)
                    normalized.Statements[i] = normalized.Statements[i] with { File = normalized };
                for (int i = 0; i < normalized.Types.Count; i++)
                    normalized.Types[i] = normalized.Types[i] with { File = normalized };
                documents.Add(new Document(relativePath, normalized.Language, normalized));
            }
            catch { }
        }
        return documents;
    }
}