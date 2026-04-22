// Sample: async/sync pattern violations for AZC0103, AZC0104, AZC0107, AZC0110, AZC0111

public class AsyncSyncBadService
{
    // AZC0103: sync-blocking calls in async method
    public async Task BadAsyncMethod()
    {
        SomeTask().Wait();
        var r = SomeTask().GetAwaiter().GetResult();
    }

    // AZC0104: GetResult() in sync method (should use EnsureCompleted)
    public void BadSyncGetResult()
    {
        var r = SomeTask().GetAwaiter().GetResult();
    }

    // AZC0107: calling *Async method from sync context
    public void BadSyncCallsAsync()
    {
        DoWorkAsync();
    }

    // AZC0110: unconditional await in dual-mode method
    // AZC0111: unconditional EnsureCompleted in dual-mode method
    internal async Task DualModeBad(bool async)
    {
        await SomeTask();
        SomeTask().EnsureCompleted();
    }

    // AZC0108: wrong async arg value in call
    internal async Task DualModeBadAsyncArg(bool async)
    {
        if (async)
        {
            await InnerAsync(async: false);
        }
        else
        {
            InnerAsync(async: true).EnsureCompleted();
        }
    }

    // AZC0109: misuse of async parameter — used outside if/ternary
    internal async Task DualModeBadAsyncMisuse(bool async)
    {
        LogAsync(async);
    }

    // Good: properly guarded dual-mode — should NOT be flagged
    internal async Task DualModeGood(bool async)
    {
        if (async)
        {
            await SomeTask();
        }
        else
        {
            SomeTask().EnsureCompleted();
        }
    }

    // Good: properly guarded dual-mode with correct async arg values
    internal async Task DualModeGoodAsyncArg(bool async)
    {
        if (async)
        {
            await InnerAsync(async: true);
        }
        else
        {
            InnerAsync(async: false).EnsureCompleted();
        }
    }

    // Good: async method without sync-blocking — should NOT be flagged
    public async Task GoodAsyncMethod()
    {
        await SomeTask();
    }

    // Good: sync method using EnsureCompleted — should NOT be flagged
    public void GoodSyncMethod()
    {
        SomeTask().EnsureCompleted();
    }

    private Task SomeTask() => Task.CompletedTask;
    private Task DoWorkAsync() => Task.CompletedTask;
    private Task InnerAsync(bool async = true) => Task.CompletedTask;
    private void LogAsync(bool value) { }
}
