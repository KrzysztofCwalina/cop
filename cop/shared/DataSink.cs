namespace Cop.Core;

/// <summary>
/// Abstract base class for data sinks — output targets in the
/// Provider => Transform => Sink pipeline.
/// Providers register sinks via <see cref="DataProvider.GetSinks"/>.
/// </summary>
public abstract class DataSink
{
    /// <summary>
    /// Sink name (e.g., "WriteLine", "Write", "Send").
    /// Combined with provider namespace for qualified reference (e.g., "console.WriteLine").
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Writes the transformed result to the output target.
    /// </summary>
    /// <param name="originalItem">The original item from the source collection (may carry context, e.g., HTTP request socket).</param>
    /// <param name="result">The transformed result to output.</param>
    public abstract Task WriteAsync(object? originalItem, object result);

    /// <summary>
    /// Creates a parameterized instance of this sink (e.g., file.Write('path')).
    /// Default returns itself (for sinks that don't take parameters).
    /// </summary>
    public virtual DataSink WithArgs(List<object> args) => this;

    /// <summary>
    /// Called once when the streaming pipeline completes or is cancelled.
    /// Override to flush buffers, close connections, etc.
    /// </summary>
    public virtual Task CompleteAsync() => Task.CompletedTask;
}

/// <summary>
/// Built-in sink that writes to stdout (default when no sink is specified).
/// Registered under namespace "console" as "WriteLine".
/// </summary>
public class ConsoleWriteLineSink : DataSink
{
    public static ConsoleWriteLineSink Instance { get; } = new();

    public override string Name => "WriteLine";

    public override Task WriteAsync(object? originalItem, object result)
    {
        Console.WriteLine(result.ToString());
        return Task.CompletedTask;
    }
}

/// <summary>
/// Built-in sink that appends to a file.
/// Registered under namespace "file" as "Write".
/// </summary>
public class FileWriteSink : DataSink
{
    private string? _path;

    public override string Name => "Write";

    public override DataSink WithArgs(List<object> args)
    {
        if (args.Count < 1)
            throw new InvalidOperationException("file.Write requires a path argument.");
        return new FileWriteSink { _path = args[0]?.ToString() };
    }

    public override Task WriteAsync(object? originalItem, object result)
    {
        if (_path is null)
            throw new InvalidOperationException("file.Write: no path specified. Use file.Write('path').");
        File.AppendAllText(_path, result.ToString() + Environment.NewLine);
        return Task.CompletedTask;
    }
}
