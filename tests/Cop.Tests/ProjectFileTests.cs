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

        // Create a feed with the files package (self-contained inline version for test isolation)
        _feedDir = Path.Combine(_tempDir, "feed");
        var pkgDir = Path.Combine(_feedDir, "files");
        var pkgSrc = Path.Combine(pkgDir, "src");
        Directory.CreateDirectory(pkgSrc);

        // Package metadata
        File.WriteAllText(Path.Combine(pkgDir, "cop.json"), """
            {"name":"files","version":"1.0.0","title":"Files","description":"Test files package","authors":"test"}
            """);

        File.WriteAllText(Path.Combine(pkgSrc, "files.cop"), """
                export type Folder = {
                    Path : string,
                    Name : string,
                    Empty : bool,
                    FileCount : int,
                    SubfolderCount : int,
                    Depth : int,
                    MinutesSinceModified : int
                }
                export type File = {
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
                    Files : [File]
                }
                export let Disk = provider('filesystem')
                export let Folders = Disk.Folders
                export let Files = Disk.Files
                export predicate isEmpty(Folder) => Folder.Empty == true
                command empty-folders = foreach Folders:isEmpty => 'Empty folder: {item.Path}'
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
            ["files"],
            codebase,
            ["empty-folders"]);

        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.Outputs, Has.Count.GreaterThanOrEqualTo(1),
            $"Should find empty folders. Errors: [{string.Join("; ", result.Errors)}]");
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
            ["files"],
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
        // Create feed with both files and a csharp-like package
        var csharpDir = Path.Combine(_feedDir, "csharp-checks");
        var csharpSrc = Path.Combine(csharpDir, "src");
        Directory.CreateDirectory(csharpSrc);
        File.WriteAllText(Path.Combine(csharpDir, "cop.json"), """
            {"name":"csharp-checks","version":"1.0.0","title":"C# Checks","description":"Test","authors":"test"}
            """);
        File.WriteAllText(Path.Combine(csharpSrc, "checks.cop"), """
            import files
            command TEST-RULE = foreach Folders:isEmpty => 'Test diagnostic'
            """);

        var codebase = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(codebase);
        Directory.CreateDirectory(Path.Combine(codebase, "empty-dir"));
        File.WriteAllText(Path.Combine(codebase, "file.txt"), "content");

        var result = Engine.RunProject(
            [_feedDir],
            ["files", "csharp-checks"],
            codebase,
            ["empty-folders"]);

        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.ParseErrors, Has.Count.EqualTo(0), string.Join("; ", result.ParseErrors));
        Assert.That(result.Outputs, Has.Count.GreaterThanOrEqualTo(1),
            "Should find empty folders even with multiple packages loaded");
        Assert.That(result.Outputs.Any(d => d.Message.Contains("empty-dir")), Is.True);
    }

    [TestCase("")]
    [TestCase("export enum Collision = describe")]
    [TestCase("export flags Collision = describe")]
    public void RunProject_LocalFunctionKeepsPriorityAndImportedOverloadsRemainAvailable(
        string importedMember)
    {
        var importedSource = Path.Combine(_feedDir, "base-rules", "src");
        var callerSource = Path.Combine(_feedDir, "caller-rules", "src");
        Directory.CreateDirectory(importedSource);
        Directory.CreateDirectory(callerSource);
        File.WriteAllText(Path.Combine(_feedDir, "base-rules", "cop.json"), """
            {"name":"base-rules","version":"1.0.0"}
            """);
        File.WriteAllText(Path.Combine(_feedDir, "caller-rules", "cop.json"), """
            {"name":"caller-rules","version":"1.0.0"}
            """);
        File.WriteAllText(Path.Combine(importedSource, "rules.cop"), """
            export function describe(value: int) : string => 'imported'
            export function describe(value: string) : string => 'imported-string'
            """ + "\n" + importedMember);
        File.WriteAllText(Path.Combine(callerSource, "rules.cop"), """
            import base-rules
            function describe(value: int) : string => 'local'
            command MAIN = [describe(1), describe('text')]
            """);

        var codebase = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(codebase);
        var result = Engine.RunProject(
            [_feedDir], ["caller-rules"], codebase, ["main"]);

        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.ParseErrors, Is.Empty, string.Join("; ", result.ParseErrors));
        Assert.That(result.Outputs.Select(output => output.Message),
            Is.EqualTo(new[] { "local", "imported-string" }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RunProject_FilesPredicateIsNotShadowedByImportedEnumMember(bool hasEmptyFolder)
    {
        var realPackages = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "packages");
        if (!Directory.Exists(realPackages))
        {
            Assert.Ignore("Real packages directory not found");
            return;
        }

        var codebase = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(codebase);
        if (hasEmptyFolder)
        {
            Directory.CreateDirectory(Path.Combine(codebase, "empty-dir"));
        }
        var populatedDirectory = Path.Combine(codebase, "populated-dir");
        Directory.CreateDirectory(populatedDirectory);
        File.WriteAllText(Path.Combine(populatedDirectory, "file.txt"), "content");
        File.WriteAllText(Path.Combine(codebase, "Program.cs"), "var x = 1;\n");

        var diagMessages = new List<string>();
        var result = Engine.RunProject(
            [realPackages],
            ["files"],
            codebase,
            ["empty-folders"],
            diagLog: msg => diagMessages.Add(msg));

        Assert.That(result.HasFatalErrors, Is.False,
            "Fatal errors: " + string.Join("; ", result.Errors));
        Assert.That(result.ParseErrors, Has.Count.EqualTo(0),
            "Parse errors: " + string.Join("; ", result.ParseErrors));
        string[] expected = hasEmptyFolder
            ? ["empty-dir(0): error: Empty folder: empty-dir"]
            : [];
        Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(expected),
            string.Join("\n", diagMessages));
    }
}
