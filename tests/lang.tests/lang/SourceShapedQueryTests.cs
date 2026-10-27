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
public class SourceShapedQueryTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-query-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    [TestCase("command MAIN = items:Active", "command MAIN = Items:Active")]
    [TestCase("command MAIN = items:Name:startsWith('A'):Name:endsWith('a')",
        "command MAIN = Items:Name:startsWith('A'):Name:endsWith('a')")]
    [TestCase("command MAIN = items.Where((p) => p.Name == 'Alpha' && p.Active)",
        "command MAIN = Items.Where((p) => p.Name == 'Alpha' && p.Active)")]
    [TestCase("predicate selected(p) => p.Name == 'Alpha' && p.Active\ncommand MAIN = items:selected",
        "predicate selected(p) => p.Name == 'Alpha' && p.Active\ncommand MAIN = Items:selected")]
    [TestCase("predicate selected(p) => !(p.Name == 'Beta') && (p.Active || p.Name == 'Alpha')\ncommand MAIN = items:selected",
        "predicate selected(p) => !(p.Name == 'Beta') && (p.Active || p.Name == 'Alpha')\ncommand MAIN = Items:selected")]
    public void ProviderText_PreservesSourceShapeAndReplaysTheSameFilter(string program, string expected)
    {
        var (evaluator, provider, _) = Create();
        var value = Evaluate(evaluator, program);
        Assert.That(provider.Queries, Is.Empty);
        var text = CopQuery(value);
        Assert.That(text, Is.EqualTo(expected));
        Assert.That(provider.Queries, Is.Empty, "Rendering must not enumerate the query.");
        Assert.That(Names(value), Is.EqualTo(new[] { "Alpha" }));

        var (replay, replayProvider, _) = Create();
        Assert.That(Names(Evaluate(replay, text)), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(JsonSerializer.Serialize(replayProvider.Queries.Single()),
            Is.EqualTo(JsonSerializer.Serialize(provider.Queries.Single())));
    }

    [Test]
    public void CrossFilePredicates_RetainNamesAndOnlyIncludeRequiredDefinitions()
    {
        var (evaluator, provider, _) = Create();
        evaluator.EvalModule(CopParser.Parse("predicate hasPrefix(p) => p.Name:startsWith('A')", "prefix.cop"));
        evaluator.EvalModule(CopParser.Parse("predicate hasSuffix(p) => p.Name:endsWith('a')", "suffix.cop"));
        var value = Evaluate(evaluator, """
            predicate unused(p) => p.Name == 'unused'
            command MAIN = items:hasPrefix:hasSuffix
            """);
        Assert.That(CopQuery(value), Is.EqualTo("""
            predicate hasPrefix(p) => p.Name:startsWith('A')
            predicate hasSuffix(p) => p.Name:endsWith('a')
            command MAIN = Items:hasPrefix:hasSuffix
            """.Replace("\r\n", "\n")));
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(Names(value), Is.EqualTo(new[] { "Alpha" }));
    }

    [Test]
    public void LiteralBindings_AreResolvedWithoutForcingTheirThunks()
    {
        var (evaluator, provider, _) = Create();
        var module = CopParser.Parse("""
            let prefix = 'A'
            let alias = prefix
            predicate selected(p) => p.Name:startsWith(alias)
            command MAIN = items:selected
            """);
        evaluator.RegisterDeclarations(module);
        evaluator.EvalLetBindings(module);
        var value = evaluator.RunCommand("MAIN");
        Assert.That(value, Is.TypeOf<CopQueryable>());
        Assert.That(CopQuery(value), Is.EqualTo(
            "predicate selected(p) => p.Name:startsWith('A')\ncommand MAIN = Items:selected"));
        Assert.That(((CopThunk)evaluator.GlobalEnvironment.Lookup("prefix")).IsForced, Is.False);
        Assert.That(((CopThunk)evaluator.GlobalEnvironment.Lookup("alias")).IsForced, Is.False);
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(Names(value), Is.EqualTo(new[] { "Alpha" }));
    }

    [TestCase("Alpha")]
    [TestCase("A'\\{literal}\t\n")]
    [TestCase("A'\\literal\t\n")]
    [TestCase("A@literal")]
    public void LiteralEscaping_RoundTripsWithoutInterpolation(string literal)
    {
        var (evaluator, provider, _) = Create();
        evaluator.GlobalEnvironment.Define("needle", new CopString(literal));
        var value = Evaluate(evaluator, "predicate selected(p) => p.Name == needle\ncommand MAIN = items:selected");
        var text = CopQuery(value);
        var actual = Names(value);
        var (replay, replayProvider, _) = Create();
        Assert.That(Names(Evaluate(replay, text)), Is.EqualTo(actual));
        Assert.That(((StringOpFilter)provider.Queries.Single().Filter!).Value, Is.EqualTo(literal));
        Assert.That(JsonSerializer.Serialize(replayProvider.Queries.Single().Filter),
            Is.EqualTo(JsonSerializer.Serialize(provider.Queries.Single().Filter)));
    }

    [Test]
    public void LongLiteral_IsNotTruncatedAndVerbatimNewlinesRoundTrip()
    {
        LiteralEscaping_RoundTripsWithoutInterpolation(new string('A', 500) + "'{literal}\\\r\n\t@");
    }

    [TestCase(1e-20, true)]
    [TestCase(1e100, false)]
    [TestCase(-0.0, true)]
    [TestCase(-1.5, true)]
    public void NumericConstants_UseParseableInvariantCopLiterals(double threshold, bool matches)
    {
        var queries = new List<ProviderQuery>();
        CopQueryable Items() => new("numbers", new ProviderQuery { Collection = "Items" }, (_, query) =>
        {
            queries.Add(query);
            return new CopList(new[] { ("Alpha", 12), ("Beta", 20) }.Select(row =>
                (CopValue)new CopObject(new Dictionary<string, CopValue>
                {
                    ["Name"] = new CopString(row.Item1), ["Size"] = new CopInt(row.Item2)
                })).ToList());
        });
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var evaluator = new Evaluator(ffi);
        evaluator.GlobalEnvironment.Define("items", Items());
        evaluator.GlobalEnvironment.Define("Items", Items());
        evaluator.GlobalEnvironment.Define("threshold", new CopNumber(threshold));
        var value = Evaluate(evaluator, "command MAIN = items.Where((p) => p.Size > threshold)");
        var text = CopQuery(value);
        Assert.That(queries, Is.Empty);
        var expected = matches ? new[] { "Alpha", "Beta" } : Array.Empty<string>();
        Assert.That(Names(value), Is.EqualTo(expected));
        var original = queries.Single().Filter;
        Assert.That(original, Is.EqualTo(new ComparisonFilter("Size", CompareOp.GreaterThan, threshold)));
        Assert.That(Names(Evaluate(evaluator, text)), Is.EqualTo(expected));
        Assert.That(queries.Last().Filter, Is.EqualTo(original));
    }

    [Test]
    public void NestedPredicates_IncludeTheirDependenciesInSourceOrder()
    {
        var (evaluator, _, _) = Create();
        var value = Evaluate(evaluator, """
            predicate prefix(p) => p.Name:startsWith('A')
            predicate suffix(p) => p.Name:endsWith('a')
            predicate selected(p) => prefix && suffix
            command MAIN = items:selected
            """);
        Assert.That(CopQuery(value), Is.EqualTo("""
            predicate prefix(p) => p.Name:startsWith('A')
            predicate suffix(p) => p.Name:endsWith('a')
            predicate selected(p) => prefix && suffix
            command MAIN = Items:selected
            """.Replace("\r\n", "\n")));
        Assert.That(Names(value), Is.EqualTo(new[] { "Alpha" }));
    }

    [Test]
    public void ChangedClosureBindings_DoNotOverwriteEarlierQuerySnapshots()
    {
        var (evaluator, _, _) = Create();
        evaluator.GlobalEnvironment.Define("needle", new CopString("Alpha"));
        var first = Evaluate(evaluator, "predicate selected(p) => p.Name == needle\ncommand MAIN = items:selected");
        evaluator.GlobalEnvironment.Define("needle", new CopString("Beta"));
        evaluator.GlobalEnvironment.Define("previous", first);
        var combined = Evaluate(evaluator, "command MAIN = previous:selected");
        var text = CopQuery(combined);
        Assert.That(text, Is.EqualTo("""
            predicate selected(p) => p.Name == 'Alpha'
            predicate selected__2(p) => p.Name == 'Beta'
            command MAIN = Items:selected:selected__2
            """.Replace("\r\n", "\n")));
        Assert.That(Names(first), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(Names(combined), Is.Empty);
        var (replay, _, _) = Create();
        Assert.That(Names(Evaluate(replay, text)), Is.Empty);
    }

    [Test]
    public void PredicateName_CannotShadowTheResolvedCollectionBinding()
    {
        var (evaluator, _, _) = Create();
        var value = Evaluate(evaluator, "predicate Items(p) => p.Active\ncommand MAIN = items:Items");
        var text = CopQuery(value);
        Assert.That(text, Is.EqualTo("predicate Items__2(p) => p.Active\ncommand MAIN = Items:Items__2"));
        var (replay, _, _) = Create();
        Assert.That(Names(Evaluate(replay, text)), Is.EqualTo(new[] { "Alpha" }));
    }

    [Test]
    public void UnsupportedProgrammaticFilter_ReportsUnavailableSourceWithoutChangingExecution()
    {
        var (evaluator, provider, logs) = Create();
        var query = ((CopQueryable)evaluator.GlobalEnvironment.Lookup("items"))
            .WithFilter(new StringOpFilter("Name", StringOp.Same, "al_pha"));
        Assert.That(() => query.CopQuery, Throws.TypeOf<NotSupportedException>());
        Assert.That(Names(query), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(logs, Has.Some.StartsWith("[query-cop-unavailable]"));
        Assert.That(provider.Queries.Single().Filter, Is.EqualTo(new StringOpFilter("Name", StringOp.Same, "al_pha")));
    }

    [Test]
    public void UnknownThunk_IsNotForcedForPlanningOrDiagnostics()
    {
        var (evaluator, provider, logs) = Create();
        var forced = 0;
        evaluator.GlobalEnvironment.Define("needle", new CopThunk(() =>
        {
            forced++;
            return new CopString("Alpha");
        }));
        var value = Evaluate(evaluator, "predicate selected(p) => p.Name == needle\ncommand MAIN = items:selected");
        Assert.That(forced, Is.Zero);
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(logs, Has.Some.Contains("[query-local]"));
        Assert.That(Names(value), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(forced, Is.EqualTo(1));
        Assert.That(provider.Queries.Single().Filter, Is.Null);
    }

    [Test]
    public void UnresolvedPropertyArgument_RemainsLocalAndDoesNotFilterTheStringAsABoolean()
    {
        var (evaluator, provider, _) = Create();
        var forced = 0;
        evaluator.GlobalEnvironment.Define("needle", new CopThunk(() =>
        {
            forced++;
            return new CopString("A");
        }));
        var value = Evaluate(evaluator, "command MAIN = items:Name:startsWith(needle)");
        Assert.That(forced, Is.Zero);
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(Names(value), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(forced, Is.EqualTo(1));
        Assert.That(provider.Queries.Single().Filter, Is.Null);
    }

    [Test]
    public void UnknownPredicate_IsNeverTreatedAsAnUnfilteredPropertyProxy()
    {
        var (evaluator, _, _) = Create();
        Assert.That(() => Evaluate(evaluator, "command MAIN = items:doesNotExist"),
            Throws.TypeOf<CopEvaluationException>().With.Message.Contains("Unknown predicate 'doesNotExist'"));
    }

    [Test]
    public void LocalRemainder_IsSeparateFromTheProviderQuery()
    {
        var (evaluator, provider, logs) = Create();
        var value = Evaluate(evaluator, """
            predicate selected(p) => p.Name.Length == 5
            command MAIN = ((items:Name:startsWith('A')):selected).Count
            """);
        Assert.That(value.Display(), Is.EqualTo("1"));
        var remote = logs.Single(line => line.StartsWith("[query-cop]"));
        Assert.That(remote, Does.Contain("command MAIN = Items:Name:startsWith('A')"));
        Assert.That(remote, Does.Not.Contain("selected").And.Not.Contain(".Count"));
        Assert.That(logs.Where(line => line.StartsWith("[query-local]")),
            Has.Some.Contains("selected").And.Some.Contains(".Count"));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
        Assert.That(provider.Queries.Single().Filter,
            Is.EqualTo(new StringOpFilter("Name", StringOp.StartsWith, "A")));
    }

    [Test]
    public void SourceMetadata_DoesNotChangeWirePayloadOrCacheIdentity()
    {
        var (evaluator, provider, logs) = Create();
        var first = Evaluate(evaluator, "command MAIN = items:Name:equals('Alpha')");
        var second = Evaluate(evaluator, "command MAIN = items.Where((p) => p.Name:equals('Alpha'))");
        Assert.That(CopQuery(first), Is.Not.EqualTo(CopQuery(second)));
        Assert.That(Names(first), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
        Assert.That(logs.Count(line => line.StartsWith("[query-cop]")), Is.EqualTo(1));
        Assert.That(logs.Where(line => line.StartsWith("[query-local]")),
            Has.Some.Contains("shared query result reused").And.Some.Contains("Items.Where"));
        var build = typeof(ProcessObjectProvider).GetMethod("BuildQueryRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var wire = JsonDocument.Parse((string)build.Invoke(null, [provider.Queries.Single()])!);
        Assert.That(wire.RootElement.GetProperty("params").EnumerateObject().Select(property => property.Name),
            Is.EqualTo(new[] { "rootPath", "collection", "filter", "collectionFilters" }));
    }

    [Test]
    public void Union_DistributesSourceShapedFiltersToEachTarget()
    {
        var (evaluator, provider, logs) = Create();
        var secondRoot = Path.Combine(_root, "second");
        Directory.CreateDirectory(secondRoot);
        var service = new ProviderQueryService(_root, diagLog: logs.Add);
        service.RegisterProvider("recording", provider, ProviderSchema.FromJson(provider.GetSchema()));
        evaluator.GlobalEnvironment.Define("other",
            ((CopObject)service.CreateValue("recording", new ProviderQuery { RootPath = secondRoot })).GetField("Items"));
        var value = Evaluate(evaluator, """
            predicate selected(p) => p.Name == 'Alpha' && p.Active
            command MAIN = (items + other):selected
            """);
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(() => CopQuery(value), Throws.TypeOf<NotSupportedException>());
        Assert.That(Names(value), Is.EqualTo(new[] { "Alpha", "Alpha" }));
        var remote = logs.Where(line => line.StartsWith("[query-cop]")).ToArray();
        Assert.That(remote, Has.Length.EqualTo(2));
        Assert.That(remote.All(line => line.Contains("command MAIN = Items:selected")), Is.True);
        Assert.That(remote.All(line => !line.Contains(".Where(")), Is.True);
        Assert.That(logs, Has.Some.Contains("[query-local]").And.Some.Contains("concatenation"));
        Assert.That(provider.Queries.Select(query => query.RootPath), Is.EqualTo(new[] { _root, secondRoot }));
    }

    private (Evaluator Evaluator, RecordingQueryProvider Provider, List<string> Logs) Create()
    {
        var logs = new List<string>();
        var provider = new RecordingQueryProvider();
        var service = new ProviderQueryService(_root, diagLog: logs.Add);
        service.RegisterProvider("recording", provider, ProviderSchema.FromJson(provider.GetSchema()));
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var evaluator = new Evaluator(ffi);
        var items = ((CopObject)service.CreateValue("recording", new ProviderQuery { RootPath = _root })).GetField("Items");
        evaluator.GlobalEnvironment.Define("items", items);
        evaluator.GlobalEnvironment.Define("Items", items);
        return (evaluator, provider, logs);
    }

    private static CopValue Evaluate(Evaluator evaluator, string program)
    {
        evaluator.EvalModule(CopParser.Parse(program));
        return evaluator.RunCommand("MAIN");
    }

    private static string CopQuery(CopValue value)
    {
        Assert.That(value, Is.TypeOf<CopQueryable>());
        return ((CopQueryable)value).CopQuery;
    }

    private static string[] Names(CopValue value) => (value switch
    {
        CopQueryable query => query.Enumerate(),
        CopLazyCollection lazy => lazy.Enumerate(),
        CopList list => list.Items,
        _ => throw new AssertionException($"Expected a collection, got {value.GetType().Name}")
    }).Select(item => item switch
    {
        CopDynamicObject record => record.GetField("Name").Display(),
        CopObject record => record.GetField("Name").Display(),
        _ => throw new AssertionException($"Expected a record, got {item.GetType().Name}")
    }).ToArray();
}
