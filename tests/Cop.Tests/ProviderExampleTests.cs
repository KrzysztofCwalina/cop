using Cop.Core;
using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class ProviderExampleTests
{
    [Test]
    public void SamplePackageDeclaresItsProviderAssembly()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "cop.sln")))
            directory = Path.GetDirectoryName(directory);
        Assert.That(directory, Is.Not.Null, "Could not find cop.sln.");

        PackageMetadata? metadata = PackageMetadata.LoadFromDirectoryOrDefault(
            Path.Combine(directory!, "examples", "providers", "sample-provider", "package"));

        Assert.That(metadata, Is.Not.Null);
        Assert.That(metadata!.Name, Is.EqualTo("sample"));
        Assert.That(metadata.Provider, Is.EqualTo("clr"));
        Assert.That(metadata.ProviderEntry, Is.EqualTo("SampleProvider.SampleProvider"));
        Assert.That(metadata.ProviderAssembly, Is.EqualTo("sample-provider.dll"));
    }

    [Test]
    public async Task SampleProviderReturnsExactWidgetData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cop-provider-example-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "alpha.widget"), "name=Alpha\ncategory=ui\nsize=42\n");
            File.WriteAllText(Path.Combine(directory, "beta.widget"), "name=Beta\nsize=7\n");
            DataProvider provider = new SampleProvider.SampleProvider();

            for (int run = 0; run < 2; run++)
            {
                object? result = await provider.QueryAsync(new ProviderQuery
                {
                    RootPath = directory,
                    Collection = "Widgets"
                });
                Assert.That(result, Is.TypeOf<Dictionary<string, List<object>>>());
                var collections = (Dictionary<string, List<object>>)result!;
                Assert.That(collections.Keys, Is.EqualTo(new[] { "Widgets" }));
                Assert.That(collections["Widgets"].Cast<DataObject>().Select(widget => (
                    (string?)widget.GetField("Name"),
                    (string?)widget.GetField("Category"),
                    (long)widget.GetField("Size")!,
                    (string?)widget.GetField("FilePath"))).OrderBy(widget => widget.Item1),
                    Is.EqualTo(new[]
                    {
                        ("Alpha", "ui", 42L, "alpha.widget"),
                        ("Beta", "uncategorized", 7L, "beta.widget")
                    }));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SampleProviderPreservesCallerCancellation()
    {
        DataProvider provider = new SampleProvider.SampleProvider();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException? error = Assert.ThrowsAsync<OperationCanceledException>(
            async () => await provider.QueryAsync(new ProviderQuery(), cancellation.Token));

        Assert.That(error?.CancellationToken, Is.EqualTo(cancellation.Token));
    }
}
