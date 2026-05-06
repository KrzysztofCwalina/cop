namespace Cop.Lang;

/// <summary>
/// Thrown when a FAIL command is executed or when a function body evaluates to FAIL(...).
/// This represents a code bug — a situation the programmer asserts should never occur.
/// The engine catches this and reports it as a fatal diagnostic.
/// </summary>
public class FailException : Exception
{
    public string? SourceFile { get; }
    public int? SourceLine { get; }

    public FailException(string message, string? sourceFile = null, int? sourceLine = null)
        : base(message)
    {
        SourceFile = sourceFile;
        SourceLine = sourceLine;
    }

    public string FormatDiagnostic()
    {
        if (SourceFile != null && SourceLine != null)
            return $"FATAL: {SourceFile}({SourceLine}): {Message}";
        if (SourceFile != null)
            return $"FATAL: {SourceFile}: {Message}";
        return $"FATAL: {Message}";
    }
}
