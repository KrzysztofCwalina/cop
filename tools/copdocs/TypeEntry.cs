// Defines a type or trait declaration in the reference application's JSON data.

namespace Cop.Tools.Docs;

using System.Text.Json.Serialization;

/// <summary>Reference information for a type or trait and its properties.</summary>
public class TypeEntry
{
    /// <summary>Gets or sets the type's documentation text, when present.</summary>
    [JsonPropertyName("desc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Desc { get; set; }

    /// <summary>Gets or sets a link to the type declaration, when available.</summary>
    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }

    /// <summary>Gets or sets whether the declaration represents a trait.</summary>
    [JsonPropertyName("isTrait")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsTrait { get; set; }

    /// <summary>Gets or sets the declared base type name, when present.</summary>
    [JsonPropertyName("baseType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseType { get; set; }

    /// <summary>Gets or sets the types known to conform to this trait.</summary>
    [JsonPropertyName("conformers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Conformers { get; set; }

    /// <summary>Gets or sets the traits implemented by this type.</summary>
    [JsonPropertyName("traits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Traits { get; set; }

    /// <summary>Gets or sets the documented properties of this type.</summary>
    [JsonPropertyName("props")]
    public List<PropEntry> Props { get; set; } = [];
}
