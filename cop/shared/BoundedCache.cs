namespace Cop.Lang;

/// <summary>
/// A dictionary with a maximum capacity and LRU (Least Recently Used) eviction.
/// When the cache is full, the least recently accessed entry is evicted to make room.
/// </summary>
internal sealed class BoundedCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
    private readonly LinkedList<(TKey Key, TValue Value)> _order;

    public BoundedCache(int capacity)
    {
        _capacity = capacity;
        _map = new(capacity);
        _order = new();
    }

    public int Count => _map.Count;

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            // Move to front (most recently used)
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            _map.Remove(key);
        }
        else if (_map.Count >= _capacity)
        {
            // Evict LRU (last in list)
            var lru = _order.Last!;
            _order.RemoveLast();
            _map.Remove(lru.Value.Key);
        }

        var node = new LinkedListNode<(TKey, TValue)>((key, value));
        _order.AddFirst(node);
        _map[key] = node;
    }
}
