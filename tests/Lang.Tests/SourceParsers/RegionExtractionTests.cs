using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class RegionExtractionTests
{
    private readonly CSharpSourceParser _parser = new();

    [Test]
    public void Parse_SnippetSource_ExtractsAllRegions()
    {
        var source = File.ReadAllText(SamplePath("SnippetSource.cs"));
        var result = _parser.Parse("SnippetSource.cs", source)!;

        Assert.That(result.Regions, Has.Count.EqualTo(4));
    }

    [Test]
    public void Parse_SnippetSource_ExtractsSnippetRegionNames()
    {
        var source = File.ReadAllText(SamplePath("SnippetSource.cs"));
        var result = _parser.Parse("SnippetSource.cs", source)!;
        var names = result.Regions.Select(r => r.Name).ToList();

        Assert.That(names, Does.Contain("Snippet:CreateBlobClient"));
        Assert.That(names, Does.Contain("Snippet:UploadBlob"));
        Assert.That(names, Does.Contain("Snippet:DownloadBlob"));
        Assert.That(names, Does.Contain("NonSnippetRegion"));
    }

    [Test]
    public void Parse_SnippetSource_RegionHasCorrectLineNumbers()
    {
        var source = File.ReadAllText(SamplePath("SnippetSource.cs"));
        var result = _parser.Parse("SnippetSource.cs", source)!;
        var region = result.Regions.First(r => r.Name == "Snippet:CreateBlobClient");

        Assert.That(region.StartLine, Is.GreaterThan(0));
        Assert.That(region.EndLine, Is.GreaterThan(region.StartLine));
    }

    [Test]
    public void Parse_SnippetSource_RegionContentExcludesMarkers()
    {
        var source = File.ReadAllText(SamplePath("SnippetSource.cs"));
        var result = _parser.Parse("SnippetSource.cs", source)!;
        var region = result.Regions.First(r => r.Name == "Snippet:CreateBlobClient");

        Assert.That(region.Content, Does.Contain("new BlobClient"));
        Assert.That(region.Content, Does.Not.Contain("#region"));
        Assert.That(region.Content, Does.Not.Contain("#endregion"));
    }

    [Test]
    public void Parse_SnippetSource_RegionContentHashIsConsistent()
    {
        var source = File.ReadAllText(SamplePath("SnippetSource.cs"));
        var result = _parser.Parse("SnippetSource.cs", source)!;
        var region = result.Regions.First(r => r.Name == "Snippet:CreateBlobClient");

        Assert.That(region.ContentHash, Is.Not.Null.And.Not.Empty);
        Assert.That(region.ContentHash.Length, Is.EqualTo(64)); // SHA256 hex = 64 chars
    }

    [Test]
    public void Parse_NoRegions_ReturnsEmptyList()
    {
        var source = "using System;\npublic class Foo { }";
        var result = _parser.Parse("NoRegions.cs", source)!;

        Assert.That(result.Regions, Is.Empty);
    }

    private static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", fileName);
}
