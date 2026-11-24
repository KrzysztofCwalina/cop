using NUnit.Framework;

namespace Cop.Tests;

public partial class OperationalSafetyChecksTests
{
    [Test]
    public async Task Issue137_AsyncAwaitChecksInspectOwnedNestedAndReturnedExpressions()
    {
        Write("Input.cs", """
            using System;
            using System.Threading.Tasks;
            class Input {
                async Task NoAwait() { Work(); }
                async Task Nested(bool enabled) { if (enabled) { await Task.Delay(1); } }
                async Task<int> Returned() { return await Task.FromResult(1); }
                async Task Initialized() { var value = await Task.FromResult(1); }
                async Task Conditional(bool enabled) {
                    var value = enabled ? await Task.FromResult(1) : await Task.FromResult(2);
                }
                async Task Outer() {
                    async Task Local() { await Task.Delay(1); }
                    Func<Task> callback = async () => await Task.Delay(1);
                    Func<Task> blockCallback = async () => { await Task.Delay(1); };
                }
                void Work() {}
            }
            """);
        await AssertOutput("csharp", "asyncWithoutAwait(cb.Methods).Select(item.Message)",
            [
                "Async method NoAwait contains no await; remove async or await the intended operation",
                "Async method Outer contains no await; remove async or await the intended operation"
            ]);
    }
}
