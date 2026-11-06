using NUnit.Framework;
using Cop.Lang;
using Cop.Lang.Interpreter;

namespace Cop.Tests.Lang;

[TestFixture]
public class LanguageBridgeTests
{
    // ========================================================================
    // Basic Execution
    // ========================================================================

    [Test]
    public async Task RunsSimpleCommand()
    {
        var bridge = new LanguageBridge();
        await bridge.LoadSourceAsync(@"
command main = {
    print('hello world')
}", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        Assert.That(bridge.Outputs, Has.Count.EqualTo(1));
        Assert.That(bridge.Outputs[0], Is.EqualTo("hello world"));
    }

    [Test]
    public async Task RunsCommandWithArithmetic()
    {
        var bridge = new LanguageBridge();
        await bridge.LoadSourceAsync(@"
command main = {
    let x = 3 + 4
    print(x)
}", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        Assert.That(bridge.Outputs[0], Is.EqualTo("7"));
    }

    [Test]
    public async Task RunsCommandWithFunction()
    {
        var bridge = new LanguageBridge();
        await bridge.LoadSourceAsync(@"
function greet(name : string) : string = 'Hello, ' + name + '!'
command main = {
    print(greet('World'))
}", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        Assert.That(bridge.Outputs[0], Is.EqualTo("Hello, World!"));
    }

    // ========================================================================
    // Provider Data Integration
    // ========================================================================

    [Test]
    public async Task RegisteredCollectionIsAccessible()
    {
        var bridge = new LanguageBridge();

        // Simulate provider data
        var items = new List<DataObject>
        {
            CreateDataObject("Person", ("Name", "Alice"), ("Age", 30)),
            CreateDataObject("Person", ("Name", "Bob"), ("Age", 25)),
            CreateDataObject("Person", ("Name", "Carol"), ("Age", 35)),
        };

        bridge.RegisterCollection("people", items);
        await bridge.LoadSourceAsync(@"
command main = foreach people => print(item.Name)", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);

        Assert.That(bridge.Outputs, Is.EqualTo(new[] { "Alice", "Bob", "Carol" }));
    }

    [Test]
    public async Task FilterOnRegisteredCollection()
    {
        var bridge = new LanguageBridge();

        var items = new List<DataObject>
        {
            CreateDataObject("Item", ("Name", "Foo"), ("Active", true)),
            CreateDataObject("Item", ("Name", "Bar"), ("Active", false)),
            CreateDataObject("Item", ("Name", "Baz"), ("Active", true)),
        };

        bridge.RegisterCollection("items", items);
        await bridge.LoadSourceAsync(@"
predicate isActive(item) => item.Active
command main = foreach items:isActive => print(item.Name)", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);

        Assert.That(bridge.Outputs, Is.EqualTo(new[] { "Foo", "Baz" }));
    }

    [Test]
    public async Task NestedFieldAccess()
    {
        var bridge = new LanguageBridge();

        var innerObj = CreateDataObject("Address", ("City", "Seattle"), ("State", "WA"));
        var person = new DataObject("Person", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = "Alice",
            ["Address"] = innerObj
        });

        bridge.RegisterCollection("people", [person]);
        await bridge.LoadSourceAsync(@"
command main = foreach people => print(item.Address.City)", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);

        Assert.That(bridge.Outputs[0], Is.EqualTo("Seattle"));
    }

    // ========================================================================
    // Standard Library Functions
    // ========================================================================

    [Test]
    public async Task AssertPassesOnTrue()
    {
        var bridge = new LanguageBridge();
        await bridge.LoadSourceAsync(@"
command main = {
    assert(true)
    print('passed')
}", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        Assert.That(bridge.Outputs[0], Is.EqualTo("passed"));
        Assert.That(bridge.Errors, Is.Empty);
    }

    [Test]
    public async Task AssertFailsOnFalse()
    {
        var bridge = new LanguageBridge();
        await bridge.LoadSourceAsync(@"
command main = {
    assert(false)
}", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        Assert.That(bridge.Errors, Has.Count.EqualTo(1));
        Assert.That(bridge.Errors[0], Does.Contain("Assertion failed"));
    }

    [Test]
    public async Task FailProducesError()
    {
        var bridge = new LanguageBridge();
        await bridge.LoadSourceAsync(@"
command main = {
    fail('something went wrong')
}", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        Assert.That(bridge.Errors, Has.Count.EqualTo(1));
        Assert.That(bridge.Errors[0], Does.Contain("something went wrong"));
    }

    [Test]
    public async Task TextJoinsCollection()
    {
        var bridge = new LanguageBridge();
        await bridge.LoadSourceAsync(@"
let items = ['a', 'b', 'c']
command main = print(text(items))", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        Assert.That(bridge.Outputs[0], Is.EqualTo("a, b, c"));
    }

    [Test]
    public async Task TextWithCustomSeparator()
    {
        var bridge = new LanguageBridge();
        await bridge.LoadSourceAsync(@"
let items = ['x', 'y', 'z']
command main = print(text(items, ' | '))", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        Assert.That(bridge.Outputs[0], Is.EqualTo("x | y | z"));
    }

    // ========================================================================
    // Custom Foreign Functions
    // ========================================================================

    [Test]
    public async Task RegisterCustomFunction()
    {
        var bridge = new LanguageBridge();
        bridge.RegisterFunction("multiply", (args, env) =>
        {
            var a = ((CopInt)args[0]).Value;
            var b = ((CopInt)args[1]).Value;
            return new CopInt(a * b);
        });

        await bridge.LoadSourceAsync("command main = print(multiply(6, 7))", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        Assert.That(bridge.Outputs[0], Is.EqualTo("42"));
    }

    // ========================================================================
    // Expression Evaluation
    // ========================================================================

    [Test]
    public async Task EvalExpressionReturnsValue()
    {
        var bridge = new LanguageBridge();
        var result = (await bridge.EvalExpressionAsync("3 + 4", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(7));
    }

    // ========================================================================
    // Lazy Collections
    // ========================================================================

    [Test]
    public async Task LazyCollectionIsOnlyEnumeratedOnDemand()
    {
        var bridge = new LanguageBridge();
        int callCount = 0;

        bridge.RegisterLazyCollection("items", () =>
        {
            callCount++;
            return new[]
            {
                CreateDataObject("Item", ("Name", "One")),
                CreateDataObject("Item", ("Name", "Two")),
            };
        });

        // Just loading shouldn't enumerate
        await bridge.LoadSourceAsync("command main = foreach items => print(item.Name)", cancellationToken: CancellationToken.None);
        Assert.That(callCount, Is.EqualTo(0));

        // Running the command should enumerate
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        Assert.That(callCount, Is.EqualTo(1));
        Assert.That(bridge.Outputs, Is.EqualTo(new[] { "One", "Two" }));
    }

    // ========================================================================
    // Multiple Files
    // ========================================================================

    [Test]
    public async Task LoadsMultipleSources()
    {
        var bridge = new LanguageBridge();
        await bridge.LoadSourceAsync("function double(n : int) : int = n + n", cancellationToken: CancellationToken.None);
        await bridge.LoadSourceAsync("command main = print(double(21))", cancellationToken: CancellationToken.None);
        await bridge.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        Assert.That(bridge.Outputs[0], Is.EqualTo("42"));
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static DataObject CreateDataObject(string typeName, params (string Key, object? Value)[] fields)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
            dict[key] = value;
        return new DataObject(typeName, dict);
    }
}
