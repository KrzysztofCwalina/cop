// Sample: library code that violates AZC0105, AZC0106
// Also tests AZC0101, AZC0102, AZC0013

public class BadLibraryService
{
    // AZC0105 violation: public method with bool parameter named 'async'
    public void ProcessItem(string id, bool async)
    {
    }

    // AZC0106 violation: non-public async method without 'async' bool parameter
    internal async Task DoWorkAsync(string data)
    {
        await Task.Delay(1);
    }

    public async Task RunAsync()
    {
        // AZC0101 violation: ConfigureAwait(true)
        await Task.Delay(1).ConfigureAwait(true);

        // AZC0102 violation: GetAwaiter().GetResult()
        Task.Delay(1).GetAwaiter().GetResult();
    }

    public void CreateSource()
    {
        // AZC0013 violation: TaskCompletionSource without RunContinuationsAsynchronously
        var tcs = new TaskCompletionSource<int>();
    }
}

public class GoodLibraryService
{
    // No async bool param in public methods — good
    public void ProcessItem(string id, bool runParallel)
    {
    }

    // Non-public async with 'async' param — good
    internal async Task DoWorkAsync(string data, bool async)
    {
        await Task.Delay(1).ConfigureAwait(false);
    }
}
