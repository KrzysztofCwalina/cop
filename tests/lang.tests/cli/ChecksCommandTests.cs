using System.ComponentModel;
using System.Diagnostics;
using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests.Lang.Cli;

[TestFixture]
public class ChecksCommandTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cop-checks-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteInstructions(string cwd, string content = "# Guidelines\n- All C# public types must be in a namespace.\n")
    {
        var githubDir = Path.Combine(cwd, ".github");
        Directory.CreateDirectory(githubDir);
        File.WriteAllText(Path.Combine(githubDir, "copilot-instructions.md"), content);
    }

    // ── BuildStartInfo ───────────────────────────────────────────────

    [Test]
    public void BuildStartInfo_Copilot_UsesPromptAndAllowAllTools()
    {
        var psi = ChecksCommand.BuildStartInfo("copilot", "PROMPT-BODY", @"C:\repo");

        Assert.That(psi.FileName, Is.EqualTo("copilot"));
        Assert.That(psi.WorkingDirectory, Is.EqualTo(@"C:\repo"));
        Assert.That(psi.ArgumentList, Does.Contain("-p"));
        Assert.That(psi.ArgumentList, Does.Contain("PROMPT-BODY"));
        Assert.That(psi.ArgumentList, Does.Contain("--allow-all-tools"));
        Assert.That(psi.UseShellExecute, Is.False);
    }

    [Test]
    public void BuildStartInfo_Claude_UsesPrintAndAcceptEdits()
    {
        var psi = ChecksCommand.BuildStartInfo("claude", "PROMPT-BODY", "/repo");

        Assert.That(psi.FileName, Is.EqualTo("claude"));
        Assert.That(psi.ArgumentList, Does.Contain("-p"));
        Assert.That(psi.ArgumentList, Does.Contain("PROMPT-BODY"));
        Assert.That(psi.ArgumentList, Does.Contain("--permission-mode"));
        Assert.That(psi.ArgumentList, Does.Contain("acceptEdits"));
    }

    // ── BuildPrompt ──────────────────────────────────────────────────

    [Test]
    public void BuildPrompt_ContainsKeyConstraints()
    {
        var prompt = ChecksCommand.BuildPrompt();

        Assert.That(prompt, Does.Contain("cop-checks/"));
        Assert.That(prompt, Does.Contain("main.cop"));
        Assert.That(prompt, Does.Contain("cop verify cop-checks/"));
        // Must forbid AI-based checks and demand static checks.
        Assert.That(prompt, Does.Contain("ai.judge"));
        Assert.That(prompt.ToLowerInvariant(), Does.Contain("static"));
        // Must reference the markers so the agent skips cop's own authoring guide.
        Assert.That(prompt, Does.Contain("<!-- BEGIN COP INSTRUCTIONS -->"));
        Assert.That(prompt, Does.Contain("<!-- END COP INSTRUCTIONS -->"));
    }

    [Test]
    public void BuildPrompt_EnforcesStructureAndCleanliness()
    {
        var prompt = ChecksCommand.BuildPrompt();

        // Hard structural mandate: one file per check, only main.cop has a command.
        Assert.That(prompt, Does.Contain("ONE focused check per file"));
        Assert.That(prompt, Does.Contain("ONLY file"));
        // Repo cleanliness: no scratch files in the repo; experiment outside it.
        Assert.That(prompt.ToLowerInvariant(), Does.Contain("scratch"));
        Assert.That(prompt, Does.Contain("OUTSIDE this repository"));
        // Embeds the canonical authoring guide (so the agent follows the pattern).
        Assert.That(prompt, Does.Contain("COP AUTHORING GUIDE"));
        Assert.That(prompt, Does.Contain("Canonical"));
        // Bakes in the single-quote / no-verbatim-string gotcha.
        Assert.That(prompt, Does.Contain("SINGLE quotes"));
        // Includes verified worked examples and efficiency guidance to curb over-exploration.
        Assert.That(prompt, Does.Contain("WORKED EXAMPLES"));
        Assert.That(prompt, Does.Contain("BE EFFICIENT"));
        Assert.That(prompt, Does.Contain("{item.Name}"));
    }

    // ── Execute guards ───────────────────────────────────────────────

    [Test]
    public void Execute_MissingInstructions_ReturnsErrorWithoutLaunchingAgent()
    {
        var cwd = NewTempDir();
        try
        {
            bool launched = false;
            int rc = ChecksCommand.Execute(
                claude: false,
                cwd: cwd,
                runAgent: _ => { launched = true; return 0; });

            Assert.That(rc, Is.EqualTo(1));
            Assert.That(launched, Is.False, "agent must not launch when no guidelines exist");
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Test]
    public void Execute_AgentNotInstalled_ReturnsError()
    {
        var cwd = NewTempDir();
        try
        {
            WriteInstructions(cwd);
            // Process.Start throws Win32Exception when the agent executable is not on PATH.
            int rc = ChecksCommand.Execute(
                claude: false,
                cwd: cwd,
                runAgent: _ => throw new Win32Exception(2));

            Assert.That(rc, Is.EqualTo(1));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Test]
    public void Execute_NoChecksProduced_ReturnsError()
    {
        var cwd = NewTempDir();
        try
        {
            WriteInstructions(cwd);
            // Agent "runs" but writes nothing.
            int rc = ChecksCommand.Execute(
                claude: false,
                cwd: cwd,
                runAgent: _ => 0);

            Assert.That(rc, Is.EqualTo(1));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Test]
    public void Execute_AgentWritesVerifiableChecks_ReturnsZero()
    {
        var cwd = NewTempDir();
        try
        {
            WriteInstructions(cwd);

            // Fake agent: writes a trivial valid cop-checks/main.cop into the working dir.
            int Fake(ProcessStartInfo psi)
            {
                var checksDir = Path.Combine(psi.WorkingDirectory, "cop-checks");
                Directory.CreateDirectory(checksDir);
                File.WriteAllText(
                    Path.Combine(checksDir, "main.cop"),
                    "command MAIN = print('cop checks placeholder')\n");
                return 0;
            }

            int rc = ChecksCommand.Execute(
                claude: false,
                cwd: cwd,
                runAgent: Fake);

            Assert.That(rc, Is.EqualTo(0));
            Assert.That(File.Exists(Path.Combine(cwd, "cop-checks", "main.cop")), Is.True);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }
}
