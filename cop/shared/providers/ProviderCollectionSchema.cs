// Describes a collection exposed by the provider.

using System.Text.Json;
using Cop.Lang;

namespace Cop.Core;

/// <summary>
/// Describes a collection exposed by the provider.
/// </summary>
public class ProviderCollectionSchema
{
    /// <summary>
    /// The collection name exposed within the provider namespace.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The declared Cop type of elements in this collection.
    /// </summary>
    public string ItemType { get; set; } = "";
}
