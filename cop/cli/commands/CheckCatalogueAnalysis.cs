// Result of the static analysis behind `cop checks`.

namespace Cop.Cli.Commands;

/// <summary>The checks, commands, and warnings produced by the check catalogue.</summary>
internal sealed record CheckCatalogueAnalysis(
    IReadOnlyList<CheckCatalogueDefinition> Checks,
    IReadOnlyList<CheckCatalogueCommandEntry> Commands,
    IReadOnlyList<string> Warnings);
