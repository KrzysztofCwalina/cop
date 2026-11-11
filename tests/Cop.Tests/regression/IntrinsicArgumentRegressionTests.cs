using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class IntrinsicArgumentRegressionTests
{
    [Test]
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
            var diagnostics = VerifyCommand.CollectDiagnostics([script], root, File.ReadAllText);
            Assert.That(diagnostics.Select(d => d.Message),
                Does.Contain("'ai.judge' argument 2 expects Codebase, got string"));
            Assert.That(VerifyCommand.Execute(script), Is.EqualTo(1));

            File.WriteAllText(script, """
                import ai
                import code
                import csharp
                command MAIN = CHECK(ai.judge('The core must remain general-purpose.', csharp.parse()))
                """);
            diagnostics = VerifyCommand.CollectDiagnostics([script], root, File.ReadAllText);
            Assert.That(diagnostics, Is.Empty, string.Join("\n", diagnostics.Select(d => d.Message)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
