// Defines text ranges and style metadata for rich output.

namespace Cop.Lang;

/// <summary>
/// A span of text with optional style annotations.
/// Annotations are key-value pairs parsed from the %annotation syntax.
/// </summary>
public record TextSpan(string Text, IReadOnlyDictionary<string, string>? Annotations = null)
{
    /// <summary>
    /// Whether this text segment contains any named formatting annotations.
    /// </summary>
    public bool HasAnnotations => Annotations != null && Annotations.Count > 0;
}
