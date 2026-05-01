using Cop.Lang;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class CurryingTests
{
    [Test]
    public void CopClosure_PropertiesAreCorrect()
    {
        var func = new FunctionDefinition(
            "test", "MyType", "Output",
            [new("x", "String"), new("y", "String")],
            new Dictionary<string, Expression>(), 1, false,
            new LiteralExpr(42));

        var closure = new CopClosure(func, new List<object?> { "hello" });
        Assert.That(closure.Function, Is.SameAs(func));
        Assert.That(closure.BoundArgs, Has.Count.EqualTo(1));
        Assert.That(closure.BoundArgs[0], Is.EqualTo("hello"));
        Assert.That(closure.RemainingArgs, Is.EqualTo(1));
    }

    [Test]
    public void CopClosure_ToString_DescribesPartialApplication()
    {
        var func = new FunctionDefinition(
            "greet", "Input", "Output",
            [new("name", "String"), new("greeting", "String")],
            new Dictionary<string, Expression>(), 1, false,
            new LiteralExpr("placeholder"));

        var closure = new CopClosure(func, new List<object?> { "World" });
        Assert.That(closure.ToString(), Is.EqualTo("greet(World, ...)"));
        Assert.That(closure.RemainingArgs, Is.EqualTo(1));
    }

    [Test]
    public void CopClosure_MultipleStagesOfPartialApplication()
    {
        var func = new FunctionDefinition(
            "triple", "Input", "Output",
            [new("a", "String"), new("b", "String"), new("c", "String")],
            new Dictionary<string, Expression>(), 1, false,
            new LiteralExpr("placeholder"));

        // First partial: bind 1 of 3
        var closure1 = new CopClosure(func, new List<object?> { "X" });
        Assert.That(closure1.RemainingArgs, Is.EqualTo(2));

        // Second partial: bind 2 of 3
        var closure2 = new CopClosure(func, new List<object?> { "X", "Y" });
        Assert.That(closure2.RemainingArgs, Is.EqualTo(1));
    }

    [Test]
    public void PartialApplication_FullCall_ProducesResult()
    {
        // Full call with all args → no currying, just normal function call as filter transform
        var script = ScriptParser.Parse(@"
import code

function add(Type, a: String, b: String) => '{a}{b}'
foreach csharp.Types:add('hello', ' world') => '{item}'
", "test.cop");

        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);
        registry.AppendNamespacedCollection("csharp", "Types", [
            new ScriptObject("Type", new Dictionary<string, object?> { ["Name"] = "Foo" })
        ]);

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        Assert.That(result.Outputs, Has.Count.EqualTo(1));
        Assert.That(result.Outputs[0].Message, Is.EqualTo("hello world"));
    }

    [Test]
    public void PartialApplication_LetBindsClosureThenInvoke()
    {
        // let partial = func('arg1') → closure stored via let
        // foreach items:partial('arg2') → completes the call as a filter transform
        var script = ScriptParser.Parse(@"
import code

function combine(Type, a: String, b: String) => '{a}-{b}'
let partial = combine('first')
foreach csharp.Types:partial('second') => '{item}'
", "test.cop");

        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);
        registry.AppendNamespacedCollection("csharp", "Types", [
            new ScriptObject("Type", new Dictionary<string, object?> { ["Name"] = "Foo" })
        ]);

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        Assert.That(result.Outputs, Has.Count.EqualTo(1));
        Assert.That(result.Outputs[0].Message, Is.EqualTo("first-second"));
    }
}

