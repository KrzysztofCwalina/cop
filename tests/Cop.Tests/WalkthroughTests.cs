using System.Diagnostics;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Wires the per-language walkthrough scenarios (docs/languages/*.md §"Write a
/// Simple Rule") into the automated suite. Each fixture under
/// tests/walkthroughs/&lt;lang&gt;/ mirrors the documented `checks.cop`
/// predicates as `test` assertions against a small source file. Previously the
/// walkthroughs had no end-to-end coverage (Go had none, Java only package-level).
/// </summary>
[TestFixture]
public class WalkthroughTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    private static string CopExe => Path.Combine(RepoRoot, "install", "win-x64", "cop.exe");

    private static readonly string[] Languages =
        ["csharp", "python", "javascript", "go", "java", "rust"];

    [TestCaseSource(nameof(Languages))]
    public void Walkthrough_DocumentedRule_AssertionsPass(string language)
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 first.");

        var langDir = Path.Combine(RepoRoot, "tests", "walkthroughs", language);
        Assert.That(Directory.Exists(langDir), $"Walkthrough fixture dir not found: {langDir}");

        var psi = new ProcessStartInfo
        {
            FileName = CopExe,
            Arguments = $"test \"{langDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);

        Assert.That(process.ExitCode, Is.EqualTo(0),
            $"{language} walkthrough rule failed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        Assert.That(stdout, Does.Contain("0 failed"),
            $"{language}: expected '0 failed'.\nSTDOUT:\n{stdout}");
    }
}
