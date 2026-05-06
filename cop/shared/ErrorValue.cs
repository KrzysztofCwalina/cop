namespace Cop.Lang;

/// <summary>
/// Represents an operational error value flowing through a pipeline.
/// ErrorValue is a DataObject so it participates in the normal data flow —
/// field access (.Message, .Source) works, and it flows through collections.
/// 
/// Errors are NOT code bugs (those use FAIL). Errors represent external failures
/// that code cannot prevent: network timeouts, I/O failures, malformed external input.
/// </summary>
public class ErrorValue : DataObject
{
    public ErrorValue(string? message, string? sourceFile = null, int? sourceLine = null)
        : base("CopError", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Message"] = message,
            ["SourceFile"] = sourceFile,
            ["SourceLine"] = sourceLine,
            ["Source"] = sourceFile != null ? $"{sourceFile}({sourceLine})" : null
        })
    {
    }

    /// <summary>
    /// Tests whether an object is an error value.
    /// This is the single authoritative check — used by isError predicate,
    /// pipeline propagation, and sink error handling.
    /// </summary>
    public static bool IsError(object? value) => value is ErrorValue;
}
