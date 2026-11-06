// Describes a property within a provider type.

using System.Text.Json;
using Cop.Lang;

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// Describes a property within a provider type.
/// </summary>
public class ProviderPropertySchema
{
    /// <summary>
    /// The member name exposed on instances of the provider type.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The Cop type name of values stored in this member.
    /// </summary>
    public string Type { get; set; } = "string";
    /// <summary>
    /// Whether an instance may omit this member.
    /// </summary>
    [JsonPropertyName("Optional")]
    public bool IsOptional { get; set; }
    /// <summary>
    /// Whether this member contains a collection rather than a scalar.
    /// </summary>
    [JsonPropertyName("Collection")]
    public bool IsCollection { get; set; }
}
