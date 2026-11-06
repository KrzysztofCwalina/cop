using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using NUnit.Framework;
using IdentifierExpr = Cop.Lang.Ast.IdentifierExpr;

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

    [Test]
    public async Task RepeatedLazyMemberProjectionDoesNotCopyTheReplayBuffer()
    {
        const int count = 16_384;
        CopValue[] rows = Enumerable.Range(0, count)
            .Select(index => (CopValue)new CopObject(
                new Dictionary<string, CopValue> { ["Value"] = new CopInt(index) }))
            .ToArray();
        LanguageBridge bridge = new();
        bridge.RegisterValue("rows", new CopLazyCollection(() => rows));
        await bridge.LoadSourceAsync("command MAIN = rows.Value");
        await bridge.Evaluator.RunCommandAsync("MAIN");

        long before = GC.GetAllocatedBytesForCurrentThread();
        ValueTask<CopValue> pending = bridge.Evaluator.RunCommandAsync("MAIN");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(pending.IsCompletedSuccessfully, Is.True);
        CopList result = (CopList)await pending;
        Assert.That(result.Items.Select(value => ((CopInt)value).Value),
            Is.EqualTo(Enumerable.Range(0, count)));
        Assert.That(allocated, Is.LessThan(2L * count * IntPtr.Size),
            "Projection needs its result buffer, not another copy of the input replay buffer.");
    }

    [Test]
    public async Task RepeatedLazyMemberProjectionReadsCurrentFieldValues()
    {
        DataObject record = new("Record", new() { ["Name"] = "before" });
        LanguageBridge bridge = new();
        bridge.RegisterValue("rows", new CopLazyCollection(
            () => new CopValue[] { new CopDynamicObject(record, DataObjectAdapter.Instance) }));
        await bridge.LoadSourceAsync("command MAIN = rows.Name");

        Assert.That(await (await bridge.Evaluator.RunCommandAsync("MAIN")).DisplayAsync(),
            Is.EqualTo("[before]"));
        record.Set("Name", "after");
        Assert.That(await (await bridge.Evaluator.RunCommandAsync("MAIN")).DisplayAsync(),
            Is.EqualTo("[after]"));
    }

    [Test]
    public async Task ConcurrentLazyMemberProjectionsShareOneIncompleteInput()
    {
        TaskCompletionSource release = NewGate();
        int calls = 0;
        LanguageBridge bridge = new();
        bridge.RegisterValue("rows", new CopLazyCollection(
            async cancellationToken =>
            {
                calls++;
                await release.Task.WaitAsync(cancellationToken);
                return (IEnumerable<CopValue>)new CopValue[]
                {
                    new CopObject(new Dictionary<string, CopValue> { ["Value"] = new CopInt(7) }),
                    new CopObject(new Dictionary<string, CopValue> { ["Value"] = new CopInt(9) })
                };
            }));
        await bridge.LoadSourceAsync("command MAIN = rows.Value");

        Task<CopValue> first = bridge.Evaluator.RunCommandAsync("MAIN").AsTask();
        Task<CopValue> second = bridge.Evaluator.RunCommandAsync("MAIN").AsTask();
        Assert.That(first.IsCompleted || second.IsCompleted, Is.False);
        Assert.That(calls, Is.EqualTo(1));
        release.SetResult();

        Assert.That(await (await first).DisplayAsync(), Is.EqualTo("[7, 9]"));
        Assert.That(await (await second).DisplayAsync(), Is.EqualTo("[7, 9]"));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public async Task CompletedLazyMemberProjectionStillPreservesCancellation()
    {
        LanguageBridge bridge = new();
        bridge.RegisterValue("rows", new CopLazyCollection(() => new CopValue[]
        {
            new CopObject(new Dictionary<string, CopValue> { ["Value"] = new CopInt(7) })
        }));
        await bridge.LoadSourceAsync("command MAIN = rows.Value");
        Assert.That(await (await bridge.Evaluator.RunCommandAsync("MAIN")).DisplayAsync(),
            Is.EqualTo("[7]"));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException? error = Assert.CatchAsync<OperationCanceledException>(
            async () => await bridge.Evaluator.RunCommandAsync("MAIN", cancellation.Token));
        Assert.That(error!.CancellationToken, Is.EqualTo(cancellation.Token));
    }

    [Test]
    public async Task ScalarMemberReadsDoNotAllocateCollectionResolutionClosures()
    {
        const int count = 1_000;
        Evaluator evaluator = new();
        CopInt expected = new(7);
        evaluator.GlobalEnvironment.Define("record",
            new CopObject(new Dictionary<string, CopValue> { ["Value"] = expected }));
        MemberExpr expression = new(new IdentifierExpr("record"), "Value");
        CopValue[] results = new CopValue[count];
        await evaluator.EvalAsync(expression, evaluator.GlobalEnvironment);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < count; index++)
        {
            results[index] = await evaluator.EvalAsync(expression, evaluator.GlobalEnvironment);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(results, Is.EqualTo(Enumerable.Repeat(expected, count)));
        Assert.That(allocated, Is.LessThan(1_280L * count));
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
