// A structured exception for evaluation errors with source location.

using Cop.Core;

namespace Cop.Lang.Interpreter;

using System.Text;
using Cop.Lang.Ast;

/// <summary>
/// A structured exception for evaluation errors with source location.
/// </summary>
public sealed class CopEvaluationException : Exception
{
    /// <summary>
    /// The source line where evaluation failed, or zero when unavailable.
    /// </summary>
    public int Line { get; }
    /// <summary>
    /// The source file where evaluation failed, when known.
    /// </summary>
    public string? FilePath { get; }

    /// <summary>The message without the "line N:" / "file(line):" location prefix.</summary>
    public string RawMessage { get; }

    /// <summary>Reports an evaluation failure without source attribution.</summary>
    public CopEvaluationException() : this("An error occurred while evaluating Cop code.") { }

    /// <summary>Reports an evaluation failure without source attribution.</summary>
    public CopEvaluationException(string message) : this(message, 0, null) { }

    /// <summary>Reports an evaluation failure while retaining its underlying cause.</summary>
    public CopEvaluationException(string message, Exception innerException)
        : base($"line 0: {message}", innerException)
    {
        RawMessage = message;
    }

    /// <summary>Reports an evaluation failure at the supplied source location.</summary>
    public CopEvaluationException(string message, int line = 0, string? filePath = null)
        : base(filePath is not null ? $"{filePath}({line}): {message}" : $"line {line}: {message}")
    {
        Line = line;
        FilePath = filePath;
        RawMessage = message;
    }
}
