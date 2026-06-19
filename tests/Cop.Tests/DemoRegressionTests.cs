using System.Diagnostics;
using Cop.Cli.Commands;
using Cop.Core;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Regression tests for three bugs that repeatedly broke the simple "empty folders"
/// demo (cop-checks/main.cop run from a repo root):
///   1. Running `cop &lt;subfolder&gt;/file.cop` without -t analyzed the .cop file's
///      folder instead of the current working directory, so no files were seen.
///   2. The cop-checks examples taught `export let`, even though `export` is NOT
///      required for cross-file references within a single program.
///   3. Auto-restore never replaced a stale/incomplete cached package directory
///      (one lacking cop.json), causing "restored ok" but "package not found" loops.
/// </summary>
[TestFixture]
public class DemoRegressionTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-regression-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // --- Bug 1: target defaults to the current working directory, not the .cop file's folder ---

    [Test]
    public void Bug1_ResolveTargetRoot_WithoutTarget_UsesCurrentDirectory()
    {
        var cwd = Path.Combine(_tempDir, "cwd");
        Directory.CreateDirectory(cwd);

        // No -t given: the target must be the cwd, NOT a script folder.
        Assert.That(RunCommand.ResolveTargetRoot(null, cwd), Is.EqualTo(Path.GetFullPath(cwd)));
        Assert.That(RunCommand.ResolveTargetRoot("", cwd), Is.EqualTo(Path.GetFullPath(cwd)));
    }

    [Test]
    public void Bug1_ResolveTargetRoot_WithTarget_OverridesCurrentDirectory()
    {
        var cwd = Path.Combine(_tempDir, "cwd");
        var target = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(target);

        Assert.That(RunCommand.ResolveTargetRoot(target, cwd), Is.EqualTo(Path.GetFullPath(target)));
    }

    [Test]
    public void Bug1_RunCopFileInSubfolderWithoutTarget_AnalyzesCurrentDirectory()
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 first.");

        // checks/ holds the program; the empty folder to flag lives in the cwd (_tempDir).
        var checksDir = Path.Combine(_tempDir, "checks");
        Directory.CreateDirectory(checksDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "empty"));
        // Uses only the built-in filesystem provider — no package restore / network needed.
        File.WriteAllText(Path.Combine(checksDir, "main.cop"),
            "predicate isEmpty(Folder) => Folder.Empty == true\n" +
            "command main = foreach filesystem.Folders:isEmpty => 'EMPTY: {item.Path}'\n");

        // No -t: target must default to the cwd (_tempDir), where 'empty' lives.
        var (_, stdout, stderr) = RunCop("checks/main.cop", _tempDir);

        Assert.That(stdout, Does.Contain("EMPTY: empty"),
            $"Expected the cwd's empty folder to be analyzed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }

    // --- Bug 2: export is not required for cross-file references within one program ---

    [Test]
    public void Bug2_CrossFileLetReference_DoesNotRequireExport()
    {
        var scriptsDir = Path.Combine(_tempDir, "checks");
        var codebaseDir = Path.Combine(_tempDir, "codebase");
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(Path.Combine(codebaseDir, "empty1"));
        Directory.CreateDirectory(Path.Combine(codebaseDir, "full"));
        File.WriteAllText(Path.Combine(codebaseDir, "full", "f.txt"), "content");

        // defs.cop defines a NON-exported let; main.cop references it from another file.
        File.WriteAllText(Path.Combine(scriptsDir, "defs.cop"),
            "predicate isEmpty(Folder) => Folder.Empty == true\n" +
            "let shared-empty = filesystem.Folders:isEmpty\n");
        File.WriteAllText(Path.Combine(scriptsDir, "main.cop"),
            "command main = foreach shared-empty => 'Empty: {item.Path}'\n");

        var result = Engine.Run(scriptsDir, codebaseDir);

        Assert.That(result.HasParseErrors, Is.False, string.Join("; ", result.ParseErrors));
        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        // If export were required, main.cop could not resolve 'shared-empty' and produce output.
        Assert.That(result.Outputs, Has.Count.EqualTo(1));
        Assert.That(result.Outputs[0].Message, Does.Contain("empty1"));
    }

    // --- Bug 3: auto-restore replaces a stale/incomplete cached package, keeps a valid one ---

    [Test]
    public void Bug3_PlaceRestoredPackage_ReplacesIncompleteCacheDirectory()
    {
        // Existing pkgDir is stale/incomplete: lib DLL but NO cop.json manifest.
        var pkgDir = Path.Combine(_tempDir, "cache", "csharp");
        Directory.CreateDirectory(Path.Combine(pkgDir, "lib"));
        File.WriteAllText(Path.Combine(pkgDir, "lib", "csharp-provider.dll"), "old");

        // Freshly downloaded package in temp: complete (has cop.json + src).
        var tempDir = Path.Combine(_tempDir, "cache", ".csharp.tmp");
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        File.WriteAllText(Path.Combine(tempDir, PackageMetadata.MetadataFileName), "{}");
        File.WriteAllText(Path.Combine(tempDir, "src", "csharp.cop"), "# src");

        PackageInstaller.PlaceRestoredPackage(tempDir, pkgDir);

        // The stale dir is replaced by the complete one: cop.json + src now present.
        Assert.That(File.Exists(Path.Combine(pkgDir, PackageMetadata.MetadataFileName)), Is.True,
            "Incomplete cache directory should have been replaced with the complete download.");
        Assert.That(File.Exists(Path.Combine(pkgDir, "src", "csharp.cop")), Is.True);
        Assert.That(Directory.Exists(tempDir), Is.False, "Temp directory should have been moved into place.");
    }

    [Test]
    public void Bug3_PlaceRestoredPackage_KeepsValidConcurrentlyPlacedPackage()
    {
        // Existing pkgDir is already a VALID package (has cop.json) + a unique marker.
        var pkgDir = Path.Combine(_tempDir, "cache", "csharp");
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(pkgDir, PackageMetadata.MetadataFileName), "{}");
        File.WriteAllText(Path.Combine(pkgDir, "MARKER.txt"), "keep-me");

        var tempDir = Path.Combine(_tempDir, "cache", ".csharp.tmp");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, PackageMetadata.MetadataFileName), "{}");

        PackageInstaller.PlaceRestoredPackage(tempDir, pkgDir);

        // The existing valid package is kept (marker survives) and temp is discarded.
        Assert.That(File.Exists(Path.Combine(pkgDir, "MARKER.txt")), Is.True,
            "A valid existing package should be kept, not overwritten.");
        Assert.That(Directory.Exists(tempDir), Is.False, "Temp directory should have been discarded.");
    }

    // --- helpers ---

    private static string RepoRoot
    {
        get
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
                dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
        }
    }

    private static string CopExe => Path.Combine(RepoRoot, "install", "win-x64", "cop.exe");

    private static (int ExitCode, string Stdout, string Stderr) RunCop(string args, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CopExe,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
            try { p.Kill(entireProcessTree: true); } catch { }
        return (p.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
