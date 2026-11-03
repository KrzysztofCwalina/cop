using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class ExecutionStatisticsTests
{
    [Test]
    public async Task RepeatedConcurrentInputReportsCountEachFileOnce()
    {
        using var statistics = new ExecutionStatistics();
        var first = Path.Combine(Path.GetTempPath(), "first.input");
        var second = Path.Combine(Path.GetTempPath(), "second.input");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            ExecutionStatistics.RecordInputFile(first);
            ExecutionStatistics.RecordInputFile(second);
        })));
        statistics.CommandCompleted();

        Assert.That(statistics.Snapshot().InputFiles, Is.EqualTo(2));
        Assert.That(statistics.Snapshot().CommandsExecuted, Is.EqualTo(1));
    }

    [Test]
    public void NestedScopesRestoreTheirParentAndDoNotLeakFiles()
    {
        using var outer = new ExecutionStatistics();
        ExecutionStatistics.RecordInputFile(Path.Combine(Path.GetTempPath(), "outer.input"));
        using (var inner = new ExecutionStatistics())
        {
            ExecutionStatistics.RecordInputFile(Path.Combine(Path.GetTempPath(), "inner.input"));
            inner.CommandCompleted();
            Assert.That(inner.Snapshot().InputFiles, Is.EqualTo(1));
            Assert.That(inner.Snapshot().CommandsExecuted, Is.EqualTo(1));
        }
        ExecutionStatistics.RecordInputFile(Path.Combine(Path.GetTempPath(), "after.input"));
        Assert.That(outer.Snapshot().InputFiles, Is.EqualTo(2));
        Assert.That(outer.Snapshot().CommandsExecuted, Is.EqualTo(0));
    }

    [Test]
    public async Task IndependentExecutionsKeepSeparateCounters()
    {
        async Task<ExecutionSummary> Run(int files)
        {
            using var statistics = new ExecutionStatistics();
            await Task.Yield();
            for (int i = 0; i < files; i++)
                ExecutionStatistics.RecordInputFile(Path.Combine(Path.GetTempPath(), $"{i}.input"));
            statistics.CommandCompleted();
            return statistics.Snapshot();
        }
        var summaries = await Task.WhenAll(Run(1), Run(3));
        Assert.That(summaries.Select(summary => summary.InputFiles), Is.EqualTo(new[] { 1, 3 }));
        Assert.That(summaries.Select(summary => summary.CommandsExecuted), Is.EqualTo(new[] { 1, 1 }));
    }
}
