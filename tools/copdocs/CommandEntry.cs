// Defines a command declaration in the reference application's JSON data.

namespace Cop.Tools.Docs;

using System.Text.Json.Serialization;

/// <summary>Reference information for a named command and its parameters.</summary>
public class CommandEntry
{
    /// <summary>Gets or sets the command's declared name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the formatted command parameter list.</summary>
    [JsonPropertyName("params")]
    public string Params { get; set; } = "";

    /// <summary>Gets or sets the command's documentation text.</summary>
    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    /// <summary>Gets or sets a link to the command declaration, when available.</summary>
    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }
}
