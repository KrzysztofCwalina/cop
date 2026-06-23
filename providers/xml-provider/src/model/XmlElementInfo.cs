namespace Cop.Providers.Xml;

/// <summary>
/// An XML element with its local name, dotted path, direct text value, and 1-based line number.
/// </summary>
public record XmlElementInfo(string Name, string Path, string Value, int Line)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}
