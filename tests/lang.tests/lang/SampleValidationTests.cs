using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

/// <summary>
/// Validates that all package samples parse and compile without errors.
/// Discovers packages with samples/ directories and runs Engine.Run() on each sample.
/// Samples with "# @sample skip-validation" on any line are skipped.
/// </summary>
[TestFixture]
public class SampleValidationTests
{
    private static string PackagesDir
    {
        get
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir != null)
            {
                var candidate = Path.Combine(dir, "packages");
                if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "code")))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "packages"));
        }
    }

    private static IEnumerable<TestCaseData> GetSampleFiles()
    {
        var packagesDir = PackagesDir;
        if (!Directory.Exists(packagesDir)) yield break;

        // Find all samples/*.cop files recursively under packages/
        foreach (var sampleFile in Directory.GetFiles(packagesDir, "*.cop", SearchOption.AllDirectories))
        {
            // Only include files under a samples/ directory
            var relativePath = Path.GetRelativePath(packagesDir, sampleFile);
            var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!parts.Contains("samples")) continue;

            // Check for skip directive
            var content = File.ReadAllText(sampleFile);
            if (content.Contains("@sample skip-validation")) continue;

            // Use relative path as test name for readability
            yield return new TestCaseData(sampleFile).SetName(relativePath.Replace('\\', '/'));
        }
    }

    [TestCaseSource(nameof(GetSampleFiles))]
    public void SampleCompiles(string sampleFilePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cop-sample-tests", Path.GetFileNameWithoutExtension(sampleFilePath));
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);

        try
        {
            File.Copy(sampleFilePath, Path.Combine(tempDir, Path.GetFileName(sampleFilePath)));

            var result = Engine.Run(tempDir, tempDir, additionalFeedPaths: [PackagesDir]);

            if (result.HasParseErrors)
                Assert.Fail($"Parse errors:\n{string.Join("\n", result.ParseErrors)}");

            if (result.HasFatalErrors)
            {
                // "Command 'main' not found" is expected for snippet-style samples
                // that demonstrate concepts without defining a runnable command
                var realErrors = result.Errors
                    .Where(e => !e.Contains("Command 'main' not found"))
                    .ToList();

                if (realErrors.Count > 0)
                    Assert.Fail($"Fatal errors:\n{string.Join("\n", realErrors)}");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
