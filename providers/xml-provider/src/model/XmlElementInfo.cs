// Models an XML element and its source provenance.

namespace Cop.Providers.Xml;

/// <summary>
/// An XML element with its local name, dotted path, direct text value, and 1-based line number.
/// </summary>
public record XmlElementInfo(string Name, string Path, string Value, int Line)
{
    /// <summary>Gets the XML source file containing this element.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
