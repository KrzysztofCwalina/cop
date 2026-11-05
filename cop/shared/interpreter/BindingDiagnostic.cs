// Carries the severity, message, and location of a name-resolution diagnostic.

namespace Cop.Lang.Interpreter;

/// <summary>A diagnostic produced during name resolution and binding.</summary>
public sealed record BindingDiagnostic(
    DiagnosticSeverity Severity,
    string Message,
    int Line,
    string? FilePath = null)
{
    /// <summary>Formats the severity, source location, and diagnostic message.</summary>
    public override string ToString()
    {
        string loc = FilePath is not null ? $"{FilePath}({Line})" : $"line {Line}";
        return $"{Severity}: {loc}: {Message}";
    }
}
