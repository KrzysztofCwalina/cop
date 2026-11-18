// Describes one check discovered by `cop checks`.

namespace Cop.Cli.Commands;

/// <summary>A check declaration discovered by the check catalogue.</summary>
internal sealed record CheckCatalogueDefinition(
    string Id,
    string Name,
    string FilePath,
    string Origin,
    int Line,
    string? Description,
    string Source,
    string Kind);
