// Classifies diagnostics produced during symbol binding.

namespace Cop.Lang.Interpreter;

/// <summary>Severity of a binding diagnostic.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Additional context that does not indicate a binding problem.</summary>
    Info,

    /// <summary>A potential binding problem that is not an error.</summary>
    Warning,

    /// <summary>A binding failure, such as an unresolved or duplicate name.</summary>
    Error
}
