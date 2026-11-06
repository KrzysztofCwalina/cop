// Defines the package-level reference data serialized into the HTML application.

namespace Cop.Tools.Docs;

using System.Text.Json.Serialization;

/// <summary>The declarations, overview, and samples displayed for a package.</summary>
public class PackageEntry
{
    /// <summary>Gets or sets the package's display label.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>Gets or sets the package overview content.</summary>
    [JsonPropertyName("overview")]
    public string Overview { get; set; } = "";

    /// <summary>Gets or sets the package's executable or illustrative code samples.</summary>
    [JsonPropertyName("samples")]
    public List<SampleEntry> Samples { get; set; } = [];

    /// <summary>Gets or sets the names of collections exposed by the package.</summary>
    [JsonPropertyName("collections")]
    public List<string> Collections { get; set; } = [];

    /// <summary>Gets or sets the documented enum declarations, when present.</summary>
    [JsonPropertyName("enums")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<EnumEntry>? Enums { get; set; }

    /// <summary>Gets or sets type documentation keyed by type name.</summary>
    [JsonPropertyName("types")]
    public Dictionary<string, TypeEntry> Types { get; set; } = [];

    /// <summary>Gets or sets the package's predicate declarations.</summary>
    [JsonPropertyName("predicates")]
    public List<PredicateEntry> Predicates { get; set; } = [];

    /// <summary>Gets or sets the package's function declarations.</summary>
    [JsonPropertyName("functions")]
    public List<FunctionEntry> Functions { get; set; } = [];

    /// <summary>Gets or sets the package's check declarations.</summary>
    [JsonPropertyName("checks")]
    public List<CheckEntry> Checks { get; set; } = [];

    /// <summary>Gets or sets the package's command declarations.</summary>
    [JsonPropertyName("commands")]
    public List<CommandEntry> Commands { get; set; } = [];
}
