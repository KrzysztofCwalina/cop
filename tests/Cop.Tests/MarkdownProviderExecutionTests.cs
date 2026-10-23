using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class MarkdownProviderExecutionTests
{
    private string _root = "";
    private string _packages = "";

    [SetUp]
    public void SetUp()
    {
        var repo = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(repo, "cop.sln"))) repo = Path.GetDirectoryName(repo)!;
        _packages = Path.Combine(repo, "packages");
        _root = Path.Combine(repo, "tests", "behavior", "md-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "rules.md"), "# Group\n## Rule\nFirst paragraph.\n\nSecond paragraph.\n");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    [TestCase("")]
    [TestCase("'rules.md'")]
    public void Parse_DefaultDirectoryAndExplicitFileExposeBothTreesAndFlatCollections(string argument)
    {
        File.WriteAllText(Path.Combine(_root, "main.cop"), $$"""
            import markdown
            let parsed = markdown.parse({{argument}})
            command MAIN = {
                foreach parsed.Documents => '{item.File.Path}|{item.Kind}|{item.Children.Count}'
                foreach parsed.Headings => '{item.Level}|{item.Text}'
            }
            """);
        var result = Engine.Run(_root, _root, additionalFeedPaths: [_packages]);
        Assert.That(result.ParseErrors, Is.Empty);
        Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(output => output.Message),
            Is.EqualTo(new[] { "rules.md|document|1", "1|Group", "2|Rule" }));
    }

    [Test]
    public void Parse_DirectoryKeepsDocumentsSeparateAndOrdered()
    {
        File.WriteAllText(Path.Combine(_root, "a.md"), "# Other\n## Rule\nOther policy.\n");
        File.WriteAllText(Path.Combine(_root, "main.cop"), """
            import markdown
            command MAIN = foreach markdown.parse().Documents
                => '{item.File.Path}|{item.Children.Count}'
            """);
        var result = Engine.Run(_root, _root, additionalFeedPaths: [_packages]);
        Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(output => output.Message),
            Is.EqualTo(new[] { "a.md|1", "rules.md|1" }));
    }

    [Test]
    public void Parse_MissingFileIsFatalNotAnEmptySuccess()
    {
        File.WriteAllText(Path.Combine(_root, "main.cop"), """
            import markdown
            command MAIN = foreach markdown.parse('missing.md').Documents => item.Text
            """);
        var result = Engine.Run(_root, _root, additionalFeedPaths: [_packages]);
        Assert.That(result.HasFatalErrors, Is.True);
        Assert.That(string.Join("\n", result.Errors), Does.Contain("Markdown path not found"));
        Assert.That(result.Outputs, Is.Empty);
    }
}
