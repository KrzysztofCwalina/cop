using System.Collections;
using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class ListAppendSinkTests
{
    [Test]
    public void WriteAsync_AppendsToList()
    {
        var target = new List<object>();
        var sink = new ListAppendSink(target);

        sink.WriteAsync(null, "hello").GetAwaiter().GetResult();
        sink.WriteAsync(null, "world").GetAwaiter().GetResult();

        Assert.That(target, Has.Count.EqualTo(2));
        Assert.That(target[0], Is.EqualTo("hello"));
        Assert.That(target[1], Is.EqualTo("world"));
    }

    [Test]
    public void WriteAsync_ThreadSafe_ConcurrentAppends()
    {
        var target = new List<object>();
        var sink = new ListAppendSink(target);

        var tasks = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() => sink.WriteAsync(null, i)))
            .ToArray();

        Task.WaitAll(tasks);

        Assert.That(target, Has.Count.EqualTo(100));
        // All items should be present (order may vary due to concurrency)
        var sorted = target.Cast<int>().OrderBy(x => x).ToList();
        Assert.That(sorted, Is.EqualTo(Enumerable.Range(0, 100).ToList()));
    }

    [Test]
    public void Name_ReturnsListAppend()
    {
        var sink = new ListAppendSink(new List<object>());
        Assert.That(sink.Name, Is.EqualTo("ListAppend"));
    }

    [Test]
    public void Constructor_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ListAppendSink(null!));
    }
}
