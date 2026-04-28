using Cop.Providers.Markdown;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class MarkdownParserTests
{
    [Test]
    public void Parse_ExtractsAtxHeadings()
    {
        var source = """
            # Title
            Some text
            ## Subtitle
            More text
            ### Level 3
            """;
        var result = MarkdownParser.Parse(source);

        Assert.That(result.Headings, Has.Count.EqualTo(3));
        Assert.That(result.Headings[0].Text, Is.EqualTo("Title"));
        Assert.That(result.Headings[0].Level, Is.EqualTo(1));
        Assert.That(result.Headings[0].Line, Is.EqualTo(1));
        Assert.That(result.Headings[1].Text, Is.EqualTo("Subtitle"));
        Assert.That(result.Headings[1].Level, Is.EqualTo(2));
        Assert.That(result.Headings[2].Text, Is.EqualTo("Level 3"));
        Assert.That(result.Headings[2].Level, Is.EqualTo(3));
    }

    [Test]
    public void Parse_IgnoresHeadingsInsideFences()
    {
        var source = """
            # Real Heading
            ```
            # Not a heading
            ## Also not a heading
            ```
            ## Another Real Heading
            """;
        var result = MarkdownParser.Parse(source);

        Assert.That(result.Headings, Has.Count.EqualTo(2));
        Assert.That(result.Headings[0].Text, Is.EqualTo("Real Heading"));
        Assert.That(result.Headings[1].Text, Is.EqualTo("Another Real Heading"));
    }

    [Test]
    public void Parse_ExtractsInlineLinks()
    {
        var source = """
            Check out [Google](https://google.com) and [GitHub](https://github.com).
            Also see [docs](./docs/readme.md).
            """;
        var result = MarkdownParser.Parse(source);

        Assert.That(result.Links, Has.Count.EqualTo(3));
        Assert.That(result.Links[0].Url, Is.EqualTo("https://google.com"));
        Assert.That(result.Links[0].Text, Is.EqualTo("Google"));
        Assert.That(result.Links[0].Line, Is.EqualTo(1));
        Assert.That(result.Links[1].Url, Is.EqualTo("https://github.com"));
        Assert.That(result.Links[2].Url, Is.EqualTo("./docs/readme.md"));
    }

    [Test]
    public void Parse_IgnoresImageLinks()
    {
        var source = """
            ![alt text](image.png)
            [real link](https://example.com)
            """;
        var result = MarkdownParser.Parse(source);

        Assert.That(result.Links, Has.Count.EqualTo(1));
        Assert.That(result.Links[0].Url, Is.EqualTo("https://example.com"));
    }

    [Test]
    public void Parse_IgnoresLinksInsideFences()
    {
        var source = """
            [before](https://before.com)
            ```
            [inside fence](https://inside.com)
            ```
            [after](https://after.com)
            """;
        var result = MarkdownParser.Parse(source);

        Assert.That(result.Links, Has.Count.EqualTo(2));
        Assert.That(result.Links[0].Url, Is.EqualTo("https://before.com"));
        Assert.That(result.Links[1].Url, Is.EqualTo("https://after.com"));
    }

    [Test]
    public void Parse_BuildsSections()
    {
        var source = """
            # Introduction
            Some intro text.
            ## Details
            Detail content here.
            # Conclusion
            Final thoughts.
            """;
        var result = MarkdownParser.Parse(source);

        Assert.That(result.Sections, Has.Count.EqualTo(3));

        Assert.That(result.Sections[0].Heading, Is.EqualTo("Introduction"));
        Assert.That(result.Sections[0].Level, Is.EqualTo(1));
        Assert.That(result.Sections[0].StartLine, Is.EqualTo(1));
        Assert.That(result.Sections[0].Content, Does.Contain("Some intro text"));

        Assert.That(result.Sections[1].Heading, Is.EqualTo("Details"));
        Assert.That(result.Sections[1].Level, Is.EqualTo(2));

        Assert.That(result.Sections[2].Heading, Is.EqualTo("Conclusion"));
    }

    [Test]
    public void Parse_EmptyDocument_ReturnsEmptyCollections()
    {
        var result = MarkdownParser.Parse("");

        Assert.That(result.Headings, Is.Empty);
        Assert.That(result.Links, Is.Empty);
        Assert.That(result.FenceBlocks, Is.Empty);
        Assert.That(result.Sections, Is.Empty);
    }

    [Test]
    public void Parse_FenceBlockWithLanguageAndTag()
    {
        var source = """
            ```csharp Snippet:Example
            var x = 1;
            ```
            """;
        var result = MarkdownParser.Parse(source);

        Assert.That(result.FenceBlocks, Has.Count.EqualTo(1));
        Assert.That(result.FenceBlocks[0].Language, Is.EqualTo("csharp"));
        Assert.That(result.FenceBlocks[0].Tag, Is.EqualTo("Snippet:Example"));
        Assert.That(result.FenceBlocks[0].Content, Is.EqualTo("var x = 1;"));
    }

    [Test]
    public void Parse_FenceBlockWithLanguageOnly()
    {
        var source = """
            ```python
            print("hello")
            ```
            """;
        var result = MarkdownParser.Parse(source);

        Assert.That(result.FenceBlocks, Has.Count.EqualTo(1));
        Assert.That(result.FenceBlocks[0].Language, Is.EqualTo("python"));
        Assert.That(result.FenceBlocks[0].Tag, Is.Null);
    }

    [Test]
    public void Parse_FenceBlockWithNoLanguage()
    {
        var source = """
            ```
            plain text
            ```
            """;
        var result = MarkdownParser.Parse(source);

        Assert.That(result.FenceBlocks, Has.Count.EqualTo(1));
        Assert.That(result.FenceBlocks[0].Language, Is.Null);
        Assert.That(result.FenceBlocks[0].Tag, Is.Null);
    }

    [Test]
    public void Parse_MultipleFenceBlocks()
    {
        var source = """
            # Code Examples

            ```csharp
            var x = 1;
            ```

            ```python
            x = 1
            ```

            ```javascript
            let x = 1;
            ```
            """;
        var result = MarkdownParser.Parse(source);

        Assert.That(result.FenceBlocks, Has.Count.EqualTo(3));
        Assert.That(result.FenceBlocks[0].Language, Is.EqualTo("csharp"));
        Assert.That(result.FenceBlocks[1].Language, Is.EqualTo("python"));
        Assert.That(result.FenceBlocks[2].Language, Is.EqualTo("javascript"));
    }

    [Test]
    public void Parse_MultipleLinksOnSameLine()
    {
        var source = "See [A](https://a.com) and [B](https://b.com) for more.";
        var result = MarkdownParser.Parse(source);

        Assert.That(result.Links, Has.Count.EqualTo(2));
        Assert.That(result.Links[0].Url, Is.EqualTo("https://a.com"));
        Assert.That(result.Links[1].Url, Is.EqualTo("https://b.com"));
    }

    [Test]
    public void Parse_HeadingLevels1Through6()
    {
        var source = """
            # H1
            ## H2
            ### H3
            #### H4
            ##### H5
            ###### H6
            """;
        var result = MarkdownParser.Parse(source);

        Assert.That(result.Headings, Has.Count.EqualTo(6));
        for (int i = 0; i < 6; i++)
            Assert.That(result.Headings[i].Level, Is.EqualTo(i + 1));
    }

    [Test]
    public void Parse_ContentHashIsDeterministic()
    {
        var source = """
            ```csharp
            var x = 1;
            ```
            """;
        var result1 = MarkdownParser.Parse(source);
        var result2 = MarkdownParser.Parse(source);

        Assert.That(result1.FenceBlocks[0].ContentHash, Is.EqualTo(result2.FenceBlocks[0].ContentHash));
        Assert.That(result1.FenceBlocks[0].ContentHash.Length, Is.EqualTo(64));
    }
}
