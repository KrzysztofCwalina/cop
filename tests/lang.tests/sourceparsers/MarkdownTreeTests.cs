using Cop.Core;
using Cop.Lang;
using Cop.Providers.Markdown;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class MarkdownTreeTests
{
    [Test]
    public void Parse_GroupsCompleteMultilineSectionsWithoutMergingSiblingRules()
    {
        var root = MarkdownParser.Parse("""
            # Layering
            Production code only.

            ## Keep the core independent
            First paragraph spans
            two physical lines.

            Second paragraph explains exceptions.

            ### Examples
            Supporting example.

            ## Keep storage separate
            Another complete requirement.

            # Performance
            ## Avoid blocking
            Background work is exempt.
            """).Root;

        Assert.That(root.Children.Select(n => n.Text), Is.EqualTo(new[] { "Layering", "Performance" }));
        var group = root.Children[0];
        Assert.That(group.Children.Select(n => n.Kind), Is.EqualTo(new[] { "paragraph", "heading", "heading" }));
        var rule = group.Children[1];
        Assert.That(rule.Children.Select(n => n.Kind), Is.EqualTo(new[] { "paragraph", "paragraph", "heading" }));
        Assert.That(rule.Children[0].Text, Is.EqualTo("First paragraph spans\ntwo physical lines."));
        Assert.That(rule.Children[1].Text, Is.EqualTo("Second paragraph explains exceptions."));
        Assert.That(rule.Level, Is.EqualTo(2));
        Assert.That(rule.StartLine, Is.EqualTo(4));
        Assert.That(rule.EndLine, Is.EqualTo(12));
        Assert.That(rule.Content, Does.Contain("Second paragraph explains exceptions.")
            .And.Contain("### Examples").And.Not.Contain("Keep storage separate"));
        Assert.That(group.Children[2].Text, Is.EqualTo("Keep storage separate"));
    }

    [Test]
    public void Parse_PreservesNestedListsCodeTablesQuotesAndComments()
    {
        var root = MarkdownParser.Parse("""
            # Policy
            ## One rule
            - First item
              continues here.

              Additional explanation.
              - Nested item
            - Second item

            ~~~~csharp
            ## Not a rule
            var text = "{literal}";
            ~~~~

            | Name | Meaning |
            | --- | --- |
            | Core | Reusable |

            > Quoted context.
            > More context.

            <!--
            ## Also not a rule
            -->

            ***
            """).Root;
        var rule = root.Children.Single().Children.Single();
        Assert.That(rule.Children.Select(n => n.Kind),
            Is.EqualTo(new[] { "list", "codeBlock", "table", "quote", "html", "thematicBreak" }));
        var list = rule.Children[0];
        Assert.That(list.Children, Has.Count.EqualTo(2));
        Assert.That(list.Children[0].Children.Select(n => n.Kind),
            Is.EqualTo(new[] { "paragraph", "paragraph", "list" }));
        Assert.That(list.Children[0].Children[0].Text, Is.EqualTo("First item\ncontinues here."));
        Assert.That(list.Children[0].Children[2].Children.Single().Children.Single().Text, Is.EqualTo("Nested item"));
        Assert.That(rule.Children[1].Text, Is.EqualTo("## Not a rule\nvar text = \"{literal}\";"));
        Assert.That(rule.Children[2].Content, Does.Contain("| Core | Reusable |"));
        Assert.That(rule.Children[3].Children.Single().Text, Is.EqualTo("Quoted context.\nMore context."));
        Assert.That(rule.Children[4].Content, Does.Contain("## Also not a rule"));
        Assert.That(rule.Content, Does.Contain("Additional explanation.").And.Contain("~~~csharp"));
    }

    [Test]
    public void Parse_SupportsSetextHeadingsAndKeepsUnterminatedFencesOpaque()
    {
        var root = MarkdownParser.Parse("Group\r\n=====\r\n\r\nRule\r\n----\r\nBody.\r\n\r\n```md\r\n## Example\r\n").Root;
        var group = root.Children.Single();
        Assert.That(group.Level, Is.EqualTo(1));
        var rule = group.Children.Single();
        Assert.That(rule.Level, Is.EqualTo(2));
        Assert.That(rule.Text, Is.EqualTo("Rule"));
        Assert.That(rule.Children.Select(n => n.Kind), Is.EqualTo(new[] { "paragraph", "codeBlock" }));
        Assert.That(rule.Children[1].Text, Is.EqualTo("## Example\n"));
        Assert.That(rule.Content, Is.EqualTo("Rule\n----\nBody.\n\n```md\n## Example\n"));
    }

    [Test]
    public void Parse_EmptyDocumentHasOneEmptyRoot()
    {
        var root = MarkdownParser.Parse("").Root;
        Assert.That(root.Kind, Is.EqualTo("document"));
        Assert.That(root.Children, Is.Empty);
        Assert.That(root.Content, Is.EqualTo(""));
        Assert.That(root.StartLine, Is.EqualTo(1));
        Assert.That(root.EndLine, Is.EqualTo(1));
    }

    [Test]
    public void Parse_IndentedMarkdownExamplesRemainCodeNotSectionsOrLists()
    {
        var root = MarkdownParser.Parse(
            "# Rules\n## Real rule\nBody.\n\n\t## Example heading\n\tExample body.\n\n    - Example list\n    ```md\n    ## Another example\n    ```\n").Root;
        var rule = root.Children.Single().Children.Single();
        Assert.That(rule.Text, Is.EqualTo("Real rule"));
        Assert.That(rule.Children.Select(node => node.Kind),
            Is.EqualTo(new[] { "paragraph", "codeBlock" }));
        Assert.That(rule.Children[1].Text,
            Is.EqualTo("## Example heading\nExample body.\n\n- Example list\n```md\n## Another example\n```\n"));
    }

    [Test]
    public async Task Provider_ParsesOneFileAndMaterializesTreesAndExistingCollections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cop-md-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "policy.md");
            File.WriteAllText(path, "# Rules\n## First\nSee [guide](guide.md).\n\n```cs\nclass Example {}\n```\n");
            var provider = new MarkdownProvider();
            var collections = (Dictionary<string, List<object>>)provider.Query(new ProviderQuery { RootPath = path })!;
            var raw = new DataObject("Object", collections.ToDictionary(pair => pair.Key, pair => (object?)pair.Value));
            var parsed = (DataObject)(await provider.GetFunctions()!["toContent"]([new List<object?> { raw }]))!;

            Assert.That(parsed.TypeName, Is.EqualTo("MarkdownContent"));
            var documents = (List<object?>)parsed.GetField("Documents")!;
            Assert.That(documents, Has.Count.EqualTo(1));
            var root = (DataObject)documents[0]!;
            Assert.That(root.GetField("Kind"), Is.EqualTo("document"));
            var file = (DataObject)root.GetField("File")!;
            Assert.That(file.GetField("Path"), Is.EqualTo("policy.md"));
            Assert.That(file.GetField("FullPath"), Is.EqualTo(path));
            Assert.That(file.GetField("SourceRoot"), Is.EqualTo(directory));
            var headings = (List<object?>)parsed.GetField("Headings")!;
            Assert.That(headings, Has.Count.EqualTo(2));
            Assert.That(((DataObject)headings[1]!).GetField("Text"), Is.EqualTo("First"));
            Assert.That(parsed.GetField("Links"), Has.Count.EqualTo(1));
            Assert.That(parsed.GetField("FenceBlocks"), Has.Count.EqualTo(1));
            Assert.That(parsed.GetField("Sections"), Has.Count.EqualTo(2));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void Provider_MissingFileFailsRatherThanReturningAnEmptyTree()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".md");
        Assert.That(() => new MarkdownProvider().Query(new ProviderQuery { RootPath = path }),
            Throws.TypeOf<FileNotFoundException>().With.Message.Contains("Markdown path not found"));
    }
}
