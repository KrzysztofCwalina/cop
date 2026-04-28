using Cop.Providers.Markdown;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class FenceBlockExtractionTests
{
    [Test]
    public void Parse_Markdown_ExtractsAllFenceBlocks()
    {
        var source = File.ReadAllText(SamplePath("SnippetReadme.md"));
        var result = MarkdownParser.Parse(source);

        // 3 snippet fences + 1 orphaned + 1 non-snippet python = 5
        Assert.That(result.FenceBlocks, Has.Count.EqualTo(5));
    }

    [Test]
    public void Parse_Markdown_ExtractsFenceLanguages()
    {
        var source = File.ReadAllText(SamplePath("SnippetReadme.md"));
        var result = MarkdownParser.Parse(source);

        var languages = result.FenceBlocks.Select(fb => fb.Language).ToList();
        Assert.That(languages, Does.Contain("C#"));
        Assert.That(languages, Does.Contain("csharp"));
        Assert.That(languages, Does.Contain("python"));
    }

    [Test]
    public void Parse_Markdown_ExtractsSnippetTags()
    {
        var source = File.ReadAllText(SamplePath("SnippetReadme.md"));
        var result = MarkdownParser.Parse(source);

        var tags = result.FenceBlocks.Select(fb => fb.Tag).Where(t => t != null).ToList();
        Assert.That(tags, Does.Contain("Snippet:CreateBlobClient"));
        Assert.That(tags, Does.Contain("Snippet:UploadBlob"));
        Assert.That(tags, Does.Contain("Snippet:DownloadBlob"));
        Assert.That(tags, Does.Contain("Snippet:DeleteBlob"));
    }

    [Test]
    public void Parse_Markdown_NonSnippetFenceHasNoTag()
    {
        var source = File.ReadAllText(SamplePath("SnippetReadme.md"));
        var result = MarkdownParser.Parse(source);

        var pythonFence = result.FenceBlocks.First(fb => fb.Language == "python");
        Assert.That(pythonFence.Tag, Is.Null);
    }

    [Test]
    public void Parse_Markdown_FenceContentExcludesMarkers()
    {
        var source = File.ReadAllText(SamplePath("SnippetReadme.md"));
        var result = MarkdownParser.Parse(source);

        var createFence = result.FenceBlocks.First(fb => fb.Tag == "Snippet:CreateBlobClient");
        Assert.That(createFence.Content, Does.Contain("new BlobClient"));
        Assert.That(createFence.Content, Does.Not.Contain("```"));
    }

    [Test]
    public void Parse_Markdown_FenceBlockHasLineNumbers()
    {
        var source = File.ReadAllText(SamplePath("SnippetReadme.md"));
        var result = MarkdownParser.Parse(source);

        var firstFence = result.FenceBlocks[0];
        Assert.That(firstFence.StartLine, Is.GreaterThan(0));
        Assert.That(firstFence.EndLine, Is.GreaterThan(firstFence.StartLine));
    }

    [Test]
    public void Parse_Markdown_ContentHashIsConsistent()
    {
        var source = File.ReadAllText(SamplePath("SnippetReadme.md"));
        var result = MarkdownParser.Parse(source);

        var fence = result.FenceBlocks.First(fb => fb.Tag == "Snippet:CreateBlobClient");
        Assert.That(fence.ContentHash, Is.Not.Null.And.Not.Empty);
        Assert.That(fence.ContentHash.Length, Is.EqualTo(64));
    }

    [Test]
    public void Parse_NonMarkdown_HasNoFenceBlocks()
    {
        var result = MarkdownParser.Parse("{\"key\": \"value\"}");
        Assert.That(result.FenceBlocks, Is.Empty);
    }

    [Test]
    public void Parse_MatchingContent_HasSameHash()
    {
        // When C# region and markdown fence have identical content, hashes should match
        var csSource = File.ReadAllText(SamplePath("SnippetSource.cs"));
        var csParser = new CSharpSourceParser();
        var csResult = csParser.Parse("SnippetSource.cs", csSource)!;
        var createRegion = csResult.Regions.First(r => r.Name == "Snippet:CreateBlobClient");

        var mdSource = File.ReadAllText(SamplePath("SnippetReadme.md"));
        var mdResult = MarkdownParser.Parse(mdSource);
        var createFence = mdResult.FenceBlocks.First(fb => fb.Tag == "Snippet:CreateBlobClient");

        Assert.That(createFence.ContentHash, Is.EqualTo(createRegion.ContentHash),
            "Identical content in region and fence should produce same hash");
    }

    [Test]
    public void Parse_DifferentContent_HasDifferentHash()
    {
        var csSource = File.ReadAllText(SamplePath("SnippetSource.cs"));
        var csParser = new CSharpSourceParser();
        var csResult = csParser.Parse("SnippetSource.cs", csSource)!;
        var downloadRegion = csResult.Regions.First(r => r.Name == "Snippet:DownloadBlob");

        var mdSource = File.ReadAllText(SamplePath("SnippetReadme.md"));
        var mdResult = MarkdownParser.Parse(mdSource);
        var downloadFence = mdResult.FenceBlocks.First(fb => fb.Tag == "Snippet:DownloadBlob");

        Assert.That(downloadFence.ContentHash, Is.Not.EqualTo(downloadRegion.ContentHash),
            "Different content should produce different hash");
    }

    private static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", fileName);
}
