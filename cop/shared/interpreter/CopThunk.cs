// A deferred computation (thunk).

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// A deferred asynchronous computation. Successful ordinary computations are memoized;
/// templates that read an item are rendered independently for each item context.
/// Recursive forcing is detected per asynchronous call chain, not across unrelated callers.
/// </summary>
public sealed class CopThunk : CopValue
{
    private static readonly AsyncLocal<ForcingPath?> s_path = new();
    private readonly Func<CancellationToken, ValueTask<CopValue>> _compute;
    private readonly System.Threading.Lock _gate = new();
    private CopValue? _forced;
    private Lazy<Task<CopValue>>? _pending;

    public CopThunk(Func<CancellationToken, ValueTask<CopValue>> compute) => _compute = compute;

    /// <summary>Defers a genuinely synchronous computation without scheduling background work.</summary>
    public CopThunk(Func<CopValue> compute)
        : this(_ => ValueTask.FromResult(compute())) { }

    /// <summary>
    /// Force evaluation of this thunk. Returns the memoized result.
    /// Recursively forces if the result is itself a thunk.
    /// </summary>
    public ValueTask<CopValue> ForceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertNotRecursive();
        Lazy<Task<CopValue>> pending;
        lock (_gate)
        {
            if (_forced is not null)
            {
                return ValueTask.FromResult(_forced);
            }

            pending = _pending ??= new(() => ComputeAsync(cancellationToken));
        }

        return AwaitPendingAsync(pending, cancellationToken);
    }

    private async ValueTask<CopValue> AwaitPendingAsync(Lazy<Task<CopValue>> pending,
        CancellationToken cancellationToken)
    {
        try
        {
            return await pending.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (pending.IsValueCreated && pending.Value.IsCompleted)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_pending, pending))
                    {
                        _pending = null;
                    }
                }
            }
        }
    }

    private async Task<CopValue> ComputeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ForceCoreAsync(async token =>
                (await _compute(token).ConfigureAwait(false), true), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _pending = null;
            }
        }
    }

    internal ValueTask<CopValue> ForceTemplateAsync(
        Func<CancellationToken, ValueTask<(CopValue Value, bool Cache)>> render,
        CancellationToken cancellationToken = default) =>
        ForceCoreAsync(render, cancellationToken);

    private async ValueTask<CopValue> ForceCoreAsync(
        Func<CancellationToken, ValueTask<(CopValue Value, bool Cache)>> compute,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_forced is not null)
        {
            return _forced;
        }

        AssertNotRecursive();
        ForcingPath? previous = s_path.Value;
        s_path.Value = new(this, previous);
        try
        {
            (CopValue result, bool cache) = await compute(cancellationToken).ConfigureAwait(false);
            while (result is CopThunk nested)
            {
                result = await nested.ForceAsync(cancellationToken).ConfigureAwait(false);
            }

            if (cache)
            {
                _forced = result;
            }

            return result;
        }
        finally { s_path.Value = previous; }
    }

    private void AssertNotRecursive()
    {
        for (ForcingPath? path = s_path.Value; path is not null; path = path.Parent)
        {
            if (ReferenceEquals(path.Thunk, this))
            {
                throw new CopEvaluationException(
                    "Recursive thunk forcing detected (infinite loop)");
            }
        }
    }

    /// <summary>True if this thunk has already been forced to a concrete value.</summary>
    public bool IsForced => _forced is not null;
    internal CopValue? ForcedValue => _forced;
    internal Ast.Expression? ConstantExpression { get; init; }
    internal Environment? ConstantEnvironment { get; init; }
    internal Func<Environment, CancellationToken, ValueTask<CopValue>>?
        EvaluateInEnvironment { get; init; }

    /// <inheritdoc/>
    public override bool IsTruthy => GetForcedValue().IsTruthy;
    /// <inheritdoc/>
    public override string Display() => GetForcedValue().Display();
    /// <inheritdoc/>
    public override async ValueTask<string> DisplayAsync(
        CancellationToken cancellationToken = default) =>
        await (await ForceAsync(cancellationToken).ConfigureAwait(false))
            .DisplayAsync(cancellationToken).ConfigureAwait(false);
    /// <inheritdoc/>
    public override async ValueTask<bool> IsTruthyAsync(
        CancellationToken cancellationToken = default) =>
        (await ForceAsync(cancellationToken).ConfigureAwait(false)).IsTruthy;
    /// <inheritdoc/>
    public override string ToString() => GetForcedValue().ToString() ?? Display();

    private CopValue GetForcedValue() => _forced ?? throw new InvalidOperationException(
        "Deferred values require ForceAsync, DisplayAsync, or IsTruthyAsync.");

    private sealed record ForcingPath(CopThunk Thunk, ForcingPath? Parent);
}
