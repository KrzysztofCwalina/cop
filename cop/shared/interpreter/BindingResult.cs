namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// Severity of a binding diagnostic.
/// </summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// A diagnostic produced during name resolution / binding.
/// </summary>
public sealed record BindingDiagnostic(
    DiagnosticSeverity Severity,
    string Message,
    int Line,
    string? FilePath = null)
{
    public override string ToString()
    {
        var loc = FilePath is not null ? $"{FilePath}({Line})" : $"line {Line}";
        return $"{Severity}: {loc}: {Message}";
    }
}

/// <summary>
/// The result of binding a module: resolved symbols, scope tree, and diagnostics.
/// This is a "side table" approach — the original AST is preserved unmodified,
/// and resolution information is stored in lookup dictionaries keyed by AST node.
/// </summary>
public sealed class BindingResult
{
    /// <summary>
    /// The AST module that was bound.
    /// </summary>
    public ModuleNode Module { get; }

    /// <summary>
    /// The top-level (module) scope containing all declarations.
    /// </summary>
    public Scope GlobalScope { get; }

    /// <summary>
    /// Maps AST nodes (IdentifierExpr, CallExpr targets, etc.) to their resolved symbols.
    /// Not all nodes have entries — only those for which resolution succeeded.
    /// </summary>
    public IReadOnlyDictionary<AstNode, Symbol> ResolvedSymbols => _resolvedSymbols;

    /// <summary>
    /// Maps function/command declarations to their local scope (containing parameters and locals).
    /// </summary>
    public IReadOnlyDictionary<Declaration, Scope> DeclarationScopes => _declarationScopes;

    /// <summary>
    /// Diagnostics produced during binding (unresolved names, duplicates, etc.).
    /// </summary>
    public IReadOnlyList<BindingDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// True if there are any Error-severity diagnostics.
    /// </summary>
    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    // Mutable backing stores used during binding
    internal readonly Dictionary<AstNode, Symbol> _resolvedSymbols = new();
    internal readonly Dictionary<Declaration, Scope> _declarationScopes = new();
    internal readonly List<BindingDiagnostic> _diagnostics = [];

    public BindingResult(ModuleNode module, Scope globalScope)
    {
        Module = module;
        GlobalScope = globalScope;
    }

    internal void RecordResolution(AstNode node, Symbol symbol)
    {
        _resolvedSymbols[node] = symbol;
    }

    internal void RecordScope(Declaration decl, Scope scope)
    {
        _declarationScopes[decl] = scope;
    }

    internal void ReportDiagnostic(DiagnosticSeverity severity, string message, int line, string? filePath = null)
    {
        _diagnostics.Add(new BindingDiagnostic(severity, message, line, filePath));
    }
}
