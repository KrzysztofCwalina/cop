// Root node representing a source file / module.

namespace Cop.Lang.Ast;

// ============================================================================
// Module
// ============================================================================

/// <summary>
/// Root node representing a source file / module.
/// </summary>
public record ModuleNode(List<Declaration> Declarations, int Line = 0) : AstNode(Line)
{
    /// <summary>Decoded literal feed declarations. Loading policy remains the caller's responsibility.</summary>
    public List<string> FeedPaths { get; init; } = [];
}
