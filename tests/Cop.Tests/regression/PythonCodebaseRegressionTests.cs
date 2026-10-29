using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class PythonCodebaseRegressionTests
{
    [Test]
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
                command MAIN = print('{cb.Files.Count}:{cb.Types.Count}')
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

    [Test]
    public void Issue083_ClassCompoundStatementTerminatesWithExpectedClassMember()
    {
        var repo = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));
        var root = Path.Combine(Path.GetTempPath(), "cop-issue083-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var checks = Path.Combine(root, "checks");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(checks);
        try
        {
            File.WriteAllText(Path.Combine(target, "example.py"), "class C:\n    if True:\n        value = 1\n    def method(self):\n        useful()\n");
            File.WriteAllText(Path.Combine(checks, "main.cop"), """
                import python
                let cb = python.parse()
                command MAIN = print('{cb.Types.Count}:{cb.Types[0].Name}:{cb.Types[0].Methods[0].Name}:{cb.Calls.Count}')
                """);
            var execution = Task.Run(() => Engine.Run(checks, target, additionalFeedPaths: [Path.Combine(repo, "packages")]));
            Assert.That(execution.Wait(TimeSpan.FromSeconds(15)), Is.True, "Valid class-level control flow must never hang parsing.");
            var result = execution.Result;
            Assert.That(result.ParseErrors, Is.Empty);
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "1:C:method:1" }));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
