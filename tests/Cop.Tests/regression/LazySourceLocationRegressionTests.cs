using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class LazySourceLocationRegressionTests
{
    [Test]
    public void Issue063_ToErrorResolvesLazySourceLocationWithoutPriorFieldReads()
    {
        var repo = TestContext.CurrentContext.TestDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "cop.sln")))
            repo = Path.GetDirectoryName(repo);
        Assert.That(repo, Is.Not.Null);
        var root = Path.Combine(repo!, "tests", "behavior", "issue063-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "source");
        var scripts = Path.Combine(root, "checks");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(scripts);
        try
        {
            File.WriteAllText(Path.Combine(target, "Example.cs"), "public class Example { }\n");
            File.WriteAllText(Path.Combine(scripts, "main.cop"), """
                import code
                import csharp
                let cb = csharp.parse()
                let findings = cb.Types:toError('problem')
                command MAIN = CHECK(findings)
                """);

            var result = Engine.Run(scripts, target, additionalFeedPaths: [Path.Combine(repo!, "packages")]);

            Assert.That(result.ParseErrors, Is.Empty);
            Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message),
                Is.EqualTo(new[] { "Example.cs(1): error: problem" }));
            Assert.That(result.ExitCode, Is.EqualTo(1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
