// Writes pipeline results to stdout and operational errors to stderr.

namespace Cop.Core;

/// <summary>
/// Built-in console.WriteLine sink used when a pipeline has no explicit destination.
/// </summary>
public class ConsoleWriteLineSink : SinkProvider
{
    /// <summary>The shared stateless console-output sink.</summary>
    public static ConsoleWriteLineSink Instance { get; } = new();

    /// <summary>The sink name registered in the console namespace.</summary>
    public override string Name => "WriteLine";

    /// <summary>
    /// Writes normal results to stdout and operational errors to stderr.
    /// </summary>
    public override Task WriteAsync(object? originalItem, object result)
    {
        if (result is Cop.Lang.ErrorValue err)
        {
            string message = err.GetField("Message")?.ToString() ?? "error";
            Console.Error.WriteLine($"ERROR: {message}");
        }
        else
        {
            Console.WriteLine(result.ToString());
        }
        return Task.CompletedTask;
    }
}
