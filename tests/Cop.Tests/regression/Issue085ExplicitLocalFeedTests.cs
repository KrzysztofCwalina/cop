using NUnit.Framework;

namespace Cop.Tests;

public partial class ToolchainCliIntegrationTests
{
    [Test]
    [Explicit("Issue #85: auto-restore ignores explicitly declared local feeds - remove when fixed")]
    [Category("PendingFix")]
    public async Task ExplicitLocalFeed_DoesNotAttemptRemoteRestore()
    {
        Directory.Move(Path.Combine(_root, "packages"), Path.Combine(_root, "feed"));
        var feed = Path.Combine(_root, "feed").Replace('\\', '/');
        File.WriteAllText(_script, $"feed '{feed}'\nimport fixture\ncommand MAIN = print('unused')");
        var result = await Run(_script, "-t", _root, "--no-color");
        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput.Trim(), Is.EqualTo("unused"));
        Assert.That(result.StandardError, Is.Empty, "A locally resolvable import must not trigger network restore.");
    }
}
