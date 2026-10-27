using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class SharedQueryStageTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ThousandDistinctBranches_EvaluateTheSharedFilterOnce(bool combined)
    {
        var (provider, service) = Fixture();
        var source = Source(service);
        var publicTypes = source.WithFilter(Public);
        var branches = Enumerable.Range(0, 1000).Select(i => combined
            ? source.WithFilter(new AndFilter([Public, Name($"Type{i:D3}")]))
            : publicTypes.WithFilter(Name($"Type{i:D3}"))).ToArray();
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(provider.PublicReads, Is.Zero);
        var expected = Enumerable.Range(0, 1000).Select(i => i < 100 && i % 20 == 0 ? 1 : 0).ToArray();
        Assert.That(branches.Select(b => b.Enumerate().Count()), Is.EqualTo(expected));
        Assert.That(branches.Select(b => b.Enumerate().Count()), Is.EqualTo(expected));
        Assert.That(provider.PublicReads, Is.EqualTo(100), "Measure predicate work, not just file parsing.");
        Assert.That(provider.Produced, Is.EqualTo(100));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
        Assert.That(provider.Queries[0].Filter, Is.EqualTo(Public));
    }

    [Test]
    public void SequentialDiscovery_PromotesThePrefixAfterTheSecondDistinctBranch()
    {
        var (provider, service) = Fixture();
        var publicTypes = Source(service).WithFilter(Public);
        for (int i = 0; i < 1000; i++)
            Assert.That(publicTypes.WithFilter(Name($"Type{i:D3}")).Enumerate().Count(),
                Is.EqualTo(i < 100 && i % 20 == 0 ? 1 : 0));
        Assert.That(provider.Queries, Has.Count.EqualTo(2));
        Assert.That(provider.PublicReads, Is.EqualTo(200));
    }

    [Test]
    public void IndependentlyComposedPrefixes_ShareWithinTheSameExecution()
    {
        var (provider, service) = Fixture();
        var first = Source(service).WithFilter(Public).WithFilter(Name("Type000"));
        var second = Source(service).WithFilter(Public).WithFilter(Name("Type020"));
        Assert.That(Names(first), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
        Assert.That(provider.PublicReads, Is.EqualTo(100));
    }

    [Test]
    public void PartialConsumers_ResumeTheSamePrefixWithoutProducingLaterRows()
    {
        var (provider, service) = Fixture();
        provider.ThrowAt = 21;
        var publicTypes = Source(service).WithFilter(Public);
        var first = publicTypes.WithFilter(Name("Type000"));
        var second = publicTypes.WithFilter(Name("Type020"));
        Assert.That(Names(first.Enumerate().Take(1)), Is.EqualTo(new[] { "Type000" }));
        Assert.That(provider.Produced, Is.EqualTo(1));
        Assert.That(provider.PublicReads, Is.EqualTo(1));
        Assert.That(Names(second.Enumerate().Take(1)), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.Produced, Is.EqualTo(21));
        Assert.That(provider.PublicReads, Is.EqualTo(21));
        Assert.That(Names(first.Enumerate().Take(1)), Is.EqualTo(new[] { "Type000" }));
        Assert.That(provider.Produced, Is.EqualTo(21));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
    }

    [Test]
    public void SharedFailure_IsNotTurnedIntoAnEmptySuccessfulResult()
    {
        var (provider, service) = Fixture();
        provider.ThrowAt = 21;
        var publicTypes = Source(service).WithFilter(Public);
        var first = publicTypes.WithFilter(Name("Type040"));
        var second = publicTypes.WithFilter(Name("Type060"));
        Assert.Throws<InvalidOperationException>(() => first.Enumerate().ToArray());
        int reads = provider.PublicReads;
        provider.ThrowAt = null;
        Assert.Throws<InvalidOperationException>(() => second.Enumerate().ToArray());
        Assert.That(provider.PublicReads, Is.EqualTo(reads));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
    }

    [Test]
    public void SharedQueryInitializationFailure_IsMemoizedToo()
    {
        var (provider, service) = Fixture();
        provider.ThrowOnQuery = true;
        var visible = Source(service).WithFilter(Public);
        var first = visible.WithFilter(Name("Type000"));
        var second = visible.WithFilter(Name("Type020"));
        Assert.Throws<InvalidOperationException>(() => first.Enumerate().ToArray());
        provider.ThrowOnQuery = false;
        Assert.Throws<InvalidOperationException>(() => second.Enumerate().ToArray());
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
        Assert.That(provider.Produced, Is.Zero);
    }

    [Test]
    public void AnAlreadyConsumedPrefix_IsReusedByItsFirstChild()
    {
        var (provider, service) = Fixture();
        var publicTypes = Source(service).WithFilter(Public);
        Assert.That(Names(publicTypes.Enumerate().Take(1)), Is.EqualTo(new[] { "Type000" }));
        var child = publicTypes.WithFilter(Name("Type020"));
        Assert.That(Names(child.Enumerate().Take(1)), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.Produced, Is.EqualTo(21));
        Assert.That(provider.PublicReads, Is.EqualTo(21));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
    }

    [Test]
    public void AUniqueBranch_StillPushesItsCompleteFilterToTheProvider()
    {
        var (provider, service) = Fixture();
        var branch = Source(service).WithFilter(Public).WithFilter(Name("Type020"));
        Assert.That(Names(branch), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
        Assert.That(FilterExpression.Format(provider.Queries[0].Filter!),
            Is.EqualTo("IsPublic, Name:equalsExact('Type020')"));
    }

    [TestCase("root")]
    [TestCase("exclusions")]
    [TestCase("options")]
    [TestCase("collection")]
    public void DifferentSourceIdentities_DoNotShareAFilteredView(string difference)
    {
        var (provider, service) = Fixture();
        var otherQuery = new ProviderQuery
        {
            RootPath = difference == "root" ? Path.GetFullPath("other") : Path.GetFullPath("."),
            ExcludedDirectories = difference == "exclusions" ? new HashSet<string> { "generated" } : null,
            Options = difference == "options" ? new Dictionary<string, string> { ["mode"] = "other" } : null
        };
        var first = Source(service).WithFilter(Public).WithFilter(Name("Type000"));
        var second = Source(service, otherQuery, difference == "collection" ? "Other" : "Items")
            .WithFilter(Public).WithFilter(Name("Type020"));
        Assert.That(Names(first), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InterpreterPreservesNamedPredicatesAndCapturedValues(bool colon)
    {
        var (provider, service) = Fixture();
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var evaluator = new Evaluator(ffi);
        evaluator.GlobalEnvironment.Define("items", Source(service));
        evaluator.EvalModule(CopParser.Parse($$"""
            predicate visible(row) => row.IsPublic
            let firstName = 'Type000'
            let otherName = 'Type020'
            predicate first(row) => row.Name == firstName
            predicate other(row) => row.Name == otherName
            let publicItems = items:visible
            command MAIN = [{{(colon ? "publicItems:first, publicItems:other" : "publicItems.Where(item.Name == firstName), publicItems.Where(item.Name == otherName)")}}]
            """));
        var plans = (CopList)evaluator.RunCommand("MAIN");
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(Names(plans.Items[0]), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(plans.Items[1]), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.PublicReads, Is.EqualTo(100));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
    }

    [Test]
    public void DistinctPredicateValues_DoNotBecomeTheSamePrefix()
    {
        var (provider, service) = Fixture();
        var source = Source(service);
        var first = source.WithFilter(new PropertyFilter("IsPublic", true)).WithFilter(Name("Type000"));
        var second = source.WithFilter(new PropertyFilter("IsPublic", false)).WithFilter(Name("Type001"));
        Assert.That(Names(first), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Type001" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(2));
    }

    [Test]
    public void PotentiallyFailingFilters_AreNotHoistedAheadOfTheirOriginalPrefix()
    {
        var (_, service) = Fixture();
        var source = Source(service);
        var empty = source.WithFilter(Name("Absent"));
        var first = empty.WithFilter(new StringOpFilter("Name", StringOp.Matches, "["));
        var second = empty.WithFilter(Name("Type020"));
        Assert.That(Names(first), Is.Empty);
        Assert.That(Names(second), Is.Empty);
    }

    [Test]
    public void InterleavedConsumers_ReuseAndResumeTheSameRows()
    {
        var (provider, service) = Fixture();
        var visible = Source(service).WithFilter(Public);
        var first = visible.WithFilter(new StringOpFilter("Name", StringOp.StartsWith, "Type0"));
        var second = visible.WithFilter(new StringOpFilter("Name", StringOp.EndsWith, "0"));
        using var a = first.Enumerate().GetEnumerator();
        using var b = second.Enumerate().GetEnumerator();
        foreach (var name in new[] { "Type000", "Type020", "Type040", "Type060", "Type080" })
        {
            Assert.That(a.MoveNext(), Is.True);
            Assert.That(b.MoveNext(), Is.True);
            Assert.That(Names([a.Current, b.Current]), Is.EqualTo(new[] { name, name }));
        }
        Assert.That(a.MoveNext(), Is.False);
        Assert.That(b.MoveNext(), Is.False);
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
        Assert.That(provider.PublicReads, Is.EqualTo(100));
        Assert.That(provider.Produced, Is.EqualTo(100));
    }

    [Test]
    public void CompoundOrderedPrefixes_AreSharedWithoutReorderingConditions()
    {
        var (provider, service) = Fixture();
        var source = Source(service);
        var first = source.WithFilter(new AndFilter(
            [Public, new StringOpFilter("Name", StringOp.StartsWith, "Type0"), Name("Type000")]));
        var second = source.WithFilter(new AndFilter(
            [Public, new StringOpFilter("Name", StringOp.StartsWith, "Type0"), Name("Type020")]));
        Assert.That(Names(first), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
        Assert.That(FilterExpression.Format(provider.Queries[0].Filter!),
            Is.EqualTo("IsPublic, Name:startsWith('Type0')"));
        Assert.That(provider.PublicReads, Is.EqualTo(100));
    }

    [Test]
    public void ReversedConditions_AreNotTreatedAsAnIdenticalPrefix()
    {
        var (provider, service) = Fixture();
        var source = Source(service);
        var first = source.WithFilter(new AndFilter([Public, Name("Type000")]));
        var second = source.WithFilter(new AndFilter([Name("Type000"), Public]));
        Assert.That(Names(first), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Type000" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(2));
        Assert.That(provider.PublicReads, Is.EqualTo(101));
    }

    [Test]
    public void QueryablesWithoutScalarSchema_DoNotAutomaticallySharePrefixes()
    {
        var (provider, service) = Fixture();
        var source = new CopQueryable("counting",
            new ProviderQuery { RootPath = Path.GetFullPath("."), Collection = "Items" }, service.QueryProvider);
        var first = source.WithFilter(Public).WithFilter(Name("Type000"));
        var second = source.WithFilter(Public).WithFilter(Name("Type020"));
        Assert.That(Names(first), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(2));
        Assert.That(provider.PublicReads, Is.EqualTo(200));
    }

    [Test]
    public void LookupBudget_DoesNotEvictAStageStillUsedByLiveBranches()
    {
        var (provider, service) = Fixture();
        var scope = new QueryReuseScope();
        var source = Source(service);
        source.SetReuseScope(scope);
        var visible = source.WithFilter(Public);
        var branches = Enumerable.Range(0, 1000).Select(i => visible.WithFilter(Name($"Type{i:D3}"))).ToArray();
        Assert.That(scope.Count, Is.EqualTo(QueryReuseScope.Capacity));
        Assert.That(Names(branches[0]), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(branches[20]), Is.EqualTo(new[] { "Type020" }));
        Assert.That(Names(branches[999]), Is.Empty);
        Assert.That(provider.PublicReads, Is.EqualTo(100));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
        GC.KeepAlive(branches);
    }

    [Test]
    public void SeparateExecutions_DoNotShareRuntimeResults()
    {
        var (provider, firstService) = Fixture();
        var secondService = new ProviderQueryService(Path.GetFullPath("."));
        secondService.RegisterProvider("counting", provider, ProviderSchema.FromJson(provider.GetSchema()));
        var first = Source(firstService).WithFilter(Public).WithFilter(Name("Type000"));
        var second = Source(secondService).WithFilter(Public).WithFilter(Name("Type020"));
        Assert.That(Names(first), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(2));
        Assert.That(provider.PublicReads, Is.EqualTo(200));
    }

    [Test]
    public void SeparateProviderBindings_DoNotShareResults()
    {
        var (provider, service) = Fixture();
        service.RegisterProvider("other", provider, ProviderSchema.FromJson(provider.GetSchema()));
        var first = Source(service).WithFilter(Public).WithFilter(Name("Type000"));
        var other = (CopQueryable)((CopObject)service.CreateValue("other",
            new ProviderQuery { RootPath = Path.GetFullPath(".") })).GetField("Items");
        var second = other.WithFilter(Public).WithFilter(Name("Type020"));
        Assert.That(Names(first), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(2));
        Assert.That(provider.PublicReads, Is.EqualTo(200));
    }

    [Test]
    public void ChangingACapturedLiteral_DoesNotChangeAnAlreadyBuiltBranch()
    {
        var (provider, service) = Fixture();
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var evaluator = new Evaluator(ffi);
        evaluator.GlobalEnvironment.Define("items", Source(service));
        evaluator.GlobalEnvironment.Define("needle", new CopString("Type000"));
        evaluator.EvalModule(CopParser.Parse("""
            predicate visible(row) => row.IsPublic
            predicate named(row) => row.Name == needle
            command FIRST = items:visible:named
            command SECOND = items:visible:named
            """));
        var first = evaluator.RunCommand("FIRST");
        evaluator.GlobalEnvironment.Define("needle", new CopString("Type020"));
        var second = evaluator.RunCommand("SECOND");
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(Names(first), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
        Assert.That(provider.PublicReads, Is.EqualTo(100));
    }

    [Test]
    public void Diagnostics_ReportTheActualPushedPrefixAndTheLocalRemainder()
    {
        var (provider, _) = Fixture();
        var logs = new List<string>();
        var service = new ProviderQueryService(Path.GetFullPath("."), diagLog: logs.Add);
        service.RegisterProvider("counting", provider, ProviderSchema.FromJson(provider.GetSchema()));
        var visible = Source(service).WithFilter(Public);
        var first = visible.WithFilter(Name("Type000"));
        var second = visible.WithFilter(Name("Type020"));
        Assert.That(Names(first), Is.EqualTo(new[] { "Type000" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Type020" }));
        var pushed = logs.Single(line => line.StartsWith("[query-cop]"));
        Assert.That(pushed, Does.Contain("item.IsPublic == true"));
        Assert.That(pushed, Does.Not.Contain("Type000").And.Not.Contain("Type020"));
        Assert.That(logs.Count(line => line.StartsWith("[query-local]") && line.Contains("shared prefix")), Is.EqualTo(2));
        Assert.That(logs, Has.Some.Contains("Type000").And.Some.Contains("Type020"));
    }

    [Test]
    public void Concatenation_DistributesBranchesEarlyEnoughToShareEachProviderPrefix()
    {
        var (provider, service) = Fixture();
        var union = CopQueryable.Concat(Source(service),
            Source(service, new ProviderQuery { RootPath = Path.GetFullPath("other") }));
        var visible = union.WithFilter(Public);
        var first = visible.WithFilter(Name("Type000"));
        var second = visible.WithFilter(Name("Type020"));
        Assert.That(provider.Queries, Is.Empty);
        Assert.That(Names(first), Is.EqualTo(new[] { "Type000", "Type000" }));
        Assert.That(Names(second), Is.EqualTo(new[] { "Type020", "Type020" }));
        Assert.That(provider.Queries, Has.Count.EqualTo(2));
        Assert.That(provider.PublicReads, Is.EqualTo(200));
    }

    [Test]
    public void Concatenation_AnAlreadyConsumedPrefixRemainsIncrementallyReusable()
    {
        var (provider, service) = Fixture();
        provider.ThrowAt = 21;
        var visible = CopQueryable.Concat(Source(service),
            Source(service, new ProviderQuery { RootPath = Path.GetFullPath("other") })).WithFilter(Public);
        Assert.That(Names(visible.Enumerate().Take(1)), Is.EqualTo(new[] { "Type000" }));
        var child = visible.WithFilter(Name("Type020"));
        Assert.That(Names(child.Enumerate().Take(1)), Is.EqualTo(new[] { "Type020" }));
        Assert.That(provider.PublicReads, Is.EqualTo(21));
        Assert.That(provider.Produced, Is.EqualTo(21));
        Assert.That(provider.Queries, Has.Count.EqualTo(1));
    }

    private static PropertyFilter Public => new("IsPublic", true);
    private static StringOpFilter Name(string value) => new("Name", StringOp.ExactEquals, value);

    private static (CountingProvider Provider, ProviderQueryService Service) Fixture()
    {
        var provider = new CountingProvider();
        var service = new ProviderQueryService(Path.GetFullPath("."));
        service.RegisterProvider("counting", provider, ProviderSchema.FromJson(provider.GetSchema()));
        return (provider, service);
    }

    private static CopQueryable Source(ProviderQueryService service, ProviderQuery? query = null, string collection = "Items") =>
        (CopQueryable)((CopObject)service.CreateValue("counting",
            query ?? new ProviderQuery { RootPath = Path.GetFullPath(".") })).GetField(collection);

    private static string[] Names(CopValue query) => Names(CollectionValues.Enumerate(query));
    private static string[] Names(IEnumerable<CopValue> values) =>
        values.Select(v => ((CopDynamicObject)v).GetField("Name").Display()).ToArray();

    private sealed record Row(string Name, bool IsPublic);

    private sealed class CountingProvider : DataProvider
    {
        public List<ProviderQuery> Queries { get; } = [];
        public int PublicReads { get; private set; }
        public int Produced { get; private set; }
        public int? ThrowAt { get; set; }
        public bool ThrowOnQuery { get; set; }
        public override string QueryResultType => "Fixture";

        public override ReadOnlyMemory<byte> GetSchema() => new ProviderSchema
        {
            Types = [new ProviderTypeSchema { Name = "Item", Properties =
            [
                new ProviderPropertySchema { Name = "Name" },
                new ProviderPropertySchema { Name = "IsPublic", Type = "bool" }
            ] }],
            Collections =
            [
                new ProviderCollectionSchema { Name = "Items", ItemType = "Item" },
                new ProviderCollectionSchema { Name = "Other", ItemType = "Item" }
            ]
        }.ToJson();

        public override RuntimeBindings GetRuntimeBindings() => new()
        {
            ClrTypeMappings = new() { [typeof(Row)] = "Item" },
            Accessors = new()
            {
                ["Item"] = new()
                {
                    ["Name"] = value => ((Row)value).Name,
                    ["IsPublic"] = value => { PublicReads++; return ((Row)value).IsPublic; }
                }
            }
        };

        public override object QueryDeferred(ProviderQuery query)
        {
            Queries.Add(query);
            if (ThrowOnQuery) throw new InvalidOperationException("Provider query initialization failed.");
            return new DeferredProviderResult(new Dictionary<string, IEnumerable<object>>
            {
                [query.Collection!] = Rows()
            });
        }

        private IEnumerable<object> Rows()
        {
            for (int i = 0; i < 100; i++)
            {
                if (ThrowAt == i) throw new InvalidOperationException("Demand reached a later row.");
                Produced++;
                yield return new Row($"Type{i:D3}", i % 20 == 0);
            }
        }
    }
}
