// Exposes unstructured text files through the source-file model.

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

    public override IReadOnlyList<string> Extensions { get; } = ExtensionLanguageMap.Keys.ToList();
    public override string Language => "text";

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        string ext = Path.GetExtension(filePath);
        string language = ExtensionLanguageMap.TryGetValue(ext, out string? lang) ? lang : "text";

        return new SourceFile(filePath, language, [], [], sourceText);
    }
}
