using NUnit.Framework;

namespace Cop.Tests;

public partial class ToolchainCliIntegrationTests
{
    [TestCase("absolute")]
    [TestCase("relative")]
    [TestCase("escaped")]
    public async Task ExplicitLocalFeed_DoesNotAttemptRemoteRestore(string kind)
    {
        var feedDirectory = Path.Combine(_root, kind == "escaped" ? "feed's sources" : "local feed");
        Directory.Move(Path.Combine(_root, "packages"), feedDirectory);
        var name = "local-feed-" + Guid.NewGuid().ToString("N");
        var package = Path.Combine(feedDirectory, name);
        Directory.CreateDirectory(Path.Combine(package, "src"));
        Directory.CreateDirectory(Path.Combine(package, "instructions"));
        File.WriteAllText(Path.Combine(package, "cop.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            name, version = "1.0.0", title = "Local feed fixture", description = "Cold-cache resolution",
            authors = "cop-tests", applyTo = "**/*.fixture"
        }));
        File.WriteAllText(Path.Combine(package, "src", "main.cop"), "export let localValue = 'LOCAL-SOURCE'");
        File.WriteAllText(Path.Combine(package, "instructions", "guide.md"), "# Local feed guide\n");
        var feed = (kind == "relative"
            ? Path.GetRelativePath(Path.GetDirectoryName(_script)!, feedDirectory)
            : feedDirectory).Replace('\\', '/').Replace("'", "\\'");
        File.WriteAllText(_script, $"feed '{feed}'\nimport {name}\ncommand MAIN = print(localValue)");
        var result = await Run(_script, "-t", _root, "--no-color");
        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput.Trim(), Is.EqualTo("LOCAL-SOURCE"));
        Assert.That(result.StandardError, Is.Empty, "A locally resolvable import must not trigger network restore.");
        var instructions = Path.Combine(_root, ".github", "instructions", name + ".instructions.md");
        Assert.That(File.Exists(instructions), Is.True, "Declared feeds must also supply package instructions.");
        Assert.That(File.ReadAllText(instructions).Replace("\r\n", "\n"),
            Is.EqualTo("---\napplyTo: '**/*.fixture'\n---\n\n# Local feed guide\n"));
    }
}
