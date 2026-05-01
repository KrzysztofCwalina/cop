using Cop.Lang;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class CodeAggregatorTests
{
    private static TypeRegistry CreateRegistryWithProviders()
    {
        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);

        // Register some items under "csharp" namespace to simulate a loaded provider
        registry.AppendNamespacedCollection("csharp", "Types", [
            new DataObject("Type", new Dictionary<string, object?> { ["Name"] = "MyClass" }),
            new DataObject("Type", new Dictionary<string, object?> { ["Name"] = "MyInterface" })
        ]);
        registry.AppendNamespacedCollection("csharp", "Methods", [
            new DataObject("Method", new Dictionary<string, object?> { ["Name"] = "DoWork" })
        ]);

        // Register items under "python" namespace
        registry.AppendNamespacedCollection("python", "Types", [
            new DataObject("Type", new Dictionary<string, object?> { ["Name"] = "PythonClass" })
        ]);

        return registry;
    }

    // --- DataObject lazy field resolver tests ---

    [Test]
    public void DataObject_LazyField_EvaluatesOnFirstAccess()
    {
        int callCount = 0;
        var obj = new DataObject("Test");
        obj.WithFieldResolver(name =>
        {
            callCount++;
            return name == "Items" ? new List<object> { "a", "b" } : null;
        });

        Assert.That(callCount, Is.EqualTo(0), "Resolver should not be called before access");
        var items = obj.GetField("Items");
        Assert.That(callCount, Is.EqualTo(1), "Resolver should be called once on first access");
        Assert.That(items, Is.InstanceOf<List<object>>());
        Assert.That((items as List<object>)!, Has.Count.EqualTo(2));
    }

    [Test]
    public void DataObject_LazyField_MemoizesResult()
    {
        int callCount = 0;
        var obj = new DataObject("Test");
        obj.WithFieldResolver(name =>
        {
            callCount++;
            return new List<object> { "x" };
        });

        obj.GetField("Items"); // first call
        obj.GetField("Items"); // second call — should use cache
        Assert.That(callCount, Is.EqualTo(1), "Resolver should be called only once (memoized)");
    }

    [Test]
    public void DataObject_LazyField_DifferentFieldsResolveIndependently()
    {
        var resolved = new HashSet<string>();
        var obj = new DataObject("Test");
        obj.WithFieldResolver(name =>
        {
            resolved.Add(name);
            return new List<object> { $"value-{name}" };
        });

        obj.GetField("Types");
        Assert.That(resolved, Is.EquivalentTo(new[] { "Types" }), "Only Types should be resolved");

        obj.GetField("Methods");
        Assert.That(resolved, Is.EquivalentTo(new[] { "Types", "Methods" }));
    }

    [Test]
    public void DataObject_EagerField_TakesPriorityOverResolver()
    {
        var obj = new DataObject("Test");
        obj.Set("Eager", "immediate");
        obj.WithFieldResolver(name => "lazy");

        Assert.That(obj.GetField("Eager"), Is.EqualTo("immediate"));
    }

    // --- Code() function integration tests ---

    [Test]
    public void Code_SingleProvider_ResolvesCollection()
    {
        var registry = CreateRegistryWithProviders();
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        var script = ScriptParser.Parse(@"
import code

let codebase = Code([csharp])
foreach codebase.Types => '{item.Name}'
", "test.cop");

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        Assert.That(result.Outputs, Has.Count.EqualTo(2));
        Assert.That(result.Outputs.Any(o => o.Message.Contains("MyClass")), Is.True);
        Assert.That(result.Outputs.Any(o => o.Message.Contains("MyInterface")), Is.True);
    }

    [Test]
    public void Code_MultipleProviders_UnionsResults()
    {
        var registry = CreateRegistryWithProviders();
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        var script = ScriptParser.Parse(@"
import code

let codebase = Code([csharp, python])
foreach codebase.Types => '{item.Name}'
", "test.cop");

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        Assert.That(result.Outputs, Has.Count.EqualTo(3));
        Assert.That(result.Outputs.Any(o => o.Message.Contains("PythonClass")), Is.True);
    }

    [Test]
    public void Code_NonImportedProvider_ThrowsHelpfulError()
    {
        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        var script = ScriptParser.Parse(
            "let codebase = Code([nonexistent])\nforeach codebase.Types => '{item.Name}'\n", "test.cop");

        var interpreter = new ScriptInterpreter(registry);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            interpreter.Run([codeFile, script], []));
        Assert.That(ex!.Message, Does.Contain("not imported"));
    }

    [Test]
    public void Code_ProviderScoped_ParsesAndEvaluates()
    {
        var registry = CreateRegistryWithProviders();
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        // csharp.Code() — provider-scoped syntax (no path)
        var script = ScriptParser.Parse(@"
import code

let codebase = csharp.Code()
foreach codebase.Types => '{item.Name}'
", "test.cop");

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        Assert.That(result.Outputs, Has.Count.EqualTo(2));
        Assert.That(result.Outputs.Any(o => o.Message.Contains("MyClass")), Is.True);
    }

    [Test]
    public void Code_ProviderScoped_WithPath()
    {
        var registry = CreateRegistryWithProviders();
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        // csharp.Code('somepath') — provider-scoped with path
        var script = ScriptParser.Parse(@"
import code

let codebase = csharp.Code('somepath')
foreach codebase.Types => '{item.Name}'
", "test.cop");

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        // Path doesn't affect registry-based resolution in tests
        Assert.That(result.Outputs, Has.Count.EqualTo(2));
    }

    // --- Structural tests: chained let bindings, aliases, member access ---

    [Test]
    public void Code_LetAlias_TypesMemberAccess()
    {
        var registry = CreateRegistryWithProviders();
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        // let types = codebase.Types should work whether parsed as value or collection
        var script = ScriptParser.Parse(@"
import code

let codebase = Code([csharp])
let types = codebase.Types
foreach types => '{item.Name}'
", "test.cop");

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        Assert.That(result.Outputs, Has.Count.EqualTo(2));
        Assert.That(result.Outputs.Any(o => o.Message.Contains("MyClass")), Is.True);
    }

    [Test]
    public void Code_ChainedCount_ResolvesThroughLazyFields()
    {
        var registry = CreateRegistryWithProviders();
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        // codebase.Types.Count — chained member access on lazy DataObject
        var script = ScriptParser.Parse(@"
import code

let codebase = Code([csharp])
let ntypes = codebase.Types.Count
foreach codebase.Types => '{ntypes} {item.Name}'
", "test.cop");

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        Assert.That(result.Outputs, Has.Count.EqualTo(2));
        Assert.That(result.Outputs[0].Message, Does.Contain("2"));
    }

    [Test]
    public void Code_LetTypes_ThenCount()
    {
        var registry = CreateRegistryWithProviders();
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        // let types = codebase.Types; let n = types.Count — two levels of indirection
        var script = ScriptParser.Parse(@"
import code

let codebase = Code([csharp])
let types = codebase.Types
let n = types.Count
foreach types => '{n} {item.Name}'
", "test.cop");

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        Assert.That(result.Outputs, Has.Count.EqualTo(2));
        Assert.That(result.Outputs[0].Message, Does.Contain("2"));
    }

    [Test]
    public void Code_LetTypes_Foreach_WithPredicate()
    {
        var registry = CreateRegistryWithProviders();
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        // Predicate filter on aliased collection (uses evaluator path, not filter hints)
        var script = ScriptParser.Parse(@"
import code

let codebase = Code([csharp])
let types = codebase.Types
predicate startsWithMy(Type) => item.Name:sw('MyC')
foreach types:startsWithMy => '{item.Name}'
", "test.cop");

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        Assert.That(result.Outputs, Has.Count.EqualTo(1));
        Assert.That(result.Outputs[0].Message, Does.Contain("MyClass"));
    }

    [Test]
    public void Code_DifferentCollections_ResolveLazily()
    {
        var registry = CreateRegistryWithProviders();
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        // Accessing different collections on the same codebase object
        var script = ScriptParser.Parse(@"
import code

let codebase = Code([csharp])
foreach codebase.Types => '{item.Name}'
foreach codebase.Methods => '{item.Name}'
", "test.cop");

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        Assert.That(result.Outputs, Has.Count.EqualTo(3));
        Assert.That(result.Outputs.Any(o => o.Message.Contains("MyClass")), Is.True);
        Assert.That(result.Outputs.Any(o => o.Message.Contains("DoWork")), Is.True);
    }

    [Test]
    public void Code_TemplateInterpolation()
    {
        var registry = CreateRegistryWithProviders();
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        // Template with codebase.Types.Count — tests GetPropertyViaRegistry path
        var script = ScriptParser.Parse(@"
import code

let codebase = Code([csharp])
foreach codebase.Types => '{codebase.Types.Count} types: {item.Name}'
", "test.cop");

        var interpreter = new ScriptInterpreter(registry);
        var result = interpreter.Run([codeFile, script], []);

        Assert.That(result.Outputs, Has.Count.EqualTo(2));
        Assert.That(result.Outputs[0].Message, Does.Contain("2 types:"));
    }
}
