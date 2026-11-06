// Describes a named language element displayed by editor tooling.

namespace Cop.Lang;

/// <summary>
/// A named language element with a short description for editor presentation.
/// </summary>
public readonly record struct MetadataEntry(string Name, string Detail);
