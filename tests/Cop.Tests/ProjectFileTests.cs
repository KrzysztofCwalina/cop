using Cop.Lang;
using Cop.Providers;
using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class RunProjectTests
{
    private string _tempDir = null!;
    private string _feedDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-runproject-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        // Create a feed with the filesystem package (self-contained inline version for test isolation)
        _feedDir = Path.Combine(_tempDir, "feed");
        var pkgSrc = Path.Combine(_feedDir, "filesystem", "src");
        Directory.CreateDirectory(pkgSrc);

        File.WriteAllText(Path.Combine(pkgSrc, "filesystem.cop"), """
                export type Folder = {
                    Path : string,
                    Name : string,
                    Empty : bool,
                    FileCount : int,
                    SubfolderCount : int,
                    Depth : int,
                    MinutesSinceModified : int
                }
                export type DiskFile = {
                    Path : string,
                    Name : string,
                    Extension : string,
                    Size : int,
                    Folder : string,
                    Depth : int,
                    MinutesSinceModified : int
                }
                export type Filesystem = {
                    Folders : [Folder],
                    Files : [DiskFile]
                }
                export let Disk = runtime::Filesystem
                export predicate isEmpty(Folder) => Folder.Empty == true
                command empty-folders = foreach Folders:isEmpty => PRINT(
                    'Empty folder: {Folder.Path}'
                )
                """);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Test]
    public void RunProject_FindsEmptyFolders()
    {
        // Create a codebase with one empty folder
        var codebase = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(codebase);
        Directory.CreateDirectory(Path.Combine(codebase, "empty-dir"));
        File.WriteAllText(Path.Combine(codebase, "file.txt"), "content");

        var result = Engine.RunProject(
            [_feedDir],
            ["filesystem"],
            codebase,
            ["empty-folders"]);

        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.Outputs, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(result.Outputs.Any(d => d.Message.Contains("empty-dir")), Is.True,
            "Should detect the empty folder");
    }

    [Test]
    public void RunProject_NoEmptyFolders_NoDiagnostics()
    {
        var codebase = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(codebase);
        File.WriteAllText(Path.Combine(codebase, "file.txt"), "content");
        Directory.CreateDirectory(Path.Combine(codebase, "subdir"));
        File.WriteAllText(Path.Combine(codebase, "subdir", "another.txt"), "content");

        var result = Engine.RunProject(
            [_feedDir],
            ["filesystem"],
            codebase,
            ["empty-folders"]);

        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.Outputs, Has.Count.EqualTo(0));
    }

    [Test]
    public void RunProject_PackageNotFound_ReturnsError()
    {
        var codebase = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(codebase);

        var result = Engine.RunProject(
            [_feedDir],
            ["nonexistent-package"],
            codebase,
            []);

        Assert.That(result.HasFatalErrors, Is.True);
        Assert.That(result.Errors[0], Does.Contain("nonexistent-package"));
    }

    [Test]
    public void RunProject_MultiplePackages_BothRulesWork()
    {
        // Create feed with both filesystem and a csharp-like package
        var csharpSrc = Path.Combine(_feedDir, "csharp", "src");
        Directory.CreateDirectory(csharpSrc);
        File.WriteAllText(Path.Combine(csharpSrc, "checks.cop"), """
            command TEST-RULE = foreach Folders:isEmpty => PRINT(
                'Test diagnostic'
            )
            """);

        var codebase = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(codebase);
        Directory.CreateDirectory(Path.Combine(codebase, "empty-dir"));
        File.WriteAllText(Path.Combine(codebase, "file.txt"), "content");

        var result = Engine.RunProject(
            [_feedDir],
            ["filesystem", "csharp"],
            codebase,
            ["empty-folders"]);

        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.ParseErrors, Has.Count.EqualTo(0), string.Join("; ", result.ParseErrors));
        Assert.That(result.Outputs, Has.Count.GreaterThanOrEqualTo(1),
            "Should find empty folders even with multiple packages loaded");
        Assert.That(result.Outputs.Any(d => d.Message.Contains("empty-dir")), Is.True);
    }

    [Test]
    public void RunProject_RealCsharpPackageWithFilesystem_FindsEmptyFolders()
    {
        // Use the actual packages directory as a feed (real csharp package with Statement predicates)
        var realPackages = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "packages");
        if (!Directory.Exists(realPackages))
        {
            Assert.Ignore("Real packages directory not found");
            return;
        }

        var codebase = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(codebase);
        Directory.CreateDirectory(Path.Combine(codebase, "empty-dir"));
        // Add a .cs file to trigger source parsing (like a real project)
        File.WriteAllText(Path.Combine(codebase, "Program.cs"), "var x = 1;\n");

        var result = Engine.RunProject(
            [realPackages],
            ["filesystem"],
            codebase,
            ["empty-folders"]);

        Assert.That(result.HasFatalErrors, Is.False,
            "Fatal errors: " + string.Join("; ", result.Errors));
        Assert.That(result.ParseErrors, Has.Count.EqualTo(0),
            "Parse errors: " + string.Join("; ", result.ParseErrors));
        Assert.That(result.Outputs, Has.Count.GreaterThanOrEqualTo(1),
            $"Should find empty folders. Got @result.Outputs.Count@ outputs.");
        Assert.That(result.Outputs.Any(d => d.Message.Contains("empty-dir")), Is.True);
    }
}
