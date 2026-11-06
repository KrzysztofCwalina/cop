using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public sealed class ExplicitRestoreRegressionTests
{
    [TestCase("feed 'github.com/'\nimport core",
        "Error: Invalid reference format: 'github.com//core'. Path must have at least 4 segments (host/owner/repo/package) (Parameter 'reference')")]
    [TestCase("import core",
        "Error: No GitHub feed found. Restore requires a feed like 'github.com/owner/repo'")]
    public async Task ExplicitGitHubFeedReachesReferenceValidation(string source, string expectedError)
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-explicit-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "main.cop");
            File.WriteAllText(script, source);
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            var configuration = directory.Parent!.Name;
            while (!File.Exists(Path.Combine(directory.FullName, "cop.sln")))
                directory = directory.Parent!;
            var cli = Path.Combine(directory.FullName, "cop", "cli", "bin", configuration, "net10.0", "cop.dll");
            var dotnet = ToolchainProcess.FindExecutable("dotnet")
                ?? throw new InvalidOperationException("dotnet is required for the CLI regression.");
            var result = await ToolchainProcess.RunAsync(
                new ToolchainCommand(dotnet, [cli, "package", "restore", script], root));

            Assert.That(result.ExitCode, Is.EqualTo(1));
            Assert.That(result.StandardOutput, Is.Empty);
            Assert.That(result.StandardError.Trim(), Is.EqualTo(expectedError));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
