namespace Cop.Lang;

/// <summary>
/// Severity of a diagnostic message produced during parsing, binding, or verification.
/// </summary>
public enum CopDiagnosticSeverity
{
    Error,
    Warning,
    Info
}

/// <summary>
/// A structured diagnostic produced during parsing, binding, import resolution,
/// or verification. Carries enough context for rich rendering (source line, caret, suggestion).
/// </summary>
public sealed record CopDiagnostic(
    CopDiagnosticSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int? Column = null,
    int? Length = null,
    string? SourceLine = null,
    string? Suggestion = null)
{
    /// <summary>
    /// Short location string: "file(line)" or "line N" if no file.
    /// </summary>
    public string Location =>
        FilePath is not null ? $"{FilePath}({Line})" : $"line {Line}";

    public override string ToString() => $"{Location}: {SeverityLabel}: {Message}";

    private string SeverityLabel => Severity switch
    {
        CopDiagnosticSeverity.Error => "error",
        CopDiagnosticSeverity.Warning => "warning",
        CopDiagnosticSeverity.Info => "info",
        _ => "error"
    };
}
