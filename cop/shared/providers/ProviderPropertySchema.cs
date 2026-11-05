// Describes a property within a provider type.

using System.Text.Json;
using Cop.Lang;

namespace Cop.Core;

/// <summary>
/// Describes a property within a provider type.
/// </summary>
public class ProviderPropertySchema
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Optional { get; set; }
    public bool Collection { get; set; }
}
