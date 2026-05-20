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
    public void RunsSimpleCommand()
    {
        var bridge = new LanguageBridge();
        bridge.LoadSource(@"
command main = {
    print('hello world')
}");
        bridge.RunCommand("main");
        Assert.That(bridge.Outputs, Has.Count.EqualTo(1));
        Assert.That(bridge.Outputs[0], Is.EqualTo("hello world"));
    }

    [Test]
    public void RunsCommandWithArithmetic()
    {
        var bridge = new LanguageBridge();
        bridge.LoadSource(@"
command main = {
    let x = 3 + 4
    print(x)
}");
        bridge.RunCommand("main");
        Assert.That(bridge.Outputs[0], Is.EqualTo("7"));
    }

    [Test]
    public void RunsCommandWithFunction()
    {
        var bridge = new LanguageBridge();
        bridge.LoadSource(@"
function greet(name : string) : string = 'Hello, ' + name + '!'
command main = {
    print(greet('World'))
}");
        bridge.RunCommand("main");
        Assert.That(bridge.Outputs[0], Is.EqualTo("Hello, World!"));
    }

    // ========================================================================
    // Provider Data Integration
    // ========================================================================

    [Test]
    public void RegisteredCollectionIsAccessible()
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
        bridge.LoadSource(@"
command main = foreach people => print(item.Name)");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs, Is.EqualTo(new[] { "Alice", "Bob", "Carol" }));
    }

    [Test]
    public void FilterOnRegisteredCollection()
    {
        var bridge = new LanguageBridge();

        var items = new List<DataObject>
        {
            CreateDataObject("Item", ("Name", "Foo"), ("Active", true)),
            CreateDataObject("Item", ("Name", "Bar"), ("Active", false)),
            CreateDataObject("Item", ("Name", "Baz"), ("Active", true)),
        };

        bridge.RegisterCollection("items", items);
        bridge.LoadSource(@"
predicate isActive(item) => item.Active
command main = foreach items:isActive => print(item.Name)");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs, Is.EqualTo(new[] { "Foo", "Baz" }));
    }

    [Test]
    public void NestedFieldAccess()
    {
        var bridge = new LanguageBridge();

        var innerObj = CreateDataObject("Address", ("City", "Seattle"), ("State", "WA"));
        var person = new DataObject("Person", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = "Alice",
            ["Address"] = innerObj
        });

        bridge.RegisterCollection("people", [person]);
        bridge.LoadSource(@"
command main = foreach people => print(item.Address.City)");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs[0], Is.EqualTo("Seattle"));
    }

    // ========================================================================
    // Standard Library Functions
    // ========================================================================

    [Test]
    public void AssertPassesOnTrue()
    {
        var bridge = new LanguageBridge();
        bridge.LoadSource(@"
command main = {
    assert(true)
    print('passed')
}");
        bridge.RunCommand("main");
        Assert.That(bridge.Outputs[0], Is.EqualTo("passed"));
        Assert.That(bridge.Errors, Is.Empty);
    }

    [Test]
    public void AssertFailsOnFalse()
    {
        var bridge = new LanguageBridge();
        bridge.LoadSource(@"
command main = {
    assert(false)
}");
        bridge.RunCommand("main");
        Assert.That(bridge.Errors, Has.Count.EqualTo(1));
        Assert.That(bridge.Errors[0], Does.Contain("Assertion failed"));
    }

    [Test]
    public void FailProducesError()
    {
        var bridge = new LanguageBridge();
        bridge.LoadSource(@"
command main = {
    fail('something went wrong')
}");
        bridge.RunCommand("main");
        Assert.That(bridge.Errors, Has.Count.EqualTo(1));
        Assert.That(bridge.Errors[0], Does.Contain("something went wrong"));
    }

    [Test]
    public void TextJoinsCollection()
    {
        var bridge = new LanguageBridge();
        bridge.LoadSource(@"
let items = ['a', 'b', 'c']
command main = print(text(items))");
        bridge.RunCommand("main");
        Assert.That(bridge.Outputs[0], Is.EqualTo("a, b, c"));
    }

    [Test]
    public void TextWithCustomSeparator()
    {
        var bridge = new LanguageBridge();
        bridge.LoadSource(@"
let items = ['x', 'y', 'z']
command main = print(text(items, ' | '))");
        bridge.RunCommand("main");
        Assert.That(bridge.Outputs[0], Is.EqualTo("x | y | z"));
    }

    // ========================================================================
    // Custom Foreign Functions
    // ========================================================================

    [Test]
    public void RegisterCustomFunction()
    {
        var bridge = new LanguageBridge();
        bridge.RegisterFunction("multiply", (args, env) =>
        {
            var a = ((CopInt)args[0]).Value;
            var b = ((CopInt)args[1]).Value;
            return new CopInt(a * b);
        });

        bridge.LoadSource("command main = print(multiply(6, 7))");
        bridge.RunCommand("main");
        Assert.That(bridge.Outputs[0], Is.EqualTo("42"));
    }

    // ========================================================================
    // Expression Evaluation
    // ========================================================================

    [Test]
    public void EvalExpressionReturnsValue()
    {
        var bridge = new LanguageBridge();
        var result = bridge.EvalExpression("3 + 4");
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(7));
    }

    // ========================================================================
    // Lazy Collections
    // ========================================================================

    [Test]
    public void LazyCollectionIsOnlyEnumeratedOnDemand()
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
        bridge.LoadSource("command main = foreach items => print(item.Name)");
        Assert.That(callCount, Is.EqualTo(0));

        // Running the command should enumerate
        bridge.RunCommand("main");
        Assert.That(callCount, Is.EqualTo(1));
        Assert.That(bridge.Outputs, Is.EqualTo(new[] { "One", "Two" }));
    }

    // ========================================================================
    // Multiple Files
    // ========================================================================

    [Test]
    public void LoadsMultipleSources()
    {
        var bridge = new LanguageBridge();
        bridge.LoadSource("function double(n : int) : int = n + n");
        bridge.LoadSource("command main = print(double(21))");
        bridge.RunCommand("main");
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
