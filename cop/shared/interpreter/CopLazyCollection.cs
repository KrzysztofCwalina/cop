// Lazy collection — items are produced on demand.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Lazy collection — items are produced on demand.
/// Uses a factory function with memoization — the factory runs at most once,
/// and subsequent calls return the cached result.
/// </summary>
public sealed class CopLazyCollection : CopValue
{
    internal QuerySource? Source { get; set; }
    internal Cop.Core.FilterExpression? AppliedFilter { get; init; }
    private Func<IEnumerable<CopValue>>? _factory;
    private readonly List<CopValue> _cached = [];
    private readonly System.Threading.Lock _gate = new();
    private IEnumerator<CopValue>? _iterator;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _failure;
    private bool _complete;

    public CopLazyCollection(Func<IEnumerable<CopValue>> factory) => _factory = factory;

    public IEnumerable<CopValue> Enumerate() =>
        System.Threading.Volatile.Read(ref _complete) ? EnumerateCached() : EnumerateAndCache();

    private IEnumerable<CopValue> EnumerateCached()
    {
        foreach (CopValue value in _cached)
        {
            yield return value;
        }
    }

    private IEnumerable<CopValue> EnumerateAndCache()
    {
        for (int index = 0; ; index++)
        {
            CopValue value;
            lock (_gate)
            {
                if (index < _cached.Count)
                {
                    value = _cached[index];
                }
                else
                {
                    _failure?.Throw();
                    if (_complete)
                    {
                        yield break;
                    }

                    try
                    {
                        if (_iterator is null)
                        {
                            _iterator = _factory!().GetEnumerator();
                            _factory = null;
                        }
                        if (!_iterator.MoveNext())
                        {
                            _iterator.Dispose();
                            _iterator = null;
                            System.Threading.Volatile.Write(ref _complete, true);
                            yield break;
                        }
                        value = _iterator.Current;
                        _cached.Add(value);
                    }
                    catch (Exception ex)
                    {
                        _failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                        _iterator?.Dispose();
                        _iterator = null;
                        _factory = null;
                        throw;
                    }
                }
            }
            yield return value;
        }
    }

    public override string Display() => "[...]";
    public override string ToString() => Display();
}
