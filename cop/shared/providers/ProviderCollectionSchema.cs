// Describes a collection exposed by the provider.

using System.Text.Json;
using Cop.Lang;

namespace Cop.Core;

/// <summary>
/// Describes a collection exposed by the provider.
/// </summary>
public class ProviderCollectionSchema
{
    public string Name { get; set; } = "";
    public string ItemType { get; set; } = "";
}
