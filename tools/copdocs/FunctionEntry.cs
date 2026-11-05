// Defines a function declaration in the reference application's JSON data.

namespace Cop.Tools.Docs;

using System.Text.Json.Serialization;

/// <summary>Reference information for a function's signature and documentation.</summary>
public class FunctionEntry
{
    /// <summary>Gets or sets the function's declared name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the formatted function parameter list.</summary>
    [JsonPropertyName("params")]
    public string Params { get; set; } = "";

    /// <summary>Gets or sets the formatted return type.</summary>
    [JsonPropertyName("returns")]
    public string Returns { get; set; } = "";

    /// <summary>Gets or sets the receiver type for a piped function, when present.</summary>
    [JsonPropertyName("appliesTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppliesTo { get; set; }

    /// <summary>Gets or sets the function's documentation text.</summary>
    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    /// <summary>Gets or sets a link to the function declaration, when available.</summary>
    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }
}
