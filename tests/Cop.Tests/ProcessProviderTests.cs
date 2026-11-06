using NUnit.Framework;
using Cop.Core;
using Cop.Providers;

namespace Cop.Tests;

/// <summary>
/// Tests for ProcessObjectProvider — the out-of-process provider adapter
/// that communicates with Node.js/Python providers via stdin/stdout.
/// </summary>
[TestFixture]
public class ProcessProviderTests
{
    private static string TestDataDir => Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "testdata");

    [Test]
    public async Task NodeProvider_GetSchema_ReturnsValidSchema()
    {
        if (!IsNodeAvailable()) Assert.Ignore("Node.js not available on this machine");

        await using var provider = new ProcessObjectProvider("node", "mock-provider.js", TestDataDir);
        await provider.InitializeAsync();
        var schemaBytes = provider.GetSchema();
        var schema = ProviderSchema.FromJson(schemaBytes);

        Assert.That(schema.Types, Has.Count.EqualTo(1));
        Assert.That(schema.Types[0].Name, Is.EqualTo("TestItem"));
        Assert.That(schema.Types[0].Properties, Has.Count.EqualTo(2));
        Assert.That(schema.Collections, Has.Count.EqualTo(1));
        Assert.That(schema.Collections[0].Name, Is.EqualTo("Items"));
        Assert.That(schema.Collections[0].ItemType, Is.EqualTo("TestItem"));
    }

    [Test]
    public async Task NodeProvider_Query_ReturnsCollectionData()
    {
        if (!IsNodeAvailable()) Assert.Ignore("Node.js not available on this machine");

        await using var provider = new ProcessObjectProvider("node", "mock-provider.js", TestDataDir);
        var query = new ProviderQuery { RootPath = TestDataDir };
        var resultJson = await provider.QueryAsync(query) as byte[];

        Assert.That(resultJson, Is.Not.Null);
        Assert.That(resultJson!.Length, Is.GreaterThan(0));

        // Verify we can deserialize the JSON
        var text = System.Text.Encoding.UTF8.GetString(resultJson);
        Assert.That(text, Does.Contain("alpha"));
        Assert.That(text, Does.Contain("beta"));
        Assert.That(text, Does.Contain("gamma"));
    }

    [Test]
    public async Task NodeProvider_FullIntegration_SchemaAndQuery()
    {
        if (!IsNodeAvailable()) Assert.Ignore("Node.js not available on this machine");

        await using var provider = new ProcessObjectProvider("node", "mock-provider.js", TestDataDir);
        await provider.InitializeAsync();

        // Get schema
        var schemaBytes = provider.GetSchema();
        var schema = ProviderSchema.FromJson(schemaBytes);

        // Query data
        var query = new ProviderQuery { RootPath = TestDataDir };
        var resultJson = await provider.QueryAsync(query) as byte[];

        // Deserialize using the standard path
        var collections = JsonCollectionDeserializer.Deserialize(resultJson!, schema);
        Assert.That(collections, Does.ContainKey("Items"));
        Assert.That(collections["Items"], Has.Count.EqualTo(3));
    }

    [Test]
    public async Task PythonProvider_GetSchema_ReturnsValidSchema()
    {
        if (!IsPythonAvailable()) Assert.Ignore("Python not available on this machine");

        await using var provider = new ProcessObjectProvider("python", "mock-provider.py", TestDataDir);
        await provider.InitializeAsync();
        var schemaBytes = provider.GetSchema();
        var schema = ProviderSchema.FromJson(schemaBytes);

        Assert.That(schema.Types, Has.Count.EqualTo(1));
        Assert.That(schema.Types[0].Name, Is.EqualTo("TestItem"));
        Assert.That(schema.Collections, Has.Count.EqualTo(1));
        Assert.That(schema.Collections[0].Name, Is.EqualTo("Items"));
    }

    [Test]
    public async Task PythonProvider_Query_ReturnsCollectionData()
    {
        if (!IsPythonAvailable()) Assert.Ignore("Python not available on this machine");

        await using var provider = new ProcessObjectProvider("python", "mock-provider.py", TestDataDir);
        var query = new ProviderQuery { RootPath = TestDataDir };
        var resultJson = await provider.QueryAsync(query) as byte[];

        var text = System.Text.Encoding.UTF8.GetString(resultJson!);
        Assert.That(text, Does.Contain("one"));
        Assert.That(text, Does.Contain("two"));
    }

    [Test]
    public async Task ProcessProvider_InvalidRuntime_ThrowsOnGetSchema()
    {
        await using var provider = new ProcessObjectProvider("nonexistent-runtime-xyz", "script.js", TestDataDir);
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.InitializeAsync());
    }

    [Test]
    public async Task ProcessProvider_MissingScript_ThrowsOnGetSchema()
    {
        if (!IsNodeAvailable()) Assert.Ignore("Node.js not available on this machine");

        await using var provider = new ProcessObjectProvider("node", "nonexistent-script.js", TestDataDir);
        // Node will exit with error when script doesn't exist
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.InitializeAsync());
    }

    [Test]
    public async Task ProcessProvider_Query_ReturnsJsonBytes()
    {
        if (!IsNodeAvailable()) Assert.Ignore("Node.js not available on this machine");
        await using var provider = new ProcessObjectProvider("node", "mock-provider.js", TestDataDir);
        var result = await provider.QueryAsync(new ProviderQuery { RootPath = TestDataDir });
        Assert.That(result, Is.TypeOf<byte[]>());
    }

    [Test]
    public async Task ProcessProvider_Utf8FramesPreserveUnicodeAcrossRequests()
    {
        if (!IsNodeAvailable()) Assert.Ignore("Node.js not available on this machine");
        await using var provider = new ProcessObjectProvider(
            "node", "framing-provider.js", TestDataDir)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var query = new ProviderQuery();
        for (int request = 0; request < 2; request++)
        {
            var bytes = await provider.QueryAsync(query) as byte[];
            Assert.That(bytes, Is.Not.Null);
            Assert.That(System.Text.Encoding.UTF8.GetString(bytes!),
                Is.EqualTo("{\"Value\":\"Za\u017c\u00f3\u0142\u0107 \ud83d\ude00\"}"));
        }
    }

    [Test]
    public async Task ProcessProvider_TimeoutCoversIncompleteBodyAndTerminatesChild()
    {
        if (!IsNodeAvailable()) Assert.Ignore("Node.js not available on this machine");
        await using var provider = new ProcessObjectProvider(
            "node", "framing-provider.js", TestDataDir);
        await provider.InitializeAsync();
        using var schema = System.Text.Json.JsonDocument.Parse(provider.GetSchema());
        using var child = System.Diagnostics.Process.GetProcessById(
            schema.RootElement.GetProperty("pid").GetInt32());
        provider.Timeout = TimeSpan.FromMilliseconds(200);
        var query = new ProviderQuery
        {
            Options = new Dictionary<string, string> { ["mode"] = "stall" }
        };

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await provider.QueryAsync(query).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(child.HasExited, Is.True);
    }

    [Test]
    public async Task ProcessProvider_CallerCancellationTerminatesChildAndKeepsCallerToken()
    {
        if (!IsNodeAvailable()) Assert.Ignore("Node.js not available on this machine");
        await using var provider = new ProcessObjectProvider(
            "node", "framing-provider.js", TestDataDir);
        await provider.InitializeAsync();
        using var schema = System.Text.Json.JsonDocument.Parse(provider.GetSchema());
        using var child = System.Diagnostics.Process.GetProcessById(
            schema.RootElement.GetProperty("pid").GetInt32());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var query = new ProviderQuery
        {
            Options = new Dictionary<string, string> { ["mode"] = "stall" }
        };

        var error = Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await provider.QueryAsync(query, cancellation.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(error!.CancellationToken, Is.EqualTo(cancellation.Token));
        Assert.That(child.HasExited, Is.True);
    }

    [Test]
    public async Task ProcessProvider_OverlappingDisposalsBothWaitForChildExit()
    {
        if (!IsNodeAvailable()) Assert.Ignore("Node.js not available on this machine");
        await using var provider = new ProcessObjectProvider(
            "node", "framing-provider.js", TestDataDir);
        await provider.InitializeAsync();
        using var schema = System.Text.Json.JsonDocument.Parse(provider.GetSchema());
        using var child = System.Diagnostics.Process.GetProcessById(
            schema.RootElement.GetProperty("pid").GetInt32());

        Task first = provider.DisposeAsync().AsTask();
        try
        {
            await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(child.HasExited, Is.True);
        }
        finally
        {
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public void PackageMetadata_IsNodeProvider()
    {
        var meta = new PackageMetadata
        {
            Name = "test", Version = "1.0.0", Title = "Test", Description = "Test", Authors = "test",
            Provider = "node", ProviderEntry = "src/index.js"
        };
        Assert.That(meta.IsNodeProvider, Is.True);
        Assert.That(meta.IsClrProvider, Is.False);
        Assert.That(meta.IsPythonProvider, Is.False);
        Assert.That(meta.IsProvider, Is.True);
    }

    [Test]
    public void PackageMetadata_IsPythonProvider()
    {
        var meta = new PackageMetadata
        {
            Name = "test", Version = "1.0.0", Title = "Test", Description = "Test", Authors = "test",
            Provider = "python", ProviderEntry = "src/main.py"
        };
        Assert.That(meta.IsPythonProvider, Is.True);
        Assert.That(meta.IsClrProvider, Is.False);
        Assert.That(meta.IsNodeProvider, Is.False);
        Assert.That(meta.IsProvider, Is.True);
    }

    [Test]
    public void PackageMetadata_IsProvider_IncludesAllTypes()
    {
        Assert.That(new PackageMetadata { Name = "t", Version = "1.0.0", Title = "T", Description = "T", Authors = "a", Provider = "clr" }.IsProvider, Is.True);
        Assert.That(new PackageMetadata { Name = "t", Version = "1.0.0", Title = "T", Description = "T", Authors = "a", Provider = "node" }.IsProvider, Is.True);
        Assert.That(new PackageMetadata { Name = "t", Version = "1.0.0", Title = "T", Description = "T", Authors = "a", Provider = "python" }.IsProvider, Is.True);
        Assert.That(new PackageMetadata { Name = "t", Version = "1.0.0", Title = "T", Description = "T", Authors = "a", Provider = "" }.IsProvider, Is.False);
        Assert.That(new PackageMetadata { Name = "t", Version = "1.0.0", Title = "T", Description = "T", Authors = "a", Provider = "unknown" }.IsProvider, Is.False);
    }

    private static bool IsNodeAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("node", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool IsPythonAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("python", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}
