// Categorizes diagnostics emitted by the language pipeline.

namespace Cop.Lang;

/// <summary>
/// Severity of a diagnostic produced during parsing, binding, or verification.
/// </summary>
public enum CopDiagnosticSeverity
{
    /// <summary>A failure that prevents successful verification or execution.</summary>
    Error,

    /// <summary>A potential problem that does not itself prevent execution.</summary>
    Warning,

    /// <summary>An informational diagnostic that does not indicate a failure.</summary>
    Info
}
