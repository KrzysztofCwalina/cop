using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class PackageNamespaceRegressionTests
{
    [Test]
    public void Issue062_QualifiedPurePackageFunctionExecutes()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "issue062-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "packages", "fixture", "src");
        var scripts = Path.Combine(root, "checks");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(scripts);
        try
        {
            File.WriteAllText(Path.Combine(root, "packages", "fixture", "cop.json"),
                """{"name":"fixture","version":"1.0.0"}""");
            File.WriteAllText(Path.Combine(package, "fixture.cop"), "export function answer() : int => 42");
            File.WriteAllText(Path.Combine(scripts, "main.cop"),
                "import fixture\ncommand MAIN = foreach [fixture.answer()] => '{item}'");

            var result = Engine.Run(scripts, root, additionalFeedPaths: [Path.Combine(root, "packages")]);

            Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(new[] { "42" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
