// Models a YAML scalar entry and its source provenance.

namespace Cop.Providers.Yaml;

/// <summary>
/// A single key in a YAML mapping, flattened with its dotted path, scalar value,
/// and 1-based line number. Sequence elements appear in the path as <c>[]</c> segments,
/// e.g. <c>jobs.build.steps[].uses</c>.
/// </summary>
public record YamlEntryInfo(string Path, string Key, string Value, int Line, int Document)
{
    /// <summary>Gets the YAML source file containing this entry.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
