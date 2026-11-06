// Represents parsed imports, aliases, and wildcard imports.

namespace Cop.Providers.SourceModel;

/// <summary>Parsed import syntax facts, not resolved symbol bindings.</summary>
public record ImportInfo(string Module, int Line)
{
    /// <summary>
    /// The declared or referenced name represented by this source element.
    /// </summary>
    public string? Name { get; init; }
    /// <summary>
    /// The local alias introduced by this import, when present.
    /// </summary>
    public string? Alias { get; init; }
    /// <summary>
    /// True when the import selects all names from its target.
    /// </summary>
    public bool IsWildcard { get; init; }
    /// <summary>
    /// True when the declaration is static.
    /// </summary>
    public bool IsStatic { get; init; }
    /// <summary>
    /// The relative-import depth, or zero for an absolute import.
    /// </summary>
    public int RelativeLevel { get; init; }
    /// <summary>
    /// The one-based source column, or null when the parser cannot provide it.
    /// </summary>
    public int? Column { get; init; }
    /// <summary>
    /// The one-based ending source line, when available.
    /// </summary>
    public int? EndLine { get; init; }
    /// <summary>
    /// The one-based exclusive ending source column, when available.
    /// </summary>
    public int? EndColumn { get; init; }
}
