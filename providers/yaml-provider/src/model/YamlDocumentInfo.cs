namespace Cop.Providers.Yaml;

/// <summary>
/// A YAML document. A single file may contain multiple documents separated by <c>---</c>;
/// <see cref="Index"/> is the 0-based document position within the file.
/// </summary>
public record YamlDocumentInfo(int Index, int Line)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}
