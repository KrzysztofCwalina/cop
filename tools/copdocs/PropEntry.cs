// Defines a type property's reference data.

namespace Cop.Tools.Docs;

using System.Text.Json.Serialization;

/// <summary>The name, type, and documentation of a type property.</summary>
public class PropEntry
{
    /// <summary>Gets or sets the property's declared name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the property's formatted type name.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>Gets or sets the property's documentation text.</summary>
    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";
}
