using System.Collections;

namespace Cop.Core;

/// <summary>
/// A SinkProvider adapter that enqueues (appends) items to a target list.
/// Thread-safe: uses locking for concurrent enqueue operations.
/// Used as fallback when pipe target resolves to a let-binding or
/// collection declaration rather than a registered provider SinkProvider.
/// </summary>
public class ListAppendSink : SinkProvider
{
    private readonly IList _target;
    private readonly object _lock = new();

    public ListAppendSink(IList target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public override string Name => "ListAppend";

    public override Task WriteAsync(object? originalItem, object result)
    {
        lock (_lock)
        {
            _target.Add(result);
        }
        return Task.CompletedTask;
    }
}
