// Models an XML attribute and its source provenance.

namespace Cop.Providers.Xml;

/// <summary>
/// An XML attribute with its local name, value, owning element, and 1-based line number.
/// </summary>
public record XmlAttributeInfo(string Name, string Value, string ElementName, string ElementPath, int Line)
{
    /// <summary>Gets the XML source file containing this attribute.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
