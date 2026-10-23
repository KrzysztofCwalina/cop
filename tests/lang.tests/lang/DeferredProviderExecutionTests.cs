using System.Reflection;
using System.Text.Json;
using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class DeferredProviderExecutionTests
{
    private string _root = null!;
    private static string RepoRoot => FindRepoRoot();

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    [Test]
    public void IgnoredPushdown_StillAppliesExactResidual()
    {
        var provider = new RecordingQueryProvider();
        var service = Service(provider);
        var result = service.QueryProvider("recording", new ProviderQuery
        {
            RootPath = _root, Collection = "Items", Filter = new PropertyFilter("Active", true)
        });
        Assert.That(Names(result), Is.EqualTo(new[] { "Alpha" }));
    }

    [Test]
    public void QueryableEnumeration_IsMemoized()
    {
        var provider = new RecordingQueryProvider();
        var service = Service(provider);
        var query = new CopQueryable("recording",
            new ProviderQuery { RootPath = _root, Collection = "Items" }, service.QueryProvider);
        Assert.That(Names(query), Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That(Names(query), Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
    }

    [TestCase("items.count()", "2")]
    [TestCase("count(items)", "2")]
    [TestCase("items.Count", "2")]
    [TestCase("items:Active.Count", "1")]
    [TestCase("items:Name:startsWith('A').Count", "1")]
    [TestCase("items[1].Name", "Beta")]
    [TestCase("items.take(1).Count", "1")]
    [TestCase("items.skip(1).first().Name", "Beta")]
    public void QueryableTerminals_ReturnExactValues(string expression, string expected)
    {
        var provider = new RecordingQueryProvider();
        var service = Service(provider);
        var evaluator = EvaluatorFor(new CopQueryable("recording",
            new ProviderQuery { RootPath = _root, Collection = "Items" }, service.QueryProvider));
        evaluator.EvalModule(CopParser.Parse($"command MAIN = {expression}"));
        Assert.That(evaluator.RunCommand("MAIN").Display(), Is.EqualTo(expected));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
    }

    [Test]
    public void NamedPredicate_ComposesWithoutExecutingProvider()
    {
        var provider = new RecordingQueryProvider();
        var service = Service(provider);
        var evaluator = EvaluatorFor(new CopQueryable("recording",
            new ProviderQuery { RootPath = _root, Collection = "Items" }, service.QueryProvider));
        evaluator.EvalModule(CopParser.Parse("""
            predicate selected(p) => p.Name == 'Alpha' && p.Active
            command MAIN = items:selected
            """));
        var result = evaluator.RunCommand("MAIN");
        Assert.That(provider.Queries, Is.Empty, "Composing a query must not fetch its source.");
        Assert.That(Names(result), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(provider.Queries.Single().Filter, Is.InstanceOf<AndFilter>());
    }

    [Test]
    public void UnsupportedPredicate_RemainsDeferredAndRunsLocally()
    {
        var provider = new RecordingQueryProvider();
        var service = Service(provider);
        var evaluator = EvaluatorFor(new CopQueryable("recording",
            new ProviderQuery { RootPath = _root, Collection = "Items" }, service.QueryProvider));
        evaluator.EvalModule(CopParser.Parse("""
            predicate selected(p) => p.Name.Length == 5
            command MAIN = items:selected
            """));
        var result = evaluator.RunCommand("MAIN");
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(Names(result), Is.EqualTo(new[] { "Alpha" }));
    }

    [Test]
    public void LazyFirst_DoesNotProduceRemainingItems()
    {
        int produced = 0;
        var source = new CopLazyCollection(() => Enumerable.Range(0, 100).Select(i =>
        {
            produced++;
            return (CopValue)new CopInt(i);
        }));
        Assert.That(source.Enumerate().First().Display(), Is.EqualTo("0"));
        Assert.That(produced, Is.EqualTo(1));
        Assert.That(source.Enumerate().Take(3).Select(v => v.Display()), Is.EqualTo(new[] { "0", "1", "2" }));
        Assert.That(produced, Is.EqualTo(3));
        Assert.That(source.Enumerate().Count(), Is.EqualTo(100));
        Assert.That(source.Enumerate().Count(), Is.EqualTo(100));
        Assert.That(produced, Is.EqualTo(100));
    }

    [Test]
    public void ProcessQuery_SerializesBothFilterChannels()
    {
        var build = typeof(ProcessObjectProvider).GetMethod("BuildQueryRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
        var filter = new PropertyFilter("Active", true);
        var wire = (string)build.Invoke(null, [new ProviderQuery
        {
            RootPath = _root, Collection = "Items", Filter = filter,
            CollectionFilters = new Dictionary<string, FilterExpression> { ["Items"] = filter }
        }])!;
        using var json = JsonDocument.Parse(wire);
        var parameters = json.RootElement.GetProperty("params");
        Assert.That(parameters.TryGetProperty("filter", out var combined), Is.True);
        Assert.That(combined.GetProperty("property").GetString(), Is.EqualTo("Active"));
        Assert.That(parameters.GetProperty("collectionFilters").GetProperty("Items").GetProperty("value").GetBoolean(), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EngineCollectionAccess_PushesNamedFilterAndPrunesOtherCollections(bool scoped)
    {
        // Loaded provider DLLs stay mapped on Windows until the test host exits.
        var package = Path.Combine(TestContext.CurrentContext.TestDirectory, "query-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(package, "lib"));
        File.Copy(typeof(RecordingQueryProvider).Assembly.Location, Path.Combine(package, "lib", "Lang.Tests.dll"));
        var metadata = new PackageMetadata
        {
            Name = "recording", Version = "1.0.0", Title = "Query test", Description = "Query test",
            Authors = "Cop", Provider = "clr", ProviderEntry = typeof(RecordingQueryProvider).FullName!,
            ProviderAssembly = "Lang.Tests.dll"
        };
        var access = scoped ? "provider('recording', '.').Items" : "provider('recording').Items";
        var program = $"let items = {access}\npredicate selected(p) => p.Name == 'Alpha'\ncommand MAIN = foreach items:selected => '{{item.Name}}'";
        var script = Path.Combine(_root, "main.cop");
        File.WriteAllText(script, program);
        var result = Engine.Run(_root, _root, scriptFiles: [script], providerPackages: [(package, metadata)]);
        Assert.That(result.Errors.Concat(result.ParseErrors), Is.Empty);
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "Alpha" }));
        using var query = JsonDocument.Parse(File.ReadAllLines(Path.Combine(_root, "queries.jsonl")).Single());
        Assert.That(query.RootElement.GetProperty("Collection").GetString(), Is.EqualTo("Items"));
        Assert.That(query.RootElement.GetProperty("Filter").GetString(), Is.EqualTo("Name:equalsExact('Alpha')"));
    }

    [TestCase("csharp", "input.cs", "public class Alpha {} internal class Beta {}")]
    [TestCase("python", "input.py", "class Alpha:\n    pass\nclass Beta:\n    pass\n")]
    [TestCase("javascript", "input.js", "class Alpha {} class Beta {}")]
    [TestCase("go", "input.go", "package example\ntype Alpha struct {}\ntype Beta struct {}\n")]
    [TestCase("java", "input.java", "class Alpha {} class Beta {}")]
    [TestCase("rust", "input.rs", "pub struct Alpha {}\nstruct Beta {}\n")]
    [TestCase("cop", "input.cop", "type Alpha = { Name : string }\ntype Beta = { Name : string }\n")]
    public void LanguagePackages_PreserveDeferredCollectionsAndPushNamedPredicates(string language, string file, string source)
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, file), source);
        var script = Path.Combine(_root, "main.cop");
        File.WriteAllText(script, $$"""
            import code
            import {{language}}
            let cb = codebase({{language}}.parse())
            predicate selected(Type) => Type.Name == 'Alpha'
            command MAIN = foreach cb.Types:selected => '{item.Name}'
            """);
        var diagnostics = new List<string>();
        var result = Engine.Run(_root, target, scriptFiles: [script],
            additionalFeedPaths: [Path.Combine(RepoRoot, "packages")], diagLog: diagnostics.Add);
        Assert.That(result.Errors.Concat(result.ParseErrors), Is.Empty);
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(diagnostics, Has.Some.Contains($"Query {language}.Types"));
        Assert.That(diagnostics, Has.Some.Contains("Name:equalsExact('Alpha')"));
        Assert.That(diagnostics, Has.Some.Contains($"[query-result] {language}.Types returned=1"));
        Assert.That(diagnostics.Count(d => d.Contains("[query]")), Is.EqualTo(1),
            "Creating/unifying a Codebase must not scan unused collections.");
    }

    [Test]
    public void FilesystemPackage_PushesFilterToOnlyFiles()
    {
        File.WriteAllText(Path.Combine(_root, "Alpha.txt"), "alpha");
        File.WriteAllText(Path.Combine(_root, "Beta.txt"), "beta");
        var script = Path.Combine(_root, "main.cop");
        File.WriteAllText(script, """
            import files
            predicate selected(File) => File.Name == 'Alpha.txt'
            command MAIN = foreach files():selected => '{item.Name}'
            """);
        var diagnostics = new List<string>();
        var result = Engine.Run(_root, _root, scriptFiles: [script],
            additionalFeedPaths: [Path.Combine(RepoRoot, "packages")], diagLog: diagnostics.Add);
        Assert.That(result.Errors.Concat(result.ParseErrors), Is.Empty);
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "Alpha.txt" }));
        Assert.That(diagnostics, Has.Some.Contains("Query filesystem.Files"));
        Assert.That(diagnostics, Has.Some.Contains("Name:equalsExact('Alpha.txt')"));
        Assert.That(diagnostics, Has.Some.Contains("[query-result] filesystem.Files returned=1"));
    }

    private ProviderQueryService Service(RecordingQueryProvider provider)
    {
        var service = new ProviderQueryService(_root);
        service.RegisterProvider("recording", provider, ProviderSchema.FromJson(provider.GetSchema()));
        return service;
    }

    [TestCase("markdown", "input.md", "# Alpha\n# Beta\n", "Headings", "Heading", "Text", "Alpha")]
    [TestCase("yaml", "input.yaml", "Alpha: one\nBeta: two\n", "Entries", "YamlEntry", "Key", "Alpha")]
    [TestCase("xml", "input.xml", "<Root><Alpha/><Beta/></Root>", "Elements", "XmlElement", "Name", "Alpha")]
    [TestCase("dockerfile", "Dockerfile", "FROM alpine\nRUN echo hi\n", "Instructions", "DockerInstruction", "Instruction", "RUN")]
    [TestCase("bash", "input.sh", "echo Alpha\npwd\n", "Commands", "ShellCommand", "Name", "echo")]
    [TestCase("powershell", "input.ps1", "Write-Output Alpha\nGet-Date\n", "Commands", "PowerShellCommand", "Name", "Write-Output")]
    [TestCase("sql", "input.sql", "SELECT 1;\nDELETE FROM things;\n", "Statements", "SqlStatement", "Kind", "SELECT")]
    [TestCase("openapi", "input.json", """{"openapi":"3.0.0","info":{"title":"test","version":"1"},"paths":{"/alpha":{"get":{"operationId":"Alpha","responses":{}}},"/beta":{"get":{"operationId":"Beta","responses":{}}}}}""", "Operations", "OpenApiOperation", "OperationId", "Alpha")]
    public void FormatPackages_FilterBeforeReturningRecords(string package, string file, string source,
        string collection, string type, string property, string expected)
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, file), source);
        var script = Path.Combine(_root, "main.cop");
        File.WriteAllText(script, $$"""
            import {{package}}
            let parsed = {{package}}.parse('.')
            predicate selected({{type}}) => {{type}}.{{property}} == '{{expected}}'
            command MAIN = foreach parsed.{{collection}}:selected => '{item.{{property}}}'
            """);
        var diagnostics = new List<string>();
        var result = Engine.Run(_root, target, scriptFiles: [script],
            additionalFeedPaths: [Path.Combine(RepoRoot, "packages")], diagLog: diagnostics.Add);
        Assert.That(result.Errors.Concat(result.ParseErrors), Is.Empty);
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { expected }));
        Assert.That(diagnostics, Has.Some.Contains($"[query-result] {package}.{collection} returned=1"));
        Assert.That(diagnostics.Count(d => d.Contains("[query]")), Is.EqualTo(1));
    }

    [Test]
    public void MultiLanguageUnion_DistributesFilterIntoBothProviders()
    {
        File.WriteAllText(Path.Combine(_root, "input.cs"), "class Alpha {} class Beta {}");
        File.WriteAllText(Path.Combine(_root, "input.py"), "class Alpha:\n    pass\nclass Beta:\n    pass\n");
        var script = Path.Combine(_root, "main.cop");
        File.WriteAllText(script, """
            import code
            import csharp
            import python
            let cb = codebase(csharp.parse(), python.parse())
            predicate selected(Type) => Type.Name == 'Alpha'
            command MAIN = foreach cb.Types:selected => '{item.Name}'
            """);
        var diagnostics = new List<string>();
        var result = Engine.Run(_root, _root, scriptFiles: [script],
            additionalFeedPaths: [Path.Combine(RepoRoot, "packages")], diagLog: diagnostics.Add);
        Assert.That(result.Errors.Concat(result.ParseErrors), Is.Empty);
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "Alpha", "Alpha" }));
        Assert.That(diagnostics, Has.Some.Contains("[query-result] csharp.Types returned=1"));
        Assert.That(diagnostics, Has.Some.Contains("[query-result] python.Types returned=1"));
        Assert.That(diagnostics.Count(d => d.Contains("[query]")), Is.EqualTo(2));
    }

    [TestCase("items.Where(item.Name == 'Alpha')")]
    [TestCase("items.Where((p) => p.Name == 'Alpha')")]
    public void Where_AccumulatesWithoutFetching(string expression)
    {
        var provider = new RecordingQueryProvider();
        var service = Service(provider);
        var evaluator = EvaluatorFor(new CopQueryable("recording",
            new ProviderQuery { RootPath = _root, Collection = "Items" }, service.QueryProvider));
        evaluator.EvalModule(CopParser.Parse($"command MAIN = {expression}"));
        var result = evaluator.RunCommand("MAIN");
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(Names(result), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(provider.Queries.Single().Filter, Is.EqualTo(new StringOpFilter("Name", StringOp.ExactEquals, "Alpha")));
    }

    [Test]
    public void FilterBranches_AreIndependentAndIdenticalQueriesReuseResults()
    {
        var provider = new RecordingQueryProvider();
        var service = Service(provider);
        var source = new CopQueryable("recording", new ProviderQuery { RootPath = _root, Collection = "Items" }, service.QueryProvider);
        var active = source.WithFilter(new PropertyFilter("Active", true));
        var inactive = source.WithFilter(new PropertyFilter("Active", false));
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(Names(active), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(Names(inactive), Is.EqualTo(new[] { "Beta" }));
        Assert.That(Names(source.WithFilter(new PropertyFilter("Active", true))), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(2));
        Assert.That(Names(source), Is.EqualTo(new[] { "Alpha", "Beta" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnknownProviderAccessor_CannotMakeNegationOrDisjunctionDropRecords(bool disjunction)
    {
        FilterExpression filter = new NotFilter(new PropertyFilter("Unsupported", true));
        if (disjunction) filter = new OrFilter([filter, new PropertyFilter("Active", true)]);
        var predicate = FilterCompiler.Compile(filter, new() { ["Active"] = _ => false });
        Assert.That(predicate(new object()), Is.True, "Unsupported trees must remain residual, including inside NOT/OR.");
    }

    [Test]
    public void ExactEqualityAndNegation_PreserveCaseAndNullSemantics()
    {
        var values = new CopList(
        [
            new CopObject(new Dictionary<string, CopValue> { ["Name"] = new CopString("Alpha") }),
            new CopObject(new Dictionary<string, CopValue> { ["Name"] = new CopString("alpha") }),
            new CopObject(new Dictionary<string, CopValue> { ["Name"] = CopNull.Instance })
        ]);
        var source = new CopQueryable("fixture", new(), (_, _) => values);
        var exact = new StringOpFilter("Name", StringOp.ExactEquals, "Alpha");
        Assert.That(Names(source.WithFilter(exact)), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(Names(source.WithFilter(new NotFilter(exact))), Is.EqualTo(new[] { "alpha", "null" }));
    }

    private static Evaluator EvaluatorFor(CopValue items)
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var evaluator = new Evaluator(ffi);
        evaluator.GlobalEnvironment.Define("items", items);
        return evaluator;
    }

    private static string[] Names(CopValue value) => (value switch
    {
        CopQueryable q => q.Enumerate(),
        CopLazyCollection l => l.Enumerate(),
        CopList l => l.Items,
        _ => throw new AssertionException($"Expected collection, got {value.GetType().Name}")
    }).Select(v => v switch
    {
        CopDynamicObject d => d.GetField("Name").Display(),
        CopObject o => o.GetField("Name").Display(),
        _ => throw new AssertionException("Expected object")
    }).ToArray();

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "cop.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}

public sealed class RecordingQueryProvider : DataProvider
{
    public List<ProviderQuery> Queries { get; } = [];

    public override ReadOnlyMemory<byte> GetSchema() => new ProviderSchema
    {
        Types = [new ProviderTypeSchema { Name = "QueryItem", Properties =
        [
            new ProviderPropertySchema { Name = "Name" },
            new ProviderPropertySchema { Name = "Active", Type = "bool" }
        ] }],
        Collections =
        [
            new ProviderCollectionSchema { Name = "Items", ItemType = "QueryItem" },
            new ProviderCollectionSchema { Name = "Extras", ItemType = "QueryItem" }
        ]
    }.ToJson();

    public override object Query(ProviderQuery query)
    {
        Queries.Add(query);
        if (query.RootPath is not null)
            File.AppendAllText(Path.Combine(query.RootPath, "queries.jsonl"),
                JsonSerializer.Serialize(new { query.Collection, Filter = query.Filter is null ? null : FilterExpression.Format(query.Filter) }) + "\n");
        return new Dictionary<string, List<object>>
        {
            ["Items"] =
            [
                new DataObject("QueryItem", new() { ["Name"] = "Alpha", ["Active"] = true }),
                new DataObject("QueryItem", new() { ["Name"] = "Beta", ["Active"] = false })
            ],
            ["Extras"] = [new DataObject("QueryItem", new() { ["Name"] = "Unused", ["Active"] = false })]
        };
    }
}
