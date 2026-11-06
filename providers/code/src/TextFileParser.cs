// Exposes unstructured text files through the source-file model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Exposes plain-text files as source lines without language declarations.
/// </summary>
public class TextFileParser : ISourceParser
{
    private static readonly Dictionary<string, string> s_extensionLanguageMap = new(StringComparer.OrdinalIgnoreCase)
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

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions { get; } = s_extensionLanguageMap.Keys.ToList();
    /// <inheritdoc />
    public override string Language => "text";

    /// <inheritdoc />
    public override ValueTask<SourceFile?> ParseAsync(
        string filePath, string sourceText, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Parse(filePath, sourceText));
    }

    /// <summary>Builds a text model directly from already-loaded source text.</summary>
    public SourceFile? Parse(string filePath, string sourceText)
    {
        string ext = Path.GetExtension(filePath);
        string language = s_extensionLanguageMap.TryGetValue(ext, out string? lang) ? lang : "text";

        return new SourceFile(filePath, language, [], [], sourceText);
    }
}
