// Defines a navigation category in the reference application's JSON data.

namespace Cop.Tools.Docs;

using System.Text.Json.Serialization;

/// <summary>A navigation group containing related packages.</summary>
public class Category
{
    /// <summary>Gets or sets the category's display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the identifiers of packages in this category.</summary>
    [JsonPropertyName("packages")]
    public List<string> PackageIds { get; set; } = [];
}
