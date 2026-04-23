using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class TextFileParser : ISourceParser
{
    private static readonly Dictionary<string, string> ExtensionLanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "markdown",
        [".txt"] = "text",
        [".xml"] = "xml",
        [".json"] = "json",
        [".csproj"] = "xml",
        [".props"] = "xml",
        [".targets"] = "xml",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
        [".config"] = "xml",
        [".editorconfig"] = "editorconfig",
        [".sln"] = "text",
        [".bicep"] = "bicep",
    };

    public IReadOnlyList<string> Extensions { get; } = ExtensionLanguageMap.Keys.ToList();
    public string Language => "text";

    public SourceFile? Parse(string filePath, string sourceText)
    {
        var ext = Path.GetExtension(filePath);
        var language = ExtensionLanguageMap.TryGetValue(ext, out var lang) ? lang : "text";

        return new SourceFile(filePath, language, [], [], sourceText);
    }
}
