using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class BoundedCacheTests
{
    [Test]
    public void BasicGetSet()
    {
        var cache = new BoundedCache<string, int>(capacity: 4);
        cache.Set("a", 1);
        cache.Set("b", 2);

        Assert.That(cache.TryGetValue("a", out var val), Is.True);
        Assert.That(val, Is.EqualTo(1));
        Assert.That(cache.Count, Is.EqualTo(2));
    }

    [Test]
    public void MissReturnsFalse()
    {
        var cache = new BoundedCache<string, int>(capacity: 4);
        Assert.That(cache.TryGetValue("x", out _), Is.False);
    }

    [Test]
    public void EvictsLeastRecentlyUsed()
    {
        var cache = new BoundedCache<string, int>(capacity: 3);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("c", 3);
        // Cache full: [c, b, a] (MRU → LRU)

        cache.Set("d", 4);
        // "a" evicted: [d, c, b]

        Assert.That(cache.TryGetValue("a", out _), Is.False);
        Assert.That(cache.TryGetValue("b", out var val), Is.True);
        Assert.That(val, Is.EqualTo(2));
        Assert.That(cache.Count, Is.EqualTo(3));
    }

    [Test]
    public void AccessPromotesToMRU()
    {
        var cache = new BoundedCache<string, int>(capacity: 3);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("c", 3);
        // Order: [c, b, a]

        // Access "a" — promotes to MRU
        cache.TryGetValue("a", out _);
        // Order: [a, c, b]

        cache.Set("d", 4);
        // "b" evicted (LRU): [d, a, c]

        Assert.That(cache.TryGetValue("b", out _), Is.False);
        Assert.That(cache.TryGetValue("a", out _), Is.True);
        Assert.That(cache.TryGetValue("c", out _), Is.True);
        Assert.That(cache.TryGetValue("d", out _), Is.True);
    }

    [Test]
    public void OverwriteExistingKey()
    {
        var cache = new BoundedCache<string, int>(capacity: 3);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("a", 10);

        Assert.That(cache.TryGetValue("a", out var val), Is.True);
        Assert.That(val, Is.EqualTo(10));
        Assert.That(cache.Count, Is.EqualTo(2));
    }

    [Test]
    public void CapacityOne()
    {
        var cache = new BoundedCache<string, int>(capacity: 1);
        cache.Set("a", 1);
        cache.Set("b", 2);

        Assert.That(cache.TryGetValue("a", out _), Is.False);
        Assert.That(cache.TryGetValue("b", out var val), Is.True);
        Assert.That(val, Is.EqualTo(2));
        Assert.That(cache.Count, Is.EqualTo(1));
    }

    [Test]
    public void ManyEvictions()
    {
        var cache = new BoundedCache<int, int>(capacity: 5);
        for (int i = 0; i < 100; i++)
            cache.Set(i, i * 10);

        Assert.That(cache.Count, Is.EqualTo(5));
        // Only last 5 should remain
        for (int i = 95; i < 100; i++)
        {
            Assert.That(cache.TryGetValue(i, out var val), Is.True);
            Assert.That(val, Is.EqualTo(i * 10));
        }
        Assert.That(cache.TryGetValue(94, out _), Is.False);
    }
}
