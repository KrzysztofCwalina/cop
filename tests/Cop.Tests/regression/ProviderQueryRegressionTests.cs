using System.Text.Json;
using Cop.Core;
using Cop.Lang.Interpreter;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class ProviderQueryRegressionTests
{
    private string _root = null!;
    private static string RepoRoot => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-query-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    [Test]
    public void Issue070_CompoundJsonPropertyFilterPreservesRecords()
    {
        var result = Run("""
            let chosen = json.Parse('input.json'):Name:startsWith('A')
            command MAIN = foreach chosen => '{item.Name}'
            """);
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "Alpha" }));
    }

    [TestCase("count(items)")]
    [TestCase("items.count()")]
    [TestCase("items.Count")]
    public void Issue073_JsonQueryableCountIsExact(string terminal)
    {
        var result = Run($"let items = json.Parse('input.json')\ncommand MAIN = print({terminal})");
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "2" }));
    }

    [TestCase("cb.Types:hasAlphaPrefix:isClient")]
    [TestCase("cb.Types:selected")]
    public void Issue075_CrossFilePredicatesUseLocalProviderAndPushBothFilters(string expression)
    {
        File.WriteAllText(Path.Combine(_root, "input.cs"),
            "class AlphaClient {} class BetaClient {} class AlphaService {}");
        File.WriteAllText(Path.Combine(_root, "prefix.cop"),
            "predicate hasAlphaPrefix(Type) => Type.Name:startsWith('Alpha')");
        File.WriteAllText(Path.Combine(_root, "suffix.cop"),
            "predicate isClient(Type) => Type.Name:endsWith('Client')");
        File.WriteAllText(Path.Combine(_root, "main.cop"), $$"""
            import code
            import csharp
            let cb = csharp.parse()
            predicate selected(Type) => hasAlphaPrefix && isClient
            command MAIN = foreach {{expression}} => '{item.Name}'
            """);
        var diagnostics = new List<string>();
        var result = Engine.Run(_root, _root,
            additionalFeedPaths: [Path.Combine(RepoRoot, "packages")], diagLog: diagnostics.Add);
        Assert.That(result.Errors.Concat(result.ParseErrors), Is.Empty);
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "AlphaClient" }));
        Assert.That(diagnostics, Has.Some.Contains(Path.Combine(RepoRoot, "packages", "languages", "csharp")));
        Assert.That(diagnostics, Has.Some.Contains("Name:startsWith('Alpha'), Name:endsWith('Client')"));
        Assert.That(diagnostics, Has.Some.Contains("[query-result] csharp.Types returned=1"));
        Assert.That(diagnostics.Count(d => d.Contains("[query]")), Is.EqualTo(1));
    }

    [Test]
    public void Issue076_OrderedLazyCollectionLastReturnsLastRecord()
    {
        var result = Run("""
            let items = json.Parse('input.json')
            command MAIN =
                print(items.OrderBy(item.Name).Last.Name) &
                print(items.OrderByDescending(item.Name).last.Name) &
                print(items.Where(item.Name == 'Alpha').Last.Name)
            """);
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "Beta", "Alpha", "Alpha" }));
    }

    [Test]
    public void Issue071_ProcessProviderReceivesFilterAndIssue072IgnoredFilterRemainsCorrect()
    {
        var sdk = JsonSerializer.Serialize(Path.Combine(RepoRoot, "sdk", "python"));
        File.WriteAllText(Path.Combine(_root, "provider.py"), $$"""
            import json, sys
            sys.path.insert(0, {{sdk}})
            from cop_provider_sdk import define_provider
            def schema():
                return {"types":[{"name":"Widget","properties":[{"name":"Name"},{"name":"Active","type":"bool"}]}],
                        "collections":[{"name":"Widgets","itemType":"Widget"}]}
            def query(params):
                with open("request.json", "w") as f:
                    json.dump(params, f)
                return {"Widgets":[{"Name":"Alpha","Active":True},{"Name":"Beta","Active":False}]}
            define_provider(schema=schema, query=query)
            """);
        using var provider = new ProcessObjectProvider("python", "provider.py", _root) { Timeout = TimeSpan.FromSeconds(15) };
        var service = new ProviderQueryService(_root);
        service.RegisterProvider("widgets", provider, ProviderSchema.FromJson(provider.GetSchema()));
        var source = (CopQueryable)service.CreateValue("widgets", new ProviderQuery { RootPath = _root });
        var result = source.WithFilter(new PropertyFilter("Active", true)).Enumerate()
            .Cast<CopDynamicObject>().Select(o => o.GetField("Name").Display()).ToArray();
        Assert.That(result, Is.EqualTo(new[] { "Alpha" }));
        using var request = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "request.json")));
        Assert.That(request.RootElement.GetProperty("collection").GetString(), Is.EqualTo("Widgets"));
        Assert.That(request.RootElement.GetProperty("filter").GetProperty("property").GetString(), Is.EqualTo("Active"));
        Assert.That(request.RootElement.GetProperty("collectionFilters").GetProperty("Widgets").GetProperty("value").GetBoolean(), Is.True);
    }

    private EngineResult Run(string program)
    {
        File.WriteAllText(Path.Combine(_root, "input.json"), """[{"Name":"Alpha"},{"Name":"Beta"}]""");
        File.WriteAllText(Path.Combine(_root, "main.cop"), "import json\n" + program);
        var result = Engine.Run(_root, _root, additionalFeedPaths: [Path.Combine(RepoRoot, "packages")]);
        Assert.That(result.Errors.Concat(result.ParseErrors), Is.Empty);
        return result;
    }
}
