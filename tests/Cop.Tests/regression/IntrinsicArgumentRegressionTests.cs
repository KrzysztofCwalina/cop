using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class IntrinsicArgumentRegressionTests
{
    [Test]
    [Explicit("Issue #61: verify does not check intrinsic argument types - remove when fixed")]
    [Category("PendingFix")]
    public void Issue061_VerifyRejectsIncompatibleIntrinsicArguments()
    {
        var repo = TestContext.CurrentContext.TestDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "cop.sln")))
            repo = Path.GetDirectoryName(repo);
        Assert.That(repo, Is.Not.Null);
        var root = Path.Combine(repo!, "tests", "behavior", "issue061-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "main.cop");
            File.WriteAllText(script, """
                import ai
                import code
                command MAIN = CHECK(ai.judge('The core must remain general-purpose.', 'source text'))
                """);
            Assert.That(VerifyCommand.Execute(script), Is.EqualTo(1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
