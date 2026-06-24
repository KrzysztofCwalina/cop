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

    [Test]
    public void CustomCopCmd_ClaudeBranch_RewritesCommandHooksAndInstructions()
    {
        RunInit(
            () => InitCommand.Execute(claude: true, localHook: true, copCmd: "mise exec -- cop"),
            (dir, _) =>
            {
                var settings = File.ReadAllText(Path.Combine(dir, ".claude", "settings.local.json"));
                Assert.That(settings, Does.Contain("mise exec -- cop cop-checks/main.cop -t . -om || true"));

                var script = File.ReadAllText(Path.Combine(dir, ".github", "hooks", "cop-check.sh"));
                Assert.That(script, Does.Contain("out=\"$(mise exec -- cop cop-checks/main.cop -t . -om 2>&1)\""));

                var cmd = File.ReadAllText(Path.Combine(dir, ".claude", "commands", "cop.md"));
                Assert.That(cmd, Does.Contain("mise exec -- cop cop-checks/main.cop -t ."));
                Assert.That(cmd, Does.Contain("mise exec -- cop help language"));

                var agents = File.ReadAllText(Path.Combine(dir, "AGENTS.md"));
                Assert.That(agents, Does.Contain("> **Invoking cop:**"));
                Assert.That(agents, Does.Contain("mise exec -- cop verify cop-checks/"));
                Assert.That(agents, Does.Not.Contain("{{COP}}"));
                // Non-command references stay literal (language/package, not the CLI invocation).
                Assert.That(agents, Does.Contain("import cop"));
                Assert.That(agents, Does.Contain("cop.parse()"));
            });
    }

    [Test]
    public void CustomCopCmd_CopilotBranch_RewritesInstructionsSkillAndHookScript()
    {
        RunInit(
            () => InitCommand.Execute(copilotHook: true, copCmd: "mise exec -- cop"),
            (dir, _) =>
            {
                var instr = File.ReadAllText(Path.Combine(dir, ".github", "copilot-instructions.md"));
                Assert.That(instr, Does.Contain("> **Invoking cop:**"));
                Assert.That(instr, Does.Contain("mise exec -- cop cop-checks/main.cop -t ."));
                Assert.That(instr, Does.Not.Contain("{{COP}}"));
                Assert.That(instr, Does.Contain("cop-checks/"));

                var skill = File.ReadAllText(Path.Combine(dir, ".github", "skills", "cop", "SKILL.md"));
                Assert.That(skill, Does.Contain("mise exec -- cop cop-checks/main.cop -t ."));
                Assert.That(skill, Does.Contain("mise exec -- cop help language"));

                var script = File.ReadAllText(Path.Combine(dir, ".github", "hooks", "cop-check.sh"));
                Assert.That(script, Does.Contain("out=\"$(mise exec -- cop cop-checks/main.cop -t . -om 2>&1)\""));
            });
    }

    [Test]
    public void DefaultCopCmd_ProducesBareCopWithNoPlaceholderOrCallout()
    {
        RunInit(
            () => InitCommand.Execute(copilotHook: true),
            (dir, _) =>
            {
                var instr = File.ReadAllText(Path.Combine(dir, ".github", "copilot-instructions.md"));
                Assert.That(instr, Does.Contain("cop cop-checks/main.cop -t ."));
                Assert.That(instr, Does.Not.Contain("{{COP}}"));
                Assert.That(instr, Does.Not.Contain("Invoking cop"));
                Assert.That(instr, Does.Not.Contain("mise exec"));

                var script = File.ReadAllText(Path.Combine(dir, ".github", "hooks", "cop-check.sh"));
                Assert.That(script, Does.Contain("out=\"$(cop cop-checks/main.cop -t . -om 2>&1)\""));
            });
    }

    [Test]
    public void DefaultCopCmd_WithMiseConfig_PrintsHint()
    {
        RunInit(
            () => InitCommand.Execute(),
            (_, stdout) =>
            {
                Assert.That(stdout, Does.Contain("mise config detected"));
                Assert.That(stdout, Does.Contain("cop init --cop-cmd \"mise exec -- cop\""));
            },
            seed: dir => File.WriteAllText(Path.Combine(dir, "mise.toml"), "[tools]\n"));
    }

    [Test]
    public void DefaultCopCmd_WithoutMiseConfig_DoesNotPrintHint()
    {
        RunInit(
            () => InitCommand.Execute(),
            (_, stdout) => Assert.That(stdout, Does.Not.Contain("mise config detected")));
    }

    [Test]
    public void CustomCopCmd_WithMiseConfig_DoesNotPrintHint()
    {
        // The hint only nudges users who haven't supplied an invocation yet.
        RunInit(
            () => InitCommand.Execute(copCmd: "mise exec -- cop"),
            (_, stdout) => Assert.That(stdout, Does.Not.Contain("mise config detected")),
            seed: dir => File.WriteAllText(Path.Combine(dir, "mise.toml"), "[tools]\n"));
    }

    /// <summary>
    /// Runs <paramref name="execute"/> in a fresh temp directory (as cwd) with stdout captured,
    /// optionally seeding files first, then invokes <paramref name="verify"/> with the directory
    /// and captured stdout. Always restores cwd/stdout and deletes the temp directory.
    /// </summary>
    private static void RunInit(Func<int> execute, Action<string, string> verify, Action<string>? seed = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "copinit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var prevDir = Directory.GetCurrentDirectory();
        var prevOut = Console.Out;
        var sw = new StringWriter();
        try
        {
            seed?.Invoke(dir);
            Directory.SetCurrentDirectory(dir);
            Console.SetOut(sw);
            execute();
            Console.SetOut(prevOut);
            Directory.SetCurrentDirectory(prevDir);
            verify(dir, sw.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Directory.SetCurrentDirectory(prevDir);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
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
