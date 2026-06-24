using System.Diagnostics;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class IssueRegressionTests
{
    private string _workDir = null!;

    [SetUp]
    public void SetUp()
    {
        _workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "regression-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workDir))
        {
            try { Directory.Delete(_workDir, recursive: true); } catch { }
        }
    }

    // Issue #5
    [Test]
    [Explicit("Issue #5: verify does not flag duplicate command MAIN across a folder — remove when fixed")]
    [Category("PendingFix")]
    public void Issue005_SingleFileRunIsIsolatedButFolderVerifyRejectsDuplicateMain()
    {
        File.WriteAllText(Path.Combine(_workDir, "target.cop"),
            "import code-analysis\n" +
            "command MAIN = CHECK([])\n");
        File.WriteAllText(Path.Combine(_workDir, "colliding.cop"),
            "import code-analysis\n" +
            "command MAIN = CHECK(filesystem.Folders:toError('WRONG FILE RAN'))\n");

        var run = RunCop($"\"{Path.Combine(_workDir, "target.cop")}\" -t \"{_workDir}\"");
        Assert.That(run.ExitCode, Is.EqualTo(0), Describe(run));
        Assert.That(Normalize(run.Stdout), Is.EqualTo(string.Empty), Describe(run));

        var verify = RunCop($"verify \"{_workDir}\"");
        Assert.That(verify.ExitCode, Is.EqualTo(1), Describe(verify));
        Assert.That(Normalize(verify.Stdout + verify.Stderr), Does.Contain("Duplicate declaration 'MAIN'"), Describe(verify));
    }

    // Issue #8
    [Test]
    public void Issue008_ProviderProgramInChecksFolderScansTargetRoot()
    {
        var checksDir = Path.Combine(_workDir, "checks");
        Directory.CreateDirectory(checksDir);
        File.WriteAllText(Path.Combine(_workDir, "Issue8Target.cs"), "class Issue8Target { }\n");
        File.WriteAllText(Path.Combine(checksDir, "main.cop"),
            "import code\n" +
            "import code-analysis\n" +
            "import csharp\n" +
            "let cb = codebase(csharp.parse())\n" +
            "predicate isIssue8Target(Type) => Type.Name == 'Issue8Target'\n" +
            "let violations = cb.Types:isIssue8Target:toError('ISSUE8 TYPE {item.Name}')\n" +
            "command MAIN = CHECK(violations)\n");

        var result = RunCop($"\"{Path.Combine(checksDir, "main.cop")}\" -t \"{_workDir}\"", _workDir);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("Issue8Target.cs(1): error: ISSUE8 TYPE Issue8Target"), Describe(result));
        Assert.That(Normalize(result.Stderr), Is.EqualTo(string.Empty), Describe(result));
    }

    // Issue #10
    [Test]
    public void Issue010_ExplicitSingleFileRunKeepsFileSelectionAndViolationExitCode()
    {
        var checksDir = Path.Combine(_workDir, "checks");
        Directory.CreateDirectory(checksDir);
        Directory.CreateDirectory(Path.Combine(_workDir, "empty"));
        File.WriteAllText(Path.Combine(checksDir, "target.cop"),
            "import code-analysis\n" +
            "predicate isIssue10Folder(Folder) => Folder.Path == 'empty'\n" +
            "let violations = filesystem.Folders:isIssue10Folder:toError('ISSUE10 VIOLATION {item.Path}')\n" +
            "command MAIN = CHECK(violations)\n");
        File.WriteAllText(Path.Combine(checksDir, "other.cop"),
            "import code-analysis\n" +
            "command MAIN = CHECK([])\n");

        var result = RunCop($"\"{Path.Combine(checksDir, "target.cop")}\" -t \"{_workDir}\"", _workDir);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("empty(0): error: ISSUE10 VIOLATION empty"), Describe(result));
        Assert.That(Normalize(result.Stderr), Is.EqualTo(string.Empty), Describe(result));
    }

    // Issue #37
    [Test]
    public void Issue037_SuccessfulForeachOutputExitsZero()
    {
        var program = Path.Combine(_workDir, "foreach.cop");
        File.WriteAllText(program, "command MAIN = foreach [1] => 'ISSUE37 OUTPUT {item}'\n");

        var result = RunCop($"\"{program}\"");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("ISSUE37 OUTPUT 1"), Describe(result));
        Assert.That(Normalize(result.Stderr), Is.EqualTo(string.Empty), Describe(result));
    }

    // Issue #38
    [Test]
    public void Issue038_BareTopLevelExpressionProducesOutput()
    {
        var program = Path.Combine(_workDir, "expression.cop");
        File.WriteAllText(program, "'ISSUE38 OUTPUT'\n");

        var result = RunCop($"\"{program}\"");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("ISSUE38 OUTPUT"), Describe(result));
        Assert.That(Normalize(result.Stderr), Is.EqualTo(string.Empty), Describe(result));
    }

    private static string RepoRoot => FindRepoRoot();

    private static string CopExe => Path.Combine(RepoRoot, "install", "win-x64", "cop.exe");

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCop(string args, string? workingDir = null)
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 -Runtimes win-x64 first.");

        var psi = new ProcessStartInfo
        {
            FileName = CopExe,
            Arguments = args,
            WorkingDirectory = workingDir ?? RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Assert.Fail($"cop.exe timed out. Args: {args}");
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.Replace("\r\n", "\n"), @"\u001b\[[0-9;]*m", string.Empty).Trim();

    private static string Describe((int ExitCode, string Stdout, string Stderr) result) =>
        $"ExitCode: {result.ExitCode}\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}";
}
