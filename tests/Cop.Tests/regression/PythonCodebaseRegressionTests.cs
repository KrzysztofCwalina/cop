using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class PythonCodebaseRegressionTests
{
    [Test]
    [Explicit("Issue #60: python.parse returns a collection instead of Codebase - remove when fixed")]
    [Category("PendingFix")]
    public void Issue060_PythonParseReturnsCodebase()
    {
        var repo = TestContext.CurrentContext.TestDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "cop.sln")))
            repo = Path.GetDirectoryName(repo);
        Assert.That(repo, Is.Not.Null);
        var root = Path.Combine(repo!, "tests", "behavior", "issue060-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "source");
        var scripts = Path.Combine(root, "checks");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(scripts);
        try
        {
            File.WriteAllText(Path.Combine(target, "example.py"), "class Example:\n    pass\n");
            File.WriteAllText(Path.Combine(scripts, "main.cop"), """
                import code
                import python
                let cb = python.parse()
                command MAIN = '{cb.Files.Count}:{cb.Types.Count}'
                """);
            var result = Engine.Run(scripts, target, additionalFeedPaths: [Path.Combine(repo!, "packages")]);
            Assert.That(result.ParseErrors, Is.Empty);
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(new[] { "1:1" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
