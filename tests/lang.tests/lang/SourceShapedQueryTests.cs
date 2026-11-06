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
    public async Task ProviderText_PreservesSourceShapeAndReplaysTheSameFilter(string program, string expected)
    {
        var (evaluator, provider, _) = Create();
        var value = (await EvaluateAsync(evaluator, program, cancellationToken: CancellationToken.None));
        Assert.That(provider.Queries, Is.Empty);
        var text = CopQuery(value);
        Assert.That(text, Is.EqualTo(expected));
        Assert.That(provider.Queries, Is.Empty, "Rendering must not enumerate the query.");
        Assert.That((await Names(value)), Is.EqualTo(new[] { "Alpha" }));

        var (replay, replayProvider, _) = Create();
        Assert.That((await Names((await EvaluateAsync(replay, text, cancellationToken: CancellationToken.None)))), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(JsonSerializer.Serialize(replayProvider.Queries.Single()),
            Is.EqualTo(JsonSerializer.Serialize(provider.Queries.Single())));
    }

    [Test]
    public async Task CrossFilePredicates_RetainNamesAndOnlyIncludeRequiredDefinitions()
    {
        var (evaluator, provider, _) = Create();
        await evaluator.EvalModuleAsync(CopParser.Parse("predicate hasPrefix(p) => p.Name:startsWith('A')", "prefix.cop"), cancellationToken: CancellationToken.None);
        await evaluator.EvalModuleAsync(CopParser.Parse("predicate hasSuffix(p) => p.Name:endsWith('a')", "suffix.cop"), cancellationToken: CancellationToken.None);
        var value = (await EvaluateAsync(evaluator, """
            predicate unused(p) => p.Name == 'unused'
            command MAIN = items:hasPrefix:hasSuffix
            """, cancellationToken: CancellationToken.None));
        Assert.That(CopQuery(value), Is.EqualTo("""
            predicate hasPrefix(p) => p.Name:startsWith('A')
            predicate hasSuffix(p) => p.Name:endsWith('a')
            command MAIN = Items:hasPrefix:hasSuffix
            """.Replace("\r\n", "\n")));
        Assert.That(provider.Queries, Is.Empty);
        Assert.That((await Names(value)), Is.EqualTo(new[] { "Alpha" }));
    }

    [Test]
    public async Task LiteralBindings_AreResolvedWithoutForcingTheirThunks()
    {
        var (evaluator, provider, _) = Create();
        var module = CopParser.Parse("""
            let prefix = 'A'
            let alias = prefix
            predicate selected(p) => p.Name:startsWith(alias)
            command MAIN = items:selected
            """);
        evaluator.RegisterDeclarations(module);
        await evaluator.EvalLetBindingsAsync(module, cancellationToken: CancellationToken.None);
        var value = (await evaluator.RunCommandAsync("MAIN", cancellationToken: CancellationToken.None));
        Assert.That(value, Is.TypeOf<CopQueryable>());
        Assert.That(CopQuery(value), Is.EqualTo(
            "predicate selected(p) => p.Name:startsWith('A')\ncommand MAIN = Items:selected"));
        Assert.That(((CopThunk)evaluator.GlobalEnvironment.Lookup("prefix")).IsForced, Is.False);
        Assert.That(((CopThunk)evaluator.GlobalEnvironment.Lookup("alias")).IsForced, Is.False);
        Assert.That(provider.Queries, Is.Empty);
        Assert.That((await Names(value)), Is.EqualTo(new[] { "Alpha" }));
    }

    [TestCase("Alpha")]
    [TestCase("A'\\{literal}\t\n")]
    [TestCase("A'\\literal\t\n")]
    [TestCase("A@literal")]
    public async Task LiteralEscaping_RoundTripsWithoutInterpolation(string literal)
    {
        var (evaluator, provider, _) = Create();
        evaluator.GlobalEnvironment.Define("needle", new CopString(literal));
        var value = (await EvaluateAsync(evaluator, "predicate selected(p) => p.Name == needle\ncommand MAIN = items:selected", cancellationToken: CancellationToken.None));
        var text = CopQuery(value);
        var actual = (await Names(value));
        var (replay, replayProvider, _) = Create();
        Assert.That((await Names((await EvaluateAsync(replay, text, cancellationToken: CancellationToken.None)))), Is.EqualTo(actual));
        Assert.That(((StringOpFilter)provider.Queries.Single().Filter!).Value, Is.EqualTo(literal));
        Assert.That(JsonSerializer.Serialize(replayProvider.Queries.Single().Filter),
            Is.EqualTo(JsonSerializer.Serialize(provider.Queries.Single().Filter)));
    }

    [Test]
    public async Task LongLiteral_IsNotTruncatedAndVerbatimNewlinesRoundTrip()
    {
        await LiteralEscaping_RoundTripsWithoutInterpolation(new string('A', 500) + "'{literal}\\\r\n\t@");
    }

    [TestCase(1e-20, true)]
    [TestCase(1e100, false)]
    [TestCase(-0.0, true)]
    [TestCase(-1.5, true)]
    public async Task NumericConstants_UseParseableInvariantCopLiterals(double threshold, bool matches)
    {
        var queries = new List<ProviderQuery>();
        CopQueryable Items() => new("numbers", new ProviderQuery { Collection = "Items" }, (_, query, cancellationToken) =>
        {
            queries.Add(query);
            return ValueTask.FromResult<CopValue>(new CopList(new[] { ("Alpha", 12), ("Beta", 20) }.Select(row =>
                (CopValue)new CopObject(new Dictionary<string, CopValue>
                {
                    ["Name"] = new CopString(row.Item1), ["Size"] = new CopInt(row.Item2)
                })).ToList()));
        });
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var evaluator = new Evaluator(ffi);
        evaluator.GlobalEnvironment.Define("items", Items());
        evaluator.GlobalEnvironment.Define("Items", Items());
        evaluator.GlobalEnvironment.Define("threshold", new CopNumber(threshold));
        var value = (await EvaluateAsync(evaluator, "command MAIN = items.Where((p) => p.Size > threshold)", cancellationToken: CancellationToken.None));
        var text = CopQuery(value);
        Assert.That(queries, Is.Empty);
        var expected = matches ? new[] { "Alpha", "Beta" } : Array.Empty<string>();
        Assert.That((await Names(value)), Is.EqualTo(expected));
        var original = queries.Single().Filter;
        Assert.That(original, Is.EqualTo(new ComparisonFilter("Size", CompareOp.GreaterThan, threshold)));
        Assert.That((await Names((await EvaluateAsync(evaluator, text, cancellationToken: CancellationToken.None)))), Is.EqualTo(expected));
        Assert.That(queries.Last().Filter, Is.EqualTo(original));
    }

    [Test]
    public async Task NestedPredicates_IncludeTheirDependenciesInSourceOrder()
    {
        var (evaluator, _, _) = Create();
        var value = (await EvaluateAsync(evaluator, """
            predicate prefix(p) => p.Name:startsWith('A')
            predicate suffix(p) => p.Name:endsWith('a')
            predicate selected(p) => prefix && suffix
            command MAIN = items:selected
            """, cancellationToken: CancellationToken.None));
        Assert.That(CopQuery(value), Is.EqualTo("""
            predicate prefix(p) => p.Name:startsWith('A')
            predicate suffix(p) => p.Name:endsWith('a')
            predicate selected(p) => prefix && suffix
            command MAIN = Items:selected
            """.Replace("\r\n", "\n")));
        Assert.That((await Names(value)), Is.EqualTo(new[] { "Alpha" }));
    }

    [Test]
    public async Task ChangedClosureBindings_DoNotOverwriteEarlierQuerySnapshots()
    {
        var (evaluator, _, _) = Create();
        evaluator.GlobalEnvironment.Define("needle", new CopString("Alpha"));
        var first = (await EvaluateAsync(evaluator, "predicate selected(p) => p.Name == needle\ncommand MAIN = items:selected", cancellationToken: CancellationToken.None));
        evaluator.GlobalEnvironment.Define("needle", new CopString("Beta"));
        evaluator.GlobalEnvironment.Define("previous", first);
        var combined = (await EvaluateAsync(evaluator, "command MAIN = previous:selected", cancellationToken: CancellationToken.None));
        var text = CopQuery(combined);
        Assert.That(text, Is.EqualTo("""
            predicate selected(p) => p.Name == 'Alpha'
            predicate selected__2(p) => p.Name == 'Beta'
            command MAIN = Items:selected:selected__2
            """.Replace("\r\n", "\n")));
        Assert.That((await Names(first)), Is.EqualTo(new[] { "Alpha" }));
        Assert.That((await Names(combined)), Is.Empty);
        var (replay, _, _) = Create();
        Assert.That((await Names((await EvaluateAsync(replay, text, cancellationToken: CancellationToken.None)))), Is.Empty);
    }

    [Test]
    public async Task PredicateName_CannotShadowTheResolvedCollectionBinding()
    {
        var (evaluator, _, _) = Create();
        var value = (await EvaluateAsync(evaluator, "predicate Items(p) => p.Active\ncommand MAIN = items:Items", cancellationToken: CancellationToken.None));
        var text = CopQuery(value);
        Assert.That(text, Is.EqualTo("predicate Items__2(p) => p.Active\ncommand MAIN = Items:Items__2"));
        var (replay, _, _) = Create();
        Assert.That((await Names((await EvaluateAsync(replay, text, cancellationToken: CancellationToken.None)))), Is.EqualTo(new[] { "Alpha" }));
    }

    [Test]
    public async Task UnsupportedProgrammaticFilter_ReportsUnavailableSourceWithoutChangingExecution()
    {
        var (evaluator, provider, logs) = Create();
        var query = ((CopQueryable)evaluator.GlobalEnvironment.Lookup("items"))
            .WithFilter(new StringOpFilter("Name", StringOp.Same, "al_pha"));
        Assert.That(() => query.CopQuery, Throws.TypeOf<NotSupportedException>());
        Assert.That((await Names(query)), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(logs, Has.Some.StartsWith("[query-cop-unavailable]"));
        Assert.That(provider.Queries.Single().Filter, Is.EqualTo(new StringOpFilter("Name", StringOp.Same, "al_pha")));
    }

    [Test]
    public async Task UnknownThunk_IsNotForcedForPlanningOrDiagnostics()
    {
        var (evaluator, provider, logs) = Create();
        var forced = 0;
        evaluator.GlobalEnvironment.Define("needle", new CopThunk(() =>
        {
            forced++;
            return new CopString("Alpha");
        }));
        var value = (await EvaluateAsync(evaluator, "predicate selected(p) => p.Name == needle\ncommand MAIN = items:selected", cancellationToken: CancellationToken.None));
        Assert.That(forced, Is.Zero);
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(logs, Has.Some.Contains("[query-local]"));
        Assert.That((await Names(value)), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(forced, Is.EqualTo(1));
        Assert.That(provider.Queries.Single().Filter, Is.Null);
    }

    [Test]
    public async Task UnresolvedPropertyArgument_RemainsLocalAndDoesNotFilterTheStringAsABoolean()
    {
        var (evaluator, provider, _) = Create();
        var forced = 0;
        evaluator.GlobalEnvironment.Define("needle", new CopThunk(() =>
        {
            forced++;
            return new CopString("A");
        }));
        var value = (await EvaluateAsync(evaluator, "command MAIN = items:Name:startsWith(needle)", cancellationToken: CancellationToken.None));
        Assert.That(forced, Is.Zero);
        Assert.That(provider.Queries, Is.Empty);
        Assert.That((await Names(value)), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(forced, Is.EqualTo(1));
        Assert.That(provider.Queries.Single().Filter, Is.Null);
    }

    [Test]
    public void UnknownPredicate_IsNeverTreatedAsAnUnfilteredPropertyProxy()
    {
        var (evaluator, _, _) = Create();
        Assert.That(async () => await EvaluateAsync(evaluator, "command MAIN = items:doesNotExist", cancellationToken: CancellationToken.None),
            Throws.TypeOf<CopEvaluationException>().With.Message.Contains("Unknown predicate 'doesNotExist'"));
    }

    [Test]
    public async Task LocalRemainder_IsSeparateFromTheProviderQuery()
    {
        var (evaluator, provider, logs) = Create();
        var value = (await EvaluateAsync(evaluator, """
            predicate selected(p) => p.Name.Length == 5
            command MAIN = ((items:Name:startsWith('A')):selected).Count
            """, cancellationToken: CancellationToken.None));
        Assert.That((await value.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("1"));
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
    public async Task SourceMetadata_DoesNotChangeWirePayloadOrCacheIdentity()
    {
        var (evaluator, provider, logs) = Create();
        var first = (await EvaluateAsync(evaluator, "command MAIN = items:Name:equals('Alpha')", cancellationToken: CancellationToken.None));
        var second = (await EvaluateAsync(evaluator, "command MAIN = items.Where((p) => p.Name:equals('Alpha'))", cancellationToken: CancellationToken.None));
        Assert.That(CopQuery(first), Is.Not.EqualTo(CopQuery(second)));
        Assert.That((await Names(first)), Is.EqualTo(new[] { "Alpha" }));
        Assert.That((await Names(second)), Is.EqualTo(new[] { "Alpha" }));
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
    public async Task Union_DistributesSourceShapedFiltersToEachTarget()
    {
        var (evaluator, provider, logs) = Create();
        var secondRoot = Path.Combine(_root, "second");
        Directory.CreateDirectory(secondRoot);
        var service = new ProviderQueryService(_root, diagLog: logs.Add);
        service.RegisterProvider("recording", provider, ProviderSchema.FromJson(provider.GetSchema()));
        evaluator.GlobalEnvironment.Define("other",
            ((CopObject)service.CreateValue("recording", new ProviderQuery { RootPath = secondRoot })).GetField("Items"));
        var value = (await EvaluateAsync(evaluator, """
            predicate selected(p) => p.Name == 'Alpha' && p.Active
            command MAIN = (items + other):selected
            """, cancellationToken: CancellationToken.None));
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(() => CopQuery(value), Throws.TypeOf<NotSupportedException>());
        Assert.That((await Names(value)), Is.EqualTo(new[] { "Alpha", "Alpha" }));
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

    private static async Task<CopValue> EvaluateAsync(Evaluator evaluator, string program, CancellationToken cancellationToken = default)
    {
        await evaluator.EvalModuleAsync(CopParser.Parse(program), cancellationToken: cancellationToken);
        return (await evaluator.RunCommandAsync("MAIN", cancellationToken: cancellationToken));
    }

    private static string CopQuery(CopValue value)
    {
        Assert.That(value, Is.TypeOf<CopQueryable>());
        return ((CopQueryable)value).CopQuery;
    }

    private static async Task<string[]> Names(CopValue value) => await (value switch
    {
        CopQueryable query => query.EnumerateAsync(cancellationToken: CancellationToken.None),
        CopLazyCollection lazy => lazy.EnumerateAsync(cancellationToken: CancellationToken.None),
        CopList list => list.Items.ToAsyncEnumerable(),
        _ => throw new AssertionException($"Expected a collection, got {value.GetType().Name}")
    }).Select(async (Cop.Lang.Interpreter.CopValue item, CancellationToken asyncCancellationToken17381) => item switch
    {
        CopDynamicObject record => (await (await record.GetFieldAsync("Name", cancellationToken: asyncCancellationToken17381)).DisplayAsync(cancellationToken: asyncCancellationToken17381)),
        CopObject record => (await record.GetField("Name").DisplayAsync(cancellationToken: asyncCancellationToken17381)),
        _ => throw new AssertionException($"Expected a record, got {item.GetType().Name}")
    }).ToArrayAsync();
}
