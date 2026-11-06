// Appends pipeline results to a configured file while skipping error values.

namespace Cop.Core;

/// <summary>Built-in append-only file.Write sink.</summary>
public class FileWriteSink : SinkProvider
{
    private string? _path;

    /// <summary>The sink name registered in the file namespace.</summary>
    public override string Name => "Write";

    /// <summary>
    /// Creates a file-output sink using the first argument as its path.
    /// </summary>
    public override SinkProvider WithArgs(List<object> args)
    {
        if (args.Count < 1)
        {
            throw new InvalidOperationException("file.Write requires a path argument.");
        }

        return new FileWriteSink { _path = args[0]?.ToString() };
    }

    /// <summary>
    /// Appends a result line to the configured file, skipping error values.
    /// </summary>
    public override Task WriteAsync(object? originalItem, object result)
    {
        if (_path is null)
        {
            throw new InvalidOperationException("file.Write: no path specified. Use file.Write('path').");
        }
        // Skip error values — don't write them to files
        if (result is Cop.Lang.ErrorValue)
        {
            return Task.CompletedTask;
        }

        File.AppendAllText(_path, result.ToString() + Environment.NewLine);
        return Task.CompletedTask;
    }
}
