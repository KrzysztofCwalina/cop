// Parsed Cop source used to build a check catalogue.

using Cop.Lang.Ast;

namespace Cop.Cli.Commands;

/// <summary>A parsed Cop source file included in a check catalogue.</summary>
internal sealed record CheckCatalogueSource(
    ModuleNode Module,
    string FilePath,
    string Source,
    string? PackageName);
