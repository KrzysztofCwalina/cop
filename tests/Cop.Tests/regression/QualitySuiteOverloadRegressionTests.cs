using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class QualitySuiteOverloadRegressionTests
{
    [Test]
    [Explicit("Issue #132: overloaded quality-suite delegation fails at runtime after verification - remove when fixed")]
    [Category("PendingFix")]
    public async Task Issue132_OverloadedSuiteCompositionExecutesItsSelectedArity()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "cop.sln")))
            directory = directory.Parent;
        string repo = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        string root = Path.Combine(repo, ".test-output", "overload-regression-" + Guid.NewGuid().ToString("N"));
        string library = Path.Combine(root, "feed", "quality-overload-regression");
        string project = Path.Combine(root, "program");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(library, "src"));
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(target);
        try
        {
            string original = Path.Combine(repo, "packages", "checks", "ai-slop-checks");
            File.WriteAllText(Path.Combine(library, "cop.json"),
                File.ReadAllText(Path.Combine(original, "cop.json"))
                    .Replace("\"ai-slop-checks\"", "\"quality-overload-regression\"", StringComparison.Ordinal));
            string source = File.ReadAllText(Path.Combine(original, "src", "ai-slop-checks.cop"))
                .Replace("scopedCodeQualityChecks", "codeQualityChecks", StringComparison.Ordinal);
            source = source[..source.IndexOf("## Compatibility name for aiSlopChecks", StringComparison.Ordinal)];
            source = source.Replace("aiSlopChecks", "codeQualityChecks", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(library, "src", "code-quality-checks.cop"), source);
            File.Copy(Path.Combine(original, "src", "operational-safety.cop"),
                Path.Combine(library, "src", "operational-safety.cop"));
            File.WriteAllText(Path.Combine(target, "Input.cs"), "class Input { void Run() { Assert.True(true); } }");
            File.WriteAllText(Path.Combine(project, "main.cop"), """
                import code
                import csharp
                import quality-overload-regression
                let cb = csharp.parse()
                command MAIN = codeQualityChecks(cb).Select(item.Check)
                """);
            var result = await Engine.RunAsync(project, target, new EngineRunOptions
            {
                AdditionalFeedPaths = [Path.Combine(root, "feed"), Path.Combine(repo, "packages")]
            });
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(new[] { "constantAssertions" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
