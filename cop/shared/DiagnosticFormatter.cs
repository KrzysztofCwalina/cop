using System.Text;

namespace Cop.Lang;

/// <summary>
/// Renders CopDiagnostic instances in a rich, human-friendly format
/// with source context, underline caret, and suggestions.
/// </summary>
public static class DiagnosticFormatter
{
    /// <summary>
    /// Formats a single diagnostic with source context.
    /// Example output:
    ///   src/checks.cop(12): error: Unknown identifier 'Fles'
    ///     12 | let files = Fles : name sw 'test'
    ///                      ~~~~
    ///     Did you mean 'Files'?
    /// </summary>
    public static string Format(CopDiagnostic diagnostic)
    {
        var sb = new StringBuilder();

        // Header line: location: severity: message
        sb.Append(diagnostic.Location);
        sb.Append(": ");
        sb.Append(SeverityLabel(diagnostic.Severity));
        sb.Append(": ");
        sb.AppendLine(diagnostic.Message);

        // Source line with line number gutter
        if (diagnostic.SourceLine is not null)
        {
            var lineNum = diagnostic.Line.ToString();
            var gutter = $"  {lineNum} | ";
            sb.Append(gutter);
            sb.AppendLine(diagnostic.SourceLine);

            // Caret underline
            if (diagnostic.Column is int col && col > 0)
            {
                int underlineLen = diagnostic.Length ?? 1;
                sb.Append(new string(' ', gutter.Length + col - 1));
                sb.AppendLine(new string('~', Math.Max(1, underlineLen)));
            }
        }

        // Suggestion
        if (diagnostic.Suggestion is not null)
        {
            sb.Append("  Did you mean '");
            sb.Append(diagnostic.Suggestion);
            sb.AppendLine("'?");
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Formats multiple diagnostics, separated by blank lines.
    /// </summary>
    public static string FormatAll(IEnumerable<CopDiagnostic> diagnostics)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var d in diagnostics)
        {
            if (!first) sb.AppendLine();
            sb.AppendLine(Format(d));
            first = false;
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Writes a diagnostic to stderr using rich formatting.
    /// </summary>
    public static void WriteToStdErr(CopDiagnostic diagnostic)
    {
        Console.Error.WriteLine(Format(diagnostic));
    }

    /// <summary>
    /// Writes all diagnostics to stderr.
    /// </summary>
    public static void WriteAllToStdErr(IEnumerable<CopDiagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
        {
            Console.Error.WriteLine(Format(d));
            Console.Error.WriteLine();
        }
    }

    private static string SeverityLabel(CopDiagnosticSeverity severity) => severity switch
    {
        CopDiagnosticSeverity.Error => "error",
        CopDiagnosticSeverity.Warning => "warning",
        CopDiagnosticSeverity.Info => "info",
        _ => "error"
    };
}
