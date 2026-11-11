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

            // Use relative path as test name for readability
            yield return new TestCaseData(sampleFile).SetName(relativePath.Replace('\\', '/'));
        }
    }

    [Test]
    public async Task FindFilesSampleReportsEmptyFoldersAndFilesAboveTheSizeThreshold()
    {
        var target = Path.Combine(TestContext.CurrentContext.WorkDirectory, "find-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateDirectory(Path.Combine(target, "empty"));
            using (var file = File.Create(Path.Combine(target, "boundary.bin")))
                file.SetLength(1_000_000);
            using (var file = File.Create(Path.Combine(target, "large.bin")))
                file.SetLength(1_000_001);

            var sampleDirectory = Path.Combine(PackagesDir, "core", "files", "samples");
            var sample = Path.Combine(sampleDirectory, "find-files.cop");
            var result = await Engine.RunAsync(sampleDirectory, target, new EngineRunOptions
            {
                ScriptFiles = [sample],
                AdditionalFeedPaths = [PackagesDir],
            });

            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(new[]
            {
                "Empty: empty",
                "Large: large.bin (1000001 bytes)",
            }));
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [TestCaseSource(nameof(GetSampleFiles))]
    public async Task SampleCompiles(string sampleFilePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cop-sample-tests", Path.GetFileNameWithoutExtension(sampleFilePath));
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);

        try
        {
            // Copy all files from the sample's directory (includes data files)
            var sampleDir = Path.GetDirectoryName(sampleFilePath)!;
            foreach (var file in Directory.GetFiles(sampleDir))
                File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)), true);

            var result = (await Engine.RunAsync(tempDir, tempDir, new EngineRunOptions
            {
                AdditionalFeedPaths = [PackagesDir],
            }));

            if (result.HasParseErrors)
                Assert.Fail($"Parse errors:\n{string.Join("\n", result.ParseErrors)}");

            if (result.HasFatalErrors)
            {
                // Missing command is expected for snippet-style samples
                // that demonstrate concepts without defining a runnable command
                var realErrors = result.Errors
                    .Where(e => !e.Contains("Command 'main' not found") && !e.Contains("Package has no 'command main'"))
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
