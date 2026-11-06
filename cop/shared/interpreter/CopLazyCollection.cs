// Replays a lazily advanced asynchronous collection, including its original failures.

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A demand-driven collection with one shared asynchronous iterator and a replay cache.
/// Concurrent consumers await the same advancement instead of duplicating provider work.
/// </summary>
public sealed class CopLazyCollection : CopValue
{
    internal QuerySource? Source { get; set; }
    internal Cop.Core.FilterExpression? AppliedFilter { get; init; }
    private Func<CancellationToken, IAsyncEnumerable<CopValue>>? _factory;
    private readonly List<CopValue> _cached = [];
    private readonly System.Threading.Lock _gate = new();
    private IAsyncEnumerator<CopValue>? _iterator;
    private ExceptionDispatchInfo? _failure;
    private Lazy<Task>? _advance;
    private bool _complete;

    public CopLazyCollection(Func<CancellationToken, IAsyncEnumerable<CopValue>> factory) =>
        _factory = factory;

    /// <summary>Defers an in-memory synchronous sequence without background scheduling.</summary>
    public CopLazyCollection(Func<IEnumerable<CopValue>> factory)
        : this(token => CollectionValues.FromEnumerable(factory(), token)) { }

    /// <summary>Defers synchronous data generation while retaining caller cancellation.</summary>
    public CopLazyCollection(Func<CancellationToken, IEnumerable<CopValue>> factory)
        : this(token => CollectionValues.FromEnumerable(factory(token), token)) { }

    /// <summary>Defers an asynchronous operation that produces buffered values.</summary>
    public CopLazyCollection(
        Func<CancellationToken, ValueTask<IEnumerable<CopValue>>> factory)
        : this(token => AwaitBufferedAsync(factory, token)) { }

    /// <summary>Defers asynchronous preparation of another asynchronous sequence.</summary>
    public CopLazyCollection(
        Func<CancellationToken, ValueTask<IAsyncEnumerable<CopValue>>> factory)
        : this(token => AwaitSequenceAsync(factory, token)) { }

    private static async IAsyncEnumerable<CopValue> AwaitBufferedAsync(
        Func<CancellationToken, ValueTask<IEnumerable<CopValue>>> factory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (CopValue value in await factory(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }

    private static async IAsyncEnumerable<CopValue> AwaitSequenceAsync(
        Func<CancellationToken, ValueTask<IAsyncEnumerable<CopValue>>> factory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerable<CopValue> source =
            await factory(cancellationToken).ConfigureAwait(false);
        await foreach (CopValue value in source.WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return value;
        }
    }

    /// <summary>Replays cached values and awaits new elements only as requested.</summary>
    public async IAsyncEnumerable<CopValue> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int index = 0; ; index++)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopValue? value = null;
                Lazy<Task>? advance = null;
                lock (_gate)
                {
                    if (index < _cached.Count)
                    {
                        value = _cached[index];
                    }
                    else
                    {
                        _failure?.Throw();
                        if (!_complete)
                        {
                            advance = _advance ??= new(() => AdvanceAsync(cancellationToken));
                        }
                    }
                }

                if (value is not null)
                {
                    yield return value;
                    break;
                }
                if (advance is null)
                {
                    yield break;
                }

                try
                {
                    await advance.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (advance.IsValueCreated && advance.Value.IsCompleted)
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_advance, advance))
                            {
                                _advance = null;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>Completes the replay cache without copying its items into another buffer.</summary>
    internal async ValueTask<IReadOnlyList<CopValue>> GetItemsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_complete)
            {
                return _cached;
            }
        }

        await foreach (CopValue _ in EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
        }
        return _cached;
    }

    private async Task AdvanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_iterator is null)
            {
                _iterator = _factory!(cancellationToken).GetAsyncEnumerator(cancellationToken);
                _factory = null;
            }
            if (!await _iterator.MoveNextAsync().ConfigureAwait(false))
            {
                await _iterator.DisposeAsync().ConfigureAwait(false);
                _iterator = null;
                lock (_gate)
                {
                    _complete = true;
                }
                return;
            }

            lock (_gate)
            {
                _cached.Add(_iterator.Current);
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
                _factory = null;
            }
            if (_iterator is not null)
            {
                await _iterator.DisposeAsync().ConfigureAwait(false);
                _iterator = null;
            }
            throw;
        }
    }

    /// <inheritdoc/>
    public override string Display() => "[...]";
    /// <inheritdoc/>
    public override string ToString() => Display();
}
