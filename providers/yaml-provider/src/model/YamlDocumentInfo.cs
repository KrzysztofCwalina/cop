// Models a YAML document boundary and its source provenance.

namespace Cop.Providers.Yaml;

/// <summary>
/// A YAML document. A single file may contain multiple documents separated by <c>---</c>;
/// <see cref="Index"/> is the 0-based document position within the file.
/// </summary>
public record YamlDocumentInfo(int Index, int Line)
{
    /// <summary>Gets the YAML source file containing this document.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
