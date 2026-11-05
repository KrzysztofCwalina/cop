// Describes a single type in the provider schema.

using System.Text.Json;
using Cop.Lang;

namespace Cop.Core;

/// <summary>
/// Describes a single type in the provider schema.
/// </summary>
public class ProviderTypeSchema
{
    public string Name { get; set; } = "";
    public string? Base { get; set; }
    public List<ProviderPropertySchema> Properties { get; set; } = [];
}
