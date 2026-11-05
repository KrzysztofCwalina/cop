// Defines a check declaration in the reference application's JSON data.

namespace Cop.Tools.Docs;

using System.Text.Json.Serialization;

/// <summary>The name, documentation, and source location of a check.</summary>
public class CheckEntry
{
    /// <summary>Gets or sets the check's declared name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the check's documentation text.</summary>
    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    /// <summary>Gets or sets a link to the check declaration, when available.</summary>
    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }
}
