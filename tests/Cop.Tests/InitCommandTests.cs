using System.Text.Json.Nodes;
using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
[NonParallelizable]
public class InitCommandTests
{
    private static string RepoRoot => FindRepoRoot();

    [Test]
    public void AgentLoopGeneratesClaudeAndCopilotHooks()
    {
        var testDir = Path.Combine(RepoRoot, "_test_init_" + Guid.NewGuid().ToString("N")[..8]);
        var previousDirectory = Directory.GetCurrentDirectory();

        try
        {
            Directory.CreateDirectory(testDir);
            Directory.SetCurrentDirectory(testDir);

            var exitCode = InitCommand.Execute(claude: true, localHook: true);

            Assert.That(exitCode, Is.EqualTo(0));

            var claudeSettingsPath = Path.Combine(testDir, ".claude", "settings.local.json");
            Assert.That(File.Exists(claudeSettingsPath), Is.True);
            var claudeSettings = File.ReadAllText(claudeSettingsPath);
            Assert.That(claudeSettings, Does.Contain("cop cop-checks/main.cop -t . -om || true"));

            var copilotHookPath = Path.Combine(testDir, ".github", "hooks", "cop-check.json");
            Assert.That(File.Exists(copilotHookPath), Is.True);
            var copilotHook = JsonNode.Parse(File.ReadAllText(copilotHookPath))!.AsObject();
            Assert.That(copilotHook["version"]!.GetValue<int>(), Is.EqualTo(1));
            var agentStop = copilotHook["hooks"]!["agentStop"]!.AsArray();
            Assert.That(agentStop, Has.Count.EqualTo(1));
            var hook = agentStop[0]!.AsObject();
            Assert.That(hook["type"]!.GetValue<string>(), Is.EqualTo("command"));
            Assert.That(hook["bash"]!.GetValue<string>(), Is.EqualTo("bash .github/hooks/cop-check.sh"));
            Assert.That(hook["cwd"]!.GetValue<string>(), Is.EqualTo("."));
            Assert.That(hook["timeoutSec"]!.GetValue<int>(), Is.EqualTo(120));

            var copilotScriptPath = Path.Combine(testDir, ".github", "hooks", "cop-check.sh");
            Assert.That(File.Exists(copilotScriptPath), Is.True);
            var copilotScript = File.ReadAllText(copilotScriptPath);
            Assert.That(copilotScript, Does.Contain("out=\"$(cop cop-checks/main.cop -t . -om 2>&1)\""));
            Assert.That(copilotScript, Does.Contain("\"decision\":\"block\""));
            Assert.That(copilotScript, Does.Contain("reason"));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }
}
