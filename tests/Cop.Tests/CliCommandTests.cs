using System.Diagnostics;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// End-to-end coverage for the documented CLI surface (docs/cli-reference.md,
/// docs/testing.md). These spawn the published cop.exe and assert documented
/// behavior/exit codes for read-only, deterministic, offline commands.
///
/// Side-effecting / networked commands (init, vscode, update, package
/// new/publish/restore/search, feed add/remove, lock/unlock, repl) are
/// intentionally excluded — they are not safe to run in CI.
/// </summary>
[TestFixture]
public class CliCommandTests
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

    private static (int ExitCode, string Stdout, string Stderr) RunCop(string args, string? workingDir = null)
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 first.");

        var psi = new ProcessStartInfo
        {
            FileName = CopExe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? RepoRoot,
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
            try { p.Kill(entireProcessTree: true); } catch { }
        return (p.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string WriteTempCop(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cop-cli-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "p.cop"), content);
        return dir;
    }

    // --- version / help ---

    [Test]
    public void Version_PrintsVersion_ExitZero()
    {
        var (exit, stdout, _) = RunCop("-v");
        Assert.That(exit, Is.EqualTo(0));
        var v = stdout.Trim();
        // Version is a date-based dotted number, e.g. 2026.6.22.4 — check shape without a regex.
        Assert.That(v.Length > 0 && char.IsDigit(v[0]) && v.Contains('.'),
            $"Expected a version like 2026.6.22.x, got: {v}");
        Assert.That(v.Split('.').Length, Is.GreaterThanOrEqualTo(3), $"version: {v}");
    }

    [Test]
    public void Help_PrintsUsage_ExitZero()
    {
        var (exit, stdout, _) = RunCop("-h");
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(stdout, Does.Contain("usage"));
        Assert.That(stdout, Does.Contain("cop test"));
        Assert.That(stdout, Does.Contain("cop verify"));
    }

    // Regression: a bare `cop` (no command) must NOT fall through to the getting-started screen
    // or run local files.
    [Test]
    public void Run_NoTarget_PrintsError_ExitTwo()
    {
        var (exit, stdout, stderr) = RunCop("run");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(stderr.ToLowerInvariant(), Does.Contain("needs a target"));
        Assert.That(stdout, Does.Not.Contain("getting started"));
    }

    // Regression: a bare `cop` (no command) shows help and NEVER runs local .cop files.
    [Test]
    public void BareCop_WithLocalCopFile_DoesNotRun_ShowsHelp()
    {
        var dir = WriteTempCop("command MAIN = error('RAN_THE_FILE')");
        try
        {
            var (exit, stdout, stderr) = RunCop("", dir);
            Assert.That(exit, Is.EqualTo(0));
            Assert.That(stdout, Does.Contain("getting started"));
            Assert.That(stdout + stderr, Does.Not.Contain("RAN_THE_FILE"), "bare cop must not execute local .cop files");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void HelpLanguage_PrintsLanguageReference_ExitZero()
    {
        var (exit, stdout, _) = RunCop("help language");
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(stdout, Does.Contain("Language Reference"));
    }

    [Test]
    public void HelpPackage_PrintsPackageDoc_ExitZero()
    {
        // csharp is a bundled package — resolves offline, no network restore.
        var (exit, stdout, _) = RunCop("help csharp");
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(stdout.Trim(), Is.Not.Empty);
        Assert.That(stdout, Does.Contain("csharp"));
    }

    // --- package list ---

    [Test]
    public void PackageList_ListsBundledPackages_ExitZero()
    {
        var (exit, stdout, _) = RunCop("package list");
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(stdout, Does.Contain("csharp"));
        Assert.That(stdout, Does.Contain("python"));
        Assert.That(stdout, Does.Contain("javascript"));
    }

    // --- verify ---

    [Test]
    public void Verify_ValidProgram_ExitZero()
    {
        var dir = WriteTempCop("command MAIN = print('hello')\n");
        try
        {
            var (exit, _, _) = RunCop($"verify \"{Path.Combine(dir, "p.cop")}\"");
            Assert.That(exit, Is.EqualTo(0));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Verify_InvalidProgram_ExitNonZero()
    {
        // Missing predicate body — a parse error. `cop verify` reports errors with exit 1
        // (0 = verified clean, 1 = problems found; the 0/1/2 table applies to `cop <program>`).
        var dir = WriteTempCop("predicate broken(Type) =>\n");
        try
        {
            var (exit, _, _) = RunCop($"verify \"{Path.Combine(dir, "p.cop")}\"");
            Assert.That(exit, Is.EqualTo(1));
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- test exit codes (docs/testing.md) ---

    [Test]
    public void Test_AllPass_ExitZero()
    {
        var dir = WriteTempCop("test ok = assert(1 == 1)\ntest also = assert(2 > 1)\n");
        try
        {
            var (exit, stdout, _) = RunCop($"test \"{dir}\"");
            Assert.That(exit, Is.EqualTo(0), $"stdout: {stdout}");
            Assert.That(stdout, Does.Contain("2 passed"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Test_OneFails_ExitOne()
    {
        var dir = WriteTempCop("test ok = assert(1 == 1)\ntest bad = assert(1 == 2)\n");
        try
        {
            var (exit, stdout, _) = RunCop($"test \"{dir}\"");
            Assert.That(exit, Is.EqualTo(1), $"stdout: {stdout}");
            Assert.That(stdout, Does.Contain("1 failed"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Test_NoTests_ExitTwo()
    {
        var dir = WriteTempCop("command MAIN = print('no tests here')\n");
        try
        {
            var (exit, _, _) = RunCop($"test \"{dir}\"");
            Assert.That(exit, Is.EqualTo(2));
        }
        finally { Directory.Delete(dir, true); }
    }
}
