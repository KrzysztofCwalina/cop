using System.Diagnostics;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public partial class GoldenOutputSnapshotTests
{
    private static readonly Regex AnsiRegex = AnsiPattern();

    private static string RepoRoot => FindRepoRoot();
    private static string CopExe => Path.Combine(RepoRoot, "install", "win-x64", "cop.exe");
    private static string GoldenDir => Path.Combine(RepoRoot, "tests", "behavior", "golden");
    private static string FixtureDir => Path.Combine(GoldenDir, "fixture");
    private static bool UpdateSnapshots => Environment.GetEnvironmentVariable("COP_UPDATE_SNAPSHOTS") == "1";

    private static IEnumerable<TestCaseData> SnapshotCases()
    {
        yield return new TestCaseData("check-clients").SetName("CheckClients_GoldenOutput");
        yield return new TestCaseData("report-types").SetName("ReportTypes_GoldenOutput");
    }

    [TestCaseSource(nameof(SnapshotCases))]
    public void ProgramOutput_MatchesGoldenSnapshot(string name)
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 -Runtimes win-x64 first.");

        var program = Path.Combine(GoldenDir, $"{name}.cop");
        var golden = Path.Combine(GoldenDir, $"{name}.expected.txt");
        var result = RunCop(program);

        Assert.That(result.ExitCode, Is.Not.EqualTo(2), result.Stderr);
        Assert.That(result.ExitCode, Is.AnyOf(0, 1), result.Stderr);

        var normalized = Normalize(result.Stdout);
        Assert.That(normalized, Is.Not.Empty, "Golden-output snapshots must not bless empty output.");

        if (UpdateSnapshots)
        {
            File.WriteAllText(golden, normalized);
            Assert.Pass($"Updated snapshot: {golden}");
        }

        Assert.That(File.Exists(golden), Is.True,
            $"Missing golden file {golden}. Set COP_UPDATE_SNAPSHOTS=1 to generate it.");

        var expected = Normalize(File.ReadAllText(golden));
        Assert.That(expected, Is.Not.Empty, "Committed golden snapshot must not be empty.");
        Assert.That(normalized, Is.EqualTo(expected));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCop(string program)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CopExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.ArgumentList.Add(program);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(FixtureDir);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Assert.Fail($"cop timed out for {program}");
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string Normalize(string text)
    {
        var withoutAnsi = AnsiRegex.Replace(text, string.Empty)
            .Replace(RepoRoot, "<repo>", StringComparison.OrdinalIgnoreCase);

        var lines = withoutAnsi.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd());

        var normalized = string.Join('\n', lines).TrimEnd('\n');
        return normalized.Length == 0 ? string.Empty : normalized + "\n";
    }

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiPattern();
}
