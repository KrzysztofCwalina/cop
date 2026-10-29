using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

public partial class ToolchainCliIntegrationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Issue094_ResolvedCompatibilityIsInformational_NotARepairWarning(bool diagnostics)
    {
        var feed = WriteRecoveredFixtureFeed();
        File.WriteAllText(_script, $"feed '{feed}'\nimport fixture\ncommand MAIN = print(selected)");

        var args = new List<string> { _script, "-t", _root, "--no-color" };
        if (diagnostics) args.Add("-d");
        var result = await Run(args.ToArray());

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput.Trim(), Is.EqualTo("RECOVERED"));
        Assert.That(result.StandardError, Does.Contain("Info:").And.Contain("No action is required.")
            .And.Not.Contain("Warning:").And.Not.Contain("Error:").And.Not.Contain("must be restored or rebuilt"));
        if (diagnostics)
            Assert.That(result.StandardError, Does.Contain("[diag]").And.Contain("2000.1.1.1"));
        Assert.That(File.Exists(Path.Combine(_root, "packages", "fixture", "lib", "fixture.dll")), Is.True);
    }

    [Test]
    public async Task Issue094_PackageRecoveryDoesNotHideMalformedSource()
    {
        var fallback = WriteRecoveredFixtureFeed();
        var repo = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));
        var packages = Path.Combine(repo, "packages").Replace('\\', '/');
        File.WriteAllText(_script,
            $"feed '{fallback}'\nfeed '{packages}'\nimport fixture\nimport cop\n" +
            "let cb = cop.parse()\ncommand MAIN = print(cb.Types.Count)");
        File.WriteAllText(Path.Combine(_root, "broken.cop"), "predicate broken(");

        var result = await Run(_script, "-t", _root, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(1), result.StandardError);
        Assert.That(result.StandardOutput.Trim(), Is.EqualTo("0"));
        Assert.That(result.StandardError, Does.Contain("Info:").And.Contain("No action is required.")
            .And.Contain("Error: 1 source analysis error(s)").And.Contain("broken.cop")
            .And.Not.Contain("Warning: Provider").And.Not.Contain("Analysis completed successfully"));
    }

    [Test]
    public void Issue094_UnresolvedCompatibilityRemainsAnActionableError()
    {
        ReleasePackageCompatibilityTests.WriteProvider(_root, "packages", new Version(2000, 1, 1, 1));
        File.WriteAllText(_script, "import fixture\ncommand MAIN = print('must not run')");
        var result = Cop.Providers.Engine.Run(Path.GetDirectoryName(_script)!, _root,
            additionalFeedPaths: [Path.Combine(_root, "packages")]);

        Assert.That(result.HasParseErrors || result.HasFatalErrors, Is.True);
        Assert.That(result.ParseErrors.Concat(result.Errors), Has.Some.Contains("2000.1.1.1"));
        Assert.That(result.Outputs, Is.Empty);
    }

    private string WriteRecoveredFixtureFeed()
    {
        ReleasePackageCompatibilityTests.WriteProvider(_root, "packages", new Version(2000, 1, 1, 1));
        var fallback = Path.Combine(_root, "fallback", "fixture");
        Directory.CreateDirectory(Path.Combine(fallback, "src"));
        File.WriteAllText(Path.Combine(fallback, "cop.json"),
            """{"name":"fixture","version":"1.0.0","title":"Fixture","description":"Fixture","authors":"tests"}""");
        File.WriteAllText(Path.Combine(fallback, "src", "main.cop"), "export let selected = 'RECOVERED'");
        return Path.GetDirectoryName(fallback)!.Replace('\\', '/');
    }
}
