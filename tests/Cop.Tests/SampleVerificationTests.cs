using NUnit.Framework;
using System.Diagnostics;

namespace Cop.Tests;

/// <summary>
/// Self-check: verifies all sample .cop files compile (cop verify) and are listed in docs/samples.md.
/// </summary>
[TestFixture]
public class SampleVerificationTests
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

    private static IEnumerable<string> GetAllSampleFiles()
    {
        var samplesDir = Path.Combine(RepoRoot, "samples");
        var packagesDir = Path.Combine(RepoRoot, "packages");

        // Top-level samples
        foreach (var file in Directory.GetFiles(samplesDir, "*.cop", SearchOption.AllDirectories))
            yield return file;

        // Package samples
        foreach (var file in Directory.GetFiles(packagesDir, "*.cop", SearchOption.AllDirectories))
        {
            if (file.Replace('\\', '/').Contains("/samples/"))
                yield return file;
        }
    }

    [Test]
    public void AllSamples_PassVerify()
    {
        var failures = new List<string>();

        foreach (var sampleFile in GetAllSampleFiles())
        {
            var psi = new ProcessStartInfo
            {
                FileName = CopExe,
                Arguments = $"verify \"{sampleFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);

            if (process.ExitCode != 0)
            {
                var relativePath = Path.GetRelativePath(RepoRoot, sampleFile).Replace('\\', '/');
                failures.Add($"{relativePath}: {stderr.Trim()}");
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail($"{failures.Count} sample(s) failed cop verify:\n" +
                string.Join("\n", failures));
        }
    }

    [Test]
    public void AllSamples_LinkedInSamplesMd()
    {
        var samplesIndex = Path.Combine(RepoRoot, "docs", "samples.md");
        Assert.That(File.Exists(samplesIndex), $"docs/samples.md not found at {samplesIndex}");

        var indexContent = File.ReadAllText(samplesIndex);
        var unlinked = new List<string>();

        foreach (var sampleFile in GetAllSampleFiles())
        {
            var fileName = Path.GetFileName(sampleFile);

            // Check that the filename appears in the index
            if (!indexContent.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(RepoRoot, sampleFile).Replace('\\', '/');
                unlinked.Add(relativePath);
            }
        }

        if (unlinked.Count > 0)
        {
            Assert.Fail($"{unlinked.Count} sample(s) not listed in docs/samples.md:\n" +
                string.Join("\n", unlinked.Select(p => $"  {p}")));
        }
    }
}
