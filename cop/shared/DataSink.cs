namespace Cop.Core;

/// <summary>
/// Built-in sink that writes to stdout (default when no sink is specified).
/// Registered under namespace "console" as "WriteLine".
/// Errors are written to stderr.
/// </summary>
public class ConsoleWriteLineSink : SinkProvider
{
    public static ConsoleWriteLineSink Instance { get; } = new();

    public override string Name => "WriteLine";

    public override Task WriteAsync(object? originalItem, object result)
    {
        if (result is Cop.Lang.ErrorValue err)
        {
            var message = err.GetField("Message")?.ToString() ?? "error";
            Console.Error.WriteLine($"ERROR: {message}");
        }
        else
        {
            Console.WriteLine(result.ToString());
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Built-in sink that appends to a file.
/// Registered under namespace "file" as "Write".
/// </summary>
public class FileWriteSink : SinkProvider
{
    private string? _path;

    public override string Name => "Write";

    public override SinkProvider WithArgs(List<object> args)
    {
        if (args.Count < 1)
            throw new InvalidOperationException("file.Write requires a path argument.");
        return new FileWriteSink { _path = args[0]?.ToString() };
    }

    public override Task WriteAsync(object? originalItem, object result)
    {
        if (_path is null)
            throw new InvalidOperationException("file.Write: no path specified. Use file.Write('path').");
        // Skip error values — don't write them to files
        if (result is Cop.Lang.ErrorValue)
            return Task.CompletedTask;
        File.AppendAllText(_path, result.ToString() + Environment.NewLine);
        return Task.CompletedTask;
    }
}
