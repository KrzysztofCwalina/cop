// Groups package reference entries and their navigation categories for HTML generation.

namespace Cop.Tools.Docs;

/// <summary>
/// The full reference data model matching the HTML app's JavaScript schema.
/// </summary>
public class ReferenceData
{
    /// <summary>Gets or sets the navigation categories in display order.</summary>
    public List<Category> Categories { get; set; } = [];

    /// <summary>Gets or sets reference entries keyed by package identifier.</summary>
    public Dictionary<string, PackageEntry> Packages { get; set; } = [];
}
