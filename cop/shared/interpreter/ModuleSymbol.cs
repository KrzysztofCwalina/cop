// An imported module.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// An imported module.
/// </summary>
public sealed class ModuleSymbol : Symbol
{
    public IReadOnlyDictionary<string, Symbol> Exports { get; }

    public ModuleSymbol(string name, IReadOnlyDictionary<string, Symbol> exports)
        : base(name, SymbolKind.Module)
    {
        Exports = exports;
    }
}
