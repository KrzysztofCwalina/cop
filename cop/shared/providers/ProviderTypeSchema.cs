// Describes a single type in the provider schema.

using System.Text.Json;
using Cop.Lang;

namespace Cop.Core;

/// <summary>
/// Describes a single type in the provider schema.
/// </summary>
public class ProviderTypeSchema
{
    /// <summary>
    /// The Cop type name used in provider records and collection schemas.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The optional base type inherited by this provider type.
    /// </summary>
    public string? Base { get; set; }
    /// <summary>
    /// The member definitions declared by this provider type.
    /// </summary>
    public List<ProviderPropertySchema> Properties { get; set; } = [];
}
