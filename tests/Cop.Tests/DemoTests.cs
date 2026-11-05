using System.Diagnostics;
using System.Text.Json;
using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// End-to-end tests verifying the demo script steps produce valid output.
/// Each test runs cop with the -p flag for explicit provider loading.
/// </summary>
[TestFixture]
public class DemoTests
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
    private static string DemoDir => Path.Combine(RepoRoot, "samples", "static-analysis", "slop-metrics");

    private (string stdout, string stderr, int exitCode) RunCop(string args, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CopExe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };

        using var process = Process.Start(psi)!;
        return ReadProcessOutput(process, timeoutMs);
    }

    private static (string stdout, string stderr, int exitCode) ReadProcessOutput(
        Process process, int timeoutMs)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail($"Test process timed out after {timeoutMs} ms.");
        }

        return (stdout.GetAwaiter().GetResult().Trim(),
            stderr.GetAwaiter().GetResult().Trim(), process.ExitCode);
    }

    [Test]
    public async Task ProcessOutputDrainsBothFullPipesWithoutDeadlocking()
    {
        var configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent!.Name;
        var helper = Path.Combine(RepoRoot, "tests", "toolchain-worker", "bin",
            configuration, "net10.0", "Toolchain.Worker.dll");
        var start = new ProcessStartInfo(ToolchainProcess.FindExecutable("dotnet")
            ?? throw new InvalidOperationException("The test requires the .NET host."))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add("finite-flood");
        using var process = Process.Start(start)!;
        var output = Task.Run(() => ReadProcessOutput(process, 5000));
        try
        {
            var result = await output.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(result.exitCode, Is.EqualTo(0));
            Assert.That(result.stdout, Is.EqualTo(new string('x', 100_000)));
            Assert.That(result.stderr, Is.EqualTo(new string('y', 100_000)));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await output;
        }
    }

    [Test]
    public void Demo_AllFiles_PassVerify()
    {
        var demoFiles = Directory.GetFiles(DemoDir, "*.cop");
        Assert.That(demoFiles.Length, Is.GreaterThanOrEqualTo(2), "Expected at least 2 slop-metrics .cop files");

        var failures = new List<string>();
        foreach (var file in demoFiles)
        {
            var (_, stderr, exitCode) = RunCop($"verify \"{file}\"");
            if (exitCode != 0)
                failures.Add($"{Path.GetFileName(file)}: {stderr}");
        }

        if (failures.Count > 0)
            Assert.Fail($"Demo files failed verify:\n{string.Join("\n", failures)}");
    }

    [Test]
    public void Demo_CsharpChecks_WithPFlag_ProducesViolations()
    {
        // Step 1: run csharp-checks package with -p csharp
        var (stdout, _, _) = RunCop($"run csharp-checks -t \"{RepoRoot}\" -p csharp");

        // Should produce violations
        Assert.That(stdout, Is.Not.Empty, "csharp-checks should produce output");
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines.Length, Is.GreaterThan(5), "Should find multiple violations");
    }

    [Test]
    public void Demo_CodeMetrics_WithPFlag_ProducesValidJson()
    {
        // Step 2: run code-metrics package with -p csharp
        var (stdout, _, _) = RunCop($"run code-metrics -t \"{RepoRoot}\" -p csharp");

        Assert.That(stdout, Does.StartWith("{"), "Output should be JSON object");

        var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        Assert.That(root.TryGetProperty("totalViolations", out var total), Is.True);
        Assert.That(root.TryGetProperty("linesOfCode", out var loc), Is.True);
        Assert.That(root.TryGetProperty("slopPerKloc", out var slopKloc), Is.True);
        Assert.That(root.TryGetProperty("weightedSlopPerKloc", out _), Is.True);

        Assert.That(total.GetInt32(), Is.GreaterThan(0), "Should find some violations");
        Assert.That(loc.GetInt32(), Is.GreaterThan(10000), "Cop repo has >10K lines of C#");
        Assert.That(slopKloc.GetDouble(), Is.GreaterThan(0), "Slop/KLOC should be positive");
    }

    [Test]
    public void Demo_Slop_WithPFlag_ProducesJson()
    {
        // demo-slop.cop with -p csharp
        var copFile = Path.Combine(DemoDir, "slop.cop");
        var (stdout, _, _) = RunCop($"\"{copFile}\" -t \"{RepoRoot}\" -p csharp");

        Assert.That(stdout, Does.StartWith("{"), "Output should be JSON");
        var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.That(root.GetProperty("totalViolations").GetInt32(), Is.GreaterThan(0));
        Assert.That(root.GetProperty("linesOfCode").GetInt32(), Is.GreaterThan(10000));
    }

    [Test]
    public void Demo_Custom_WithPFlag_AddsViolations()
    {
        // demo-slop (base) vs demo-custom (extended)
        var slopFile = Path.Combine(DemoDir, "slop.cop");
        var customFile = Path.Combine(DemoDir, "custom-slop.cop");

        var (slopOut, _, _) = RunCop($"\"{slopFile}\" -t \"{RepoRoot}\" -p csharp");
        var (customOut, _, _) = RunCop($"\"{customFile}\" -t \"{RepoRoot}\" -p csharp");

        var baseViolations = JsonDocument.Parse(slopOut).RootElement.GetProperty("totalViolations").GetInt32();
        var customViolations = JsonDocument.Parse(customOut).RootElement.GetProperty("totalViolations").GetInt32();

        Assert.That(customViolations, Is.GreaterThan(baseViolations),
            $"Custom ({customViolations}) should exceed base ({baseViolations})");
    }

    [Test]
    public void Demo_PFlag_ErrorOnUnknownProvider()
    {
        var (_, stderr, exitCode) = RunCop($"run code-metrics -t \"{RepoRoot}\" -p nonexistent");
        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(stderr, Does.Contain("not found"));
    }
}
