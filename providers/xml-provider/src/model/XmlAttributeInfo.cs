namespace Cop.Providers.Xml;

/// <summary>
/// An XML attribute with its local name, value, owning element, and 1-based line number.
/// </summary>
public record XmlAttributeInfo(string Name, string Value, string ElementName, string ElementPath, int Line)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}
