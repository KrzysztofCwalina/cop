// Defines an enum declaration in the reference application's JSON data.

namespace Cop.Tools.Docs;

using System.Text.Json.Serialization;

/// <summary>The name, values, and source location of an enum declaration.</summary>
public class EnumEntry
{
    /// <summary>Gets or sets the enum's declared name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the enum's formatted member list.</summary>
    [JsonPropertyName("values")]
    public string Values { get; set; } = "";

    /// <summary>Gets or sets a link to the enum declaration, when available.</summary>
    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }
}
