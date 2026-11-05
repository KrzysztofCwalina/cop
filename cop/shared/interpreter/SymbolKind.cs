// Categories of named entities recorded during symbol binding.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// Categories of named entities recorded during symbol binding.
/// </summary>
// ============================================================================
// Symbol Kinds
// ============================================================================

public enum SymbolKind
{
    Variable,
    Parameter,
    Function,
    Type,
    Enum,
    EnumMember,
    Module
}
