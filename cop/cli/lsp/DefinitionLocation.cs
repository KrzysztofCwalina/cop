// Locates a symbol definition for editor navigation.

namespace Cop.Cli.Lsp;

/// <summary>
/// A definition's file path and zero-based line and character span.
/// </summary>
internal sealed record DefinitionLocation(string FilePath, int Line, int Column, int Length);
