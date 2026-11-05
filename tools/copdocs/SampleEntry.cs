// Defines a sample displayed in a package reference page.

namespace Cop.Tools.Docs;

using System.Text.Json.Serialization;

/// <summary>A titled Cop code sample shown in a package's reference page.</summary>
public class SampleEntry
{
    /// <summary>Gets or sets the sample's display title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>Gets or sets the Cop source code of the sample.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";
}
