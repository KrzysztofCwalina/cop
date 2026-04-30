using Cop.Core;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class ProviderQueryServiceTests
{
    private string _testDir = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "cop_pqs_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Test]
    public void Query_UnknownProvider_ReturnsEmpty()
    {
        var svc = new ProviderQueryService(_testDir);
        var result = svc.Query("unknown", "Types", _testDir);
        Assert.That(result, Is.Empty);
        Assert.That(svc.Warnings, Has.Count.EqualTo(1));
        Assert.That(svc.Warnings[0], Does.Contain("not found"));
    }

    [Test]
    public void Query_NonExistentPath_ReturnsEmpty()
    {
        var svc = new ProviderQueryService(_testDir);
        var provider = new FakeProvider();
        var schema = ProviderSchema.FromJson(provider.GetSchema());
        svc.RegisterProvider("fake", provider, schema);

        var result = svc.Query("fake", "Items", Path.Combine(_testDir, "nonexistent"));
        Assert.That(result, Is.Empty);
        Assert.That(svc.Warnings, Has.Count.EqualTo(1));
        Assert.That(svc.Warnings[0], Does.Contain("does not exist"));
    }

    [Test]
    public void Query_RelativePath_ResolvesAgainstInvocationDirectory()
    {
        var subDir = Path.Combine(_testDir, "sub");
        Directory.CreateDirectory(subDir);

        var svc = new ProviderQueryService(_testDir);
        var provider = new FakeProvider();
        var schema = ProviderSchema.FromJson(provider.GetSchema());
        svc.RegisterProvider("fake", provider, schema);

        var result = svc.Query("fake", "Items", "sub");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(subDir));
    }

    [Test]
    public void Query_SamePath_IsCached()
    {
        var svc = new ProviderQueryService(_testDir);
        var provider = new FakeProvider();
        var schema = ProviderSchema.FromJson(provider.GetSchema());
        svc.RegisterProvider("fake", provider, schema);

        var result1 = svc.Query("fake", "Items", _testDir);
        var result2 = svc.Query("fake", "Items", _testDir);

        Assert.That(ReferenceEquals(result1, result2), Is.True, "Cache should return same instance");
        Assert.That(provider.QueryCount, Is.EqualTo(1), "Provider should only be queried once");
    }

    [Test]
    public void Query_DifferentPaths_AreNotCached()
    {
        var dir1 = Path.Combine(_testDir, "a");
        var dir2 = Path.Combine(_testDir, "b");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        var svc = new ProviderQueryService(_testDir);
        var provider = new FakeProvider();
        var schema = ProviderSchema.FromJson(provider.GetSchema());
        svc.RegisterProvider("fake", provider, schema);

        var result1 = svc.Query("fake", "Items", dir1);
        var result2 = svc.Query("fake", "Items", dir2);

        Assert.That(ReferenceEquals(result1, result2), Is.False);
        Assert.That(provider.QueryCount, Is.EqualTo(2));
    }

    [Test]
    public void Query_ProviderNameIsCaseInsensitive()
    {
        var svc = new ProviderQueryService(_testDir);
        var provider = new FakeProvider();
        var schema = ProviderSchema.FromJson(provider.GetSchema());
        svc.RegisterProvider("Fake", provider, schema);

        var result = svc.Query("fake", "Items", _testDir);
        Assert.That(result, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Fake provider that returns the root path as an item in an "Items" collection.
    /// </summary>
    private class FakeProvider : DataProvider
    {
        public int QueryCount { get; private set; }

        public override DataFormat SupportedFormats => DataFormat.ObjectCollections;

        private static readonly byte[] SchemaBytes = System.Text.Encoding.UTF8.GetBytes("""
            {
                "namespace": "fake",
                "collections": [{ "name": "Items", "type": "FakeItem" }],
                "types": [{ "name": "FakeItem", "fields": [{ "name": "Path", "type": "string" }] }]
            }
            """);

        public override ReadOnlyMemory<byte> GetSchema() => SchemaBytes;

        public override Dictionary<string, List<object>> QueryCollections(ProviderQuery query)
        {
            QueryCount++;
            return new Dictionary<string, List<object>>
            {
                ["Items"] = [query.RootPath!]
            };
        }
    }
}
