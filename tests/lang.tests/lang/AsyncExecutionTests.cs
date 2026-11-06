using Cop.Lang;
using Cop.Lang.Interpreter;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public sealed class AsyncExecutionTests
{
    [Test]
    public async Task NestedCallsAwaitAnIncompleteForeignFunction()
    {
        TaskCompletionSource release = NewGate();
        LanguageBridge bridge = new();
        bridge.RegisterFunction("delayed", async (_, _, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return new CopInt(21);
        });
        await bridge.LoadSourceAsync("""
            function twice(value : int) : int = value * 2
            command MAIN = twice(delayed())
            """);

        Task<CopValue> execution = bridge.Evaluator.RunCommandAsync("MAIN").AsTask();
        Assert.That(execution.IsCompleted, Is.False);
        release.SetResult();

        CopValue result = await execution;
        Assert.That(result, Is.TypeOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(42));
    }

    [Test]
    public async Task PredicatesAndTransformsAwaitInSourceOrder()
    {
        TaskCompletionSource releasePredicates = NewGate();
        TaskCompletionSource releaseTransform = NewGate();
        TaskCompletionSource enteredTransform = NewGate();
        List<string> calls = [];
        LanguageBridge bridge = new();
        bridge.RegisterValue("items", new CopList([new CopInt(1), new CopInt(2), new CopInt(3)]));
        bridge.RegisterFunction("accepts", async (arguments, _, cancellationToken) =>
        {
            int value = ((CopInt)arguments[0]).Value;
            calls.Add($"predicate:{value}");
            await releasePredicates.Task.WaitAsync(cancellationToken);
            return CopBool.Of(value % 2 == 0);
        });
        bridge.RegisterFunction("project", async (arguments, _, cancellationToken) =>
        {
            int value = ((CopInt)arguments[0]).Value;
            calls.Add($"transform:{value}");
            enteredTransform.SetResult();
            await releaseTransform.Task.WaitAsync(cancellationToken);
            return new CopInt(value * 10);
        });
        await bridge.LoadSourceAsync(
            "command MAIN = items.Where(accepts(item)).Select(project(item))");

        Task<CopValue> execution = bridge.Evaluator.RunCommandAsync("MAIN").AsTask();
        Assert.That(execution.IsCompleted, Is.False);
        Assert.That(calls, Is.EqualTo(new[] { "predicate:1" }));
        releasePredicates.SetResult();
        await enteredTransform.Task;
        Assert.That(execution.IsCompleted, Is.False);
        Assert.That(calls, Is.EqualTo(new[]
            { "predicate:1", "predicate:2", "predicate:3", "transform:2" }));
        releaseTransform.SetResult();

        CopValue result = await execution;
        Assert.That(result, Is.TypeOf<CopList>());
        Assert.That(((CopList)result).Items.Select(value => ((CopInt)value).Value),
            Is.EqualTo(new[] { 20 }));
    }

    [Test]
    public async Task NestedCallsPropagateAnAsynchronousFailure()
    {
        TaskCompletionSource release = NewGate();
        LanguageBridge bridge = new();
        bridge.RegisterFunction("delayed", async (_, _, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            throw new CopEvaluationException("delayed failure");
        });
        await bridge.LoadSourceAsync("""
            function nested() = delayed()
            command MAIN = nested()
            """);

        Task<CopValue> execution = bridge.Evaluator.RunCommandAsync("MAIN").AsTask();
        Assert.That(execution.IsCompleted, Is.False);
        release.SetResult();

        CopEvaluationException? exception = Assert.ThrowsAsync<CopEvaluationException>(
            async () => await execution);
        Assert.That(exception!.RawMessage, Is.EqualTo("delayed failure"));
    }

    [Test]
    public async Task CancellationIsNotConvertedIntoAPredicateFailureOrEmptyResult()
    {
        TaskCompletionSource entered = NewGate();
        TaskCompletionSource release = NewGate();
        LanguageBridge bridge = new();
        bridge.RegisterValue("items", new CopList([new CopInt(1)]));
        bridge.RegisterFunction("pending", async (_, _, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return CopBool.True;
        });
        await bridge.LoadSourceAsync("command MAIN = items:pending");
        using CancellationTokenSource cancellation = new();
        Task<CopValue> execution = bridge.Evaluator.RunCommandAsync(
            "MAIN", cancellation.Token).AsTask();
        Task<List<CopValue>> results = CollectResultAsync(execution, cancellation.Token);
        await entered.Task;
        Assert.That(results.IsCompleted, Is.False);
        cancellation.Cancel();

        OperationCanceledException? exception = Assert.CatchAsync<OperationCanceledException>(
            async () => await results);
        Assert.That(exception!.CancellationToken, Is.EqualTo(cancellation.Token));
    }

    [Test]
    public async Task ConcurrentThunkReadersShareOneIncompleteComputation()
    {
        TaskCompletionSource release = NewGate();
        int calls = 0;
        CopThunk thunk = new(async cancellationToken =>
        {
            calls++;
            await release.Task.WaitAsync(cancellationToken);
            return new CopInt(7);
        });

        Task<CopValue> first = thunk.ForceAsync().AsTask();
        Task<CopValue> second = thunk.ForceAsync().AsTask();
        Assert.That(first.IsCompleted || second.IsCompleted, Is.False);
        Assert.That(calls, Is.EqualTo(1));
        release.SetResult();

        Assert.That(await second, Is.SameAs(await first));
        Assert.That(((CopInt)await thunk.ForceAsync()).Value, Is.EqualTo(7));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public async Task AsyncDataObjectFieldsRemainDemandDrivenAndMemoized()
    {
        TaskCompletionSource release = NewGate();
        int calls = 0;
        DataObject record = new("Record", new() { ["Name"] = "structural" });
        record.WithAsyncFieldResolver(async (_, cancellationToken) =>
        {
            calls++;
            await release.Task.WaitAsync(cancellationToken);
            return 9;
        });

        Assert.That(await record.GetFieldAsync("Name"), Is.EqualTo("structural"));
        Assert.That(calls, Is.Zero);
        Task<object?> first = record.GetFieldAsync("BoundValue").AsTask();
        Task<object?> second = record.GetFieldAsync("BoundValue").AsTask();
        Assert.That(first.IsCompleted || second.IsCompleted, Is.False);
        Assert.That(calls, Is.EqualTo(1));
        release.SetResult();

        Assert.That(await first, Is.EqualTo(9));
        Assert.That(await second, Is.EqualTo(9));
        Assert.That(record.GetField("BoundValue"), Is.EqualTo(9));
        Assert.That(calls, Is.EqualTo(1));
    }

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<List<CopValue>> CollectResultAsync(Task<CopValue> execution,
        CancellationToken cancellationToken)
    {
        CopValue value = await execution;
        if (value is CopLazyCollection lazy)
        {
            return await lazy.EnumerateAsync(cancellationToken).ToListAsync(cancellationToken);
        }
        return value is CopList list ? list.Items.ToList() : [value];
    }
}
