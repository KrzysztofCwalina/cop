// An imported module.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// An imported module.
/// </summary>
public sealed class ModuleSymbol : Symbol
{
    /// <summary>
    /// The symbols made visible to importers of this module.
    /// </summary>
    public IReadOnlyDictionary<string, Symbol> Exports { get; }

    public ModuleSymbol(string name, IReadOnlyDictionary<string, Symbol> exports)
        : base(name, SymbolKind.Module)
    {
        Exports = exports;
    }
}
