// Defines a predicate declaration in the reference application's JSON data.

namespace Cop.Tools.Docs;

using System.Text.Json.Serialization;

/// <summary>Reference information for a predicate and its type-narrowing behavior.</summary>
public class PredicateEntry
{
    /// <summary>Gets or sets the predicate's declared name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the type the predicate accepts.</summary>
    [JsonPropertyName("appliesTo")]
    public string AppliesTo { get; set; } = "";

    /// <summary>Gets or sets the type established by a successful predicate, when present.</summary>
    [JsonPropertyName("narrows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Narrows { get; set; }

    /// <summary>Gets or sets the predicate's documentation text.</summary>
    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    /// <summary>Gets or sets a link to the predicate declaration, when available.</summary>
    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }
}
