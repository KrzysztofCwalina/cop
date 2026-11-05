// Reports a parsing failure with its source location and optional context.

using System.Text;

namespace Cop.Lang;

/// <summary>
/// Reports a parsing failure with its source location and optional context.
/// </summary>
public class ParseException : Exception
{
    public string FilePath { get; }
    public int Line { get; }
    public int? Column { get; }
    public string? SourceLine { get; }

    public ParseException(string message, string filePath, int line, int? column = null, string? sourceLine = null)
        : base($"{filePath}({line}): {message}")
    {
        FilePath = filePath;
        Line = line;
        Column = column;
        SourceLine = sourceLine;
    }

    /// <summary>
    /// Converts this ParseException into a structured CopDiagnostic.
    /// </summary>
    public CopDiagnostic ToDiagnostic() => new(
        CopDiagnosticSeverity.Error,
        Message.Contains("): ") ? Message[(Message.IndexOf("): ") + 3)..] : Message,
        FilePath,
        Line,
        Column,
        null,
        SourceLine);

    /// <summary>
    /// Extracts a specific line (1-based) from source text.
    /// </summary>
    public static string? GetSourceLine(string source, int line)
    {
        if (string.IsNullOrEmpty(source) || line < 1)
        {
            return null;
        }

        int currentLine = 1;
        int start = 0;
        while (currentLine < line && start < source.Length)
        {
            if (source[start] == '\n')
            {
                currentLine++;
            }

            start++;
        }
        if (currentLine != line)
        {
            return null;
        }

        int end = source.IndexOf('\n', start);
        if (end < 0)
        {
            end = source.Length;
        }

        string result = source[start..end].TrimEnd('\r');
        return result;
    }
}
