using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class QueryableCollectionTests
{
    [Test]
    public void WithFilter_AccumulatesFilters()
    {
        var svc = new FakeQueryService();
        var query = new ProviderQuery { RootPath = "/test.json" };
        var queryable = new CopQueryable("json", query, svc.QueryProvider);

        var f1 = new PropertyFilter("Active", true);
        var q2 = queryable.WithFilter(f1);

        Assert.That(q2.AccumulatedFilter, Is.SameAs(f1));

        var f2 = new StringOpFilter("Name", StringOp.StartsWith, "A");
        var q3 = q2.WithFilter(f2);

        Assert.That(q3.AccumulatedFilter, Is.InstanceOf<AndFilter>());
        var and = (AndFilter)q3.AccumulatedFilter!;
        Assert.That(and.Conditions, Has.Count.EqualTo(2));
    }

    [Test]
    public void WithFilter_IsImmutable()
    {
        var svc = new FakeQueryService();
        var query = new ProviderQuery { RootPath = "/test.json" };
        var original = new CopQueryable("json", query, svc.QueryProvider);

        var modified = original.WithFilter(new PropertyFilter("Active", true));

        Assert.That(original.AccumulatedFilter, Is.Null);
        Assert.That(modified.AccumulatedFilter, Is.Not.Null);
    }

    [Test]
    public void Materialize_CallsQueryServiceWithFilter()
    {
        var svc = new FakeQueryService();
        var query = new ProviderQuery { RootPath = "/data.json" };
        var queryable = new CopQueryable("json", query, svc.QueryProvider);

        var filter = new PropertyFilter("Enabled", true);
        var filtered = queryable.WithFilter(filter);

        filtered.Materialize();

        Assert.That(svc.LastProviderName, Is.EqualTo("json"));
        Assert.That(svc.LastQuery, Is.Not.Null);
        Assert.That(svc.LastQuery!.RootPath, Is.EqualTo("/data.json"));
        Assert.That(svc.LastQuery.Filter, Is.SameAs(filter));
    }

    [Test]
    public void Enumerate_ReturnsItemsFromMaterialization()
    {
        var svc = new FakeQueryService(new CopList([
            new CopString("item1"),
            new CopString("item2")
        ]));
        var query = new ProviderQuery { RootPath = "/test.json" };
        var queryable = new CopQueryable("json", query, svc.QueryProvider);

        var items = queryable.Enumerate().ToList();

        Assert.That(items, Has.Count.EqualTo(2));
        Assert.That(items[0].Display(), Is.EqualTo("item1"));
    }

    [Test]
    public void CoerceToEnumerable_MaterializesQueryable()
    {
        // CopQueryable should work anywhere a collection is expected
        var svc = new FakeQueryService(new CopList([new CopString("a")]));
        var queryable = new CopQueryable("json", new ProviderQuery { RootPath = "/t" }, svc.QueryProvider);

        // Display shows it's queryable (not yet materialized)
        Assert.That(queryable.Display(), Does.Contain("queryable"));

        // Enumerate materializes
        var items = queryable.Enumerate().ToList();
        Assert.That(items, Has.Count.EqualTo(1));
        Assert.That(svc.QueryCount, Is.EqualTo(1));
    }

    [Test]
    public void TypeValidator_AcceptsQueryableAsCollection()
    {
        var svc = new FakeQueryService();
        var queryable = new CopQueryable("json", new ProviderQuery { RootPath = "/t" }, svc.QueryProvider);

        var typeName = TypeValidator.GetActualTypeName(queryable);
        Assert.That(typeName, Is.EqualTo("collection"));
    }

    [Test]
    public void EvalFilter_UserPredicateOverQueryable_PushesItsBodyNotItsName()
    {
        // Regression (#33): translate the body, not a nonexistent 'canVote' provider field.
        CopValue Person(string name, int age) => new CopObject(
            new Dictionary<string, CopValue> { ["name"] = new CopString(name), ["age"] = new CopInt(age) });
        var svc = new FakeQueryService(new CopList([Person("Ada", 36), Person("Bo", 12)]));
        var queryable = new CopQueryable("json", new ProviderQuery { RootPath = "/people.json" }, svc.QueryProvider);

        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var module = CopParser.Parse("""
            predicate canVote(p) => p.age:greaterThan(17)
            command main = people:canVote.Count
            """, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        evaluator.GlobalEnvironment.Define("people", queryable);
        evaluator.EvalModule(module);
        var result = evaluator.RunCommand("main");

        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(1), "only Ada (age 36) should pass canVote");
        Assert.That(svc.LastQuery?.Filter, Is.EqualTo(new ComparisonFilter("age", CompareOp.GreaterThan, 17)),
            "Translate the predicate body, never a fictitious 'canVote' field.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PackagePredicate_OnQueryableProperty_PreservesMatchingObjects(bool negated)
    {
        var blob = new CopObject(new Dictionary<string, CopValue> { ["Name"] = new CopString("BlobClient") });
        var queue = new CopObject(new Dictionary<string, CopValue> { ["Name"] = new CopString("QueueClient") });
        var svc = new FakeQueryService(new CopList([blob, queue]));
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var evaluator = new Evaluator(ffi);
        var clients = new CopQueryable("fixture", new ProviderQuery { RootPath = "." }, svc.QueryProvider);
        evaluator.GlobalEnvironment.Define("names", new CopQueryableProperty(clients, "Name"));
        var not = negated ? "!" : "";
        evaluator.EvalModule(CopParser.Parse(
            $"command MAIN = names:{not}sameAs('blob_client')", "test.cop"));

        var result = evaluator.RunCommand("MAIN");

        var items = result switch
        {
            CopList list => list.Items,
            CopLazyCollection lazy => lazy.Enumerate().ToArray(),
            _ => throw new AssertionException($"Expected a collection, got {result.GetType().Name}.")
        };
        Assert.That(items, Is.EqualTo(new[] { negated ? queue : blob }));
        Assert.That(svc.LastQuery!.Filter, Is.Null);
    }

    private class FakeQueryService
    {
        private readonly CopValue _result;
        public int QueryCount { get; private set; }
        public string? LastProviderName { get; private set; }
        public ProviderQuery? LastQuery { get; private set; }

        public FakeQueryService(CopValue? result = null)
        {
            _result = result ?? new CopList([]);
        }

        public CopValue QueryProvider(string providerName, ProviderQuery query)
        {
            QueryCount++;
            LastProviderName = providerName;
            LastQuery = query;
            return _result;
        }
    }
}
