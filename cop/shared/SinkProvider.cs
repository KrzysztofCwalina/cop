namespace Cop.Core;

/// <summary>
/// Abstract base class for sink providers — output targets in streaming pipelines.
/// A sink accepts items produced by the pipeline and writes them to an output target.
/// Examples: HTTP response sender, console writer, file writer.
///
/// Provider packages contain subclasses of this. The engine discovers them at load time
/// and registers them as sinks accessible via sink('namespace') in cop scripts.
/// </summary>
public abstract class SinkProvider
{
    /// <summary>
    /// Sink name (e.g., "RESPONSES", "WriteLine", "Write").
    /// Combined with provider namespace for qualified reference (e.g., "http.RESPONSES").
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Writes the transformed result to the output target.
    /// </summary>
    /// <param name="originalItem">The original item from the source (carries context, e.g., HTTP request completion).</param>
    /// <param name="result">The transformed result to output.</param>
    public abstract Task WriteAsync(object? originalItem, object result);

    /// <summary>
    /// Creates a parameterized instance of this sink (e.g., file.Write('path')).
    /// Default returns itself (for sinks that don't take parameters).
    /// </summary>
    public virtual SinkProvider WithArgs(List<object> args) => this;

    /// <summary>
    /// Called once when the streaming pipeline completes or is cancelled.
    /// Override to flush buffers, close connections, etc.
    /// </summary>
    public virtual Task CompleteAsync() => Task.CompletedTask;

    /// <summary>
    /// Human-readable provider name, used in diagnostics.
    /// Default strips the "Sink" suffix from the class name.
    /// </summary>
    public override string ToString()
    {
        var name = GetType().Name;
        return name.EndsWith("Sink", StringComparison.Ordinal)
            ? name[..^"Sink".Length]
            : name;
    }
}
