using System.Diagnostics;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Wires the curated documentation examples (tests/doc-samples/test-doc-examples.cop)
/// into the automated suite. That file mirrors runnable scenarios from README.md,
/// language-reference.md, and static-analysis.md as `test` assertions. Previously it
/// was never executed by CI, so documented scenarios could (and did) silently break.
///
/// This runs the exact documented user command — `cop test &lt;dir&gt;` — against the
/// doc-samples fixtures and requires every assertion to pass.
/// </summary>
[TestFixture]
public class DocExamplesTests
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

    [Test]
    public void DocExamples_AllAssertionsPass()
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 first.");

        var docSamplesDir = Path.Combine(RepoRoot, "tests", "doc-samples");
        Assert.That(Directory.Exists(docSamplesDir), $"doc-samples dir not found at {docSamplesDir}");

        var psi = new ProcessStartInfo
        {
            FileName = CopExe,
            Arguments = $"test \"{docSamplesDir}\"",
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
            $"Documented examples failed (`cop test doc-samples`).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        // Sanity: ensure assertions actually ran and none failed.
        Assert.That(stdout, Does.Contain("0 failed"),
            $"Expected '0 failed' in output.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        Assert.That(stdout, Does.Not.Contain("\u2717"),
            $"Found a failing assertion marker.\nSTDOUT:\n{stdout}");
    }
}
