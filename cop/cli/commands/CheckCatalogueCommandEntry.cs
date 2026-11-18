// Describes one command discovered by `cop checks`.

namespace Cop.Cli.Commands;

/// <summary>A command and the checks it runs, as discovered by the check catalogue.</summary>
internal sealed record CheckCatalogueCommandEntry(
    string Id,
    string Name,
    string FilePath,
    string Origin,
    int Line,
    IReadOnlyList<string> CheckIds,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Unresolved);
