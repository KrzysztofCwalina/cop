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
    public int Line { get; }
    public string? FilePath { get; }

    /// <summary>The message without the "line N:" / "file(line):" location prefix.</summary>
    public string RawMessage { get; }

    public CopEvaluationException(string message, int line = 0, string? filePath = null)
        : base(filePath is not null ? $"{filePath}({line}): {message}" : $"line {line}: {message}")
    {
        Line = line;
        FilePath = filePath;
        RawMessage = message;
    }
}
