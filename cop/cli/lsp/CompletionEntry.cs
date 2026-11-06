// Carries a completion label and its language-server presentation metadata.

namespace Cop.Cli.Lsp;

/// <summary>
/// A completion item with its label, description, and LSP completion-item kind.
/// </summary>
internal readonly record struct CompletionEntry(string Label, string Detail, int Kind);
