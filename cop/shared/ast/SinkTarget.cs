// Describes the named destination at the end of a command pipeline.

namespace Cop.Lang;

/// <summary>
/// Represents the destination sink and optional arguments in a chained pipeline.
/// </summary>
public record SinkTarget(string Name, List<object>? Args = null)
{
    /// <summary>The namespace-qualified sink name, such as file.Write.</summary>
    public string Name { get; init; } = Name;

    /// <summary>Optional sink arguments, such as the destination file path.</summary>
    public List<object>? Args { get; init; } = Args;
}
