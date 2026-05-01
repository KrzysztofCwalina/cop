using Cop.Lang;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class CodeProxyTests
{
    private static TypeRegistry CreateRegistryWithProviders()
    {
        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);

        // Register some items under "csharp" namespace to simulate a loaded provider
        registry.AppendNamespacedCollection("csharp", "Types", [
            new ScriptObject("Type", new Dictionary<string, object?> { ["Name"] = "MyClass" }),
            new ScriptObject("Type", new Dictionary<string, object?> { ["Name"] = "MyInterface" })
        ]);
        registry.AppendNamespacedCollection("csharp", "Methods", [
            new ScriptObject("Method", new Dictionary<string, object?> { ["Name"] = "DoWork" })
        ]);

        // Register items under "python" namespace
        registry.AppendNamespacedCollection("python", "Types", [
            new ScriptObject("Type", new Dictionary<string, object?> { ["Name"] = "PythonClass" })
        ]);

        return registry;
    }

    [Test]
    public void Code_SingleProvider_ResolvesCollection()
    {
        var registry = CreateRegistryWithProviders();
        var proxy = new CodeProxy(["csharp"]);

        var types = proxy.GetCollection("Types", registry, null);

        Assert.That(types, Has.Count.EqualTo(2));
    }

    [Test]
    public void Code_MultipleProviders_UnionsResults()
    {
        var registry = CreateRegistryWithProviders();
        var proxy = new CodeProxy(["csharp", "python"]);

        var types = proxy.GetCollection("Types", registry, null);

        Assert.That(types, Has.Count.EqualTo(3));
    }

    [Test]
    public void Code_UnknownCollection_ReturnsEmpty()
    {
        var registry = CreateRegistryWithProviders();
        var proxy = new CodeProxy(["csharp"]);

        var items = proxy.GetCollection("NonExistent", registry, null);

        Assert.That(items, Is.Empty);
    }

    [Test]
    public void Code_BuiltInFunction_ParsesAndEvaluates()
    {
        var registry = CreateRegistryWithProviders();
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        // Parse a cop script with Code([csharp])
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
    public void Code_MultipleProviders_ParsesAndUnions()
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

        // Verify parsing worked correctly
        Assert.That(script.LetDeclarations, Has.Count.GreaterThanOrEqualTo(1),
            "let codebase should be parsed as a LetDeclaration");
        Assert.That(script.LetDeclarations.Any(l => l.Name == "codebase"), Is.True,
            "Should have 'codebase' let declaration");

        var interpreter = new ScriptInterpreter(registry);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            interpreter.Run([codeFile, script], []));
        Assert.That(ex!.Message, Does.Contain("not imported"));
    }

    [Test]
    public void Code_ToString_DescribesProxy()
    {
        var proxy = new CodeProxy(["csharp", "python"], "../sdk");
        Assert.That(proxy.ToString(), Is.EqualTo("Code([csharp, python], '../sdk')"));
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
        Assert.That(result.Outputs.Any(o => o.Message.Contains("MyInterface")), Is.True);
    }

    [Test]
    public void Code_ProviderScoped_WithPath_ParsesAndEvaluates()
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

        // Since the path doesn't affect registry-based resolution in tests,
        // the CodeProxy stores the path but still queries the same collections
        Assert.That(result.Outputs, Has.Count.EqualTo(2));
    }
}
