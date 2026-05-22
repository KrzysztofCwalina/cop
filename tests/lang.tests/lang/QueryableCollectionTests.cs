using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;
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
        var queryable = new CopQueryableCollection("json", query, svc);

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
        var original = new CopQueryableCollection("json", query, svc);

        var modified = original.WithFilter(new PropertyFilter("Active", true));

        Assert.That(original.AccumulatedFilter, Is.Null);
        Assert.That(modified.AccumulatedFilter, Is.Not.Null);
    }

    [Test]
    public void Materialize_CallsQueryServiceWithFilter()
    {
        var svc = new FakeQueryService();
        var query = new ProviderQuery { RootPath = "/data.json" };
        var queryable = new CopQueryableCollection("json", query, svc);

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
        var queryable = new CopQueryableCollection("json", query, svc);

        var items = queryable.Enumerate().ToList();

        Assert.That(items, Has.Count.EqualTo(2));
        Assert.That(items[0].Display(), Is.EqualTo("item1"));
    }

    [Test]
    public void CoerceToEnumerable_MaterializesQueryable()
    {
        // CopQueryableCollection should work anywhere a collection is expected
        var svc = new FakeQueryService(new CopList([new CopString("a")]));
        var queryable = new CopQueryableCollection("json", new ProviderQuery { RootPath = "/t" }, svc);

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
        var queryable = new CopQueryableCollection("json", new ProviderQuery { RootPath = "/t" }, svc);

        var typeName = TypeValidator.GetActualTypeName(queryable);
        Assert.That(typeName, Is.EqualTo("collection"));
    }

    private class FakeQueryService : IProviderQueryService
    {
        private readonly CopValue _result;
        public int QueryCount { get; private set; }
        public string? LastProviderName { get; private set; }
        public ProviderQuery? LastQuery { get; private set; }

        public FakeQueryService(CopValue? result = null)
        {
            _result = result ?? new CopList([]);
        }

        public List<object> Query(string providerName, string collectionName, string pathOverride)
        {
            QueryCount++;
            return [];
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
