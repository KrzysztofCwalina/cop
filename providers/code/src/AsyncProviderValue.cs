// Memoizes demanded provider work without blocking callers or retaining canceled work.

namespace Cop.Providers;

/// <summary>Shares a lazily started asynchronous provider computation.</summary>
public sealed class AsyncProviderValue<T>(Func<CancellationToken, ValueTask<T>> create)
{
    private readonly object _gate = new();
    private Task<T>? _task;

    /// <summary>Awaits shared work while preserving this caller's cancellation.</summary>
    public async ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<T> task;
            lock (_gate)
            {
                if (_task?.IsCanceled == true)
                {
                    _task = null;
                }

                task = _task ??= create(cancellationToken).AsTask();
            }

            try
            {
                return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && task.IsCanceled)
            {
                // Another caller owned the canceled attempt. This caller may retry.
                lock (_gate)
                {
                    if (ReferenceEquals(_task, task))
                    {
                        _task = null;
                    }
                }
            }
        }
    }
}
