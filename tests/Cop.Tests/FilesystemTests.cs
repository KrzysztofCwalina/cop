using Cop.Core;
using Cop.Lang;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class FilesystemTests
{
    private static readonly DataProvider _fsProvider = new FilesystemProvider();
    private static readonly ProviderSchema _fsSchema = ProviderSchema.FromJson(_fsProvider.GetSchema());

    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-fs-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private TypeRegistry CreateRegistryAndScan(string rootPath)
    {
        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(_fsProvider, registry);
        ProviderLoader.QueryAndRegister(_fsProvider, _fsSchema, "filesystem", registry, new ProviderQuery { RootPath = rootPath });
        return registry;
    }

    [Test]
    public void Scan_EmptyFolder_IsDetected()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "empty-dir"));
        File.WriteAllText(Path.Combine(_tempDir, "src", "file.txt"), "hello");

        var registry = CreateRegistryAndScan(_tempDir);
        var folders = registry.GetGlobalCollectionItems("Folders");
        var acc = registry.GetAccessors("Folder")!;

        Assert.That(folders, Is.Not.Null);
        Assert.That(folders, Has.Count.EqualTo(2));

        var emptyFolder = folders!.Single(f => (string)acc["Name"](f)! == "empty-dir");
        Assert.That(acc["Empty"](emptyFolder), Is.True);
        Assert.That(acc["FileCount"](emptyFolder), Is.EqualTo(0));
        Assert.That(acc["SubfolderCount"](emptyFolder), Is.EqualTo(0));

        var srcFolder = folders!.Single(f => (string)acc["Name"](f)! == "src");
        Assert.That(acc["Empty"](srcFolder), Is.False);
        Assert.That(acc["FileCount"](srcFolder), Is.EqualTo(1));
    }

    [Test]
    public void Scan_DiskFiles_AreEnumerated()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "docs"));
        File.WriteAllText(Path.Combine(_tempDir, "root.txt"), "root");
        File.WriteAllText(Path.Combine(_tempDir, "docs", "readme.md"), "# Hi");

        var registry = CreateRegistryAndScan(_tempDir);
        var diskFiles = registry.GetGlobalCollectionItems("DiskFiles");
        var acc = registry.GetAccessors("DiskFile")!;

        Assert.That(diskFiles, Is.Not.Null);
        Assert.That(diskFiles, Has.Count.EqualTo(2));

        var rootFile = diskFiles!.Single(f => (string)acc["Name"](f)! == "root.txt");
        Assert.That(acc["Extension"](rootFile), Is.EqualTo(".txt"));
        Assert.That(acc["Folder"](rootFile), Is.EqualTo(""));
        Assert.That(acc["Depth"](rootFile), Is.EqualTo(0));

        var readmeFile = diskFiles!.Single(f => (string)acc["Name"](f)! == "readme.md");
        Assert.That(acc["Extension"](readmeFile), Is.EqualTo(".md"));
        Assert.That(acc["Folder"](readmeFile), Is.EqualTo("docs"));
        Assert.That(acc["Depth"](readmeFile), Is.EqualTo(1));
    }

    [Test]
    public void Scan_NestedFolders_DepthIsCorrect()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "a", "b", "c"));

        var registry = CreateRegistryAndScan(_tempDir);
        var folders = registry.GetGlobalCollectionItems("Folders")!;
        var acc = registry.GetAccessors("Folder")!;

        Assert.That(acc["Depth"](folders.Single(f => (string)acc["Name"](f)! == "a")), Is.EqualTo(1));
        Assert.That(acc["Depth"](folders.Single(f => (string)acc["Name"](f)! == "b")), Is.EqualTo(2));
        Assert.That(acc["Depth"](folders.Single(f => (string)acc["Name"](f)! == "c")), Is.EqualTo(3));
    }

    [Test]
    public void Scan_PathsUseForwardSlashes()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "parent", "child"));
        File.WriteAllText(Path.Combine(_tempDir, "parent", "child", "file.cs"), "");

        var registry = CreateRegistryAndScan(_tempDir);
        var folders = registry.GetGlobalCollectionItems("Folders")!;
        var files = registry.GetGlobalCollectionItems("DiskFiles")!;
        var fAcc = registry.GetAccessors("Folder")!;
        var dAcc = registry.GetAccessors("DiskFile")!;

        Assert.That(fAcc["Path"](folders.Single(f => (string)fAcc["Name"](f)! == "child")), Is.EqualTo("parent/child"));
        Assert.That(dAcc["Path"](files.Single()), Is.EqualTo("parent/child/file.cs"));
        Assert.That(dAcc["Folder"](files.Single()), Is.EqualTo("parent/child"));
    }

    [Test]
    public void Integration_EmptyFolderCheck_EmitsDiagnostic()
    {
        // Arrange: create directory structure
        var scriptsDir = Path.Combine(_tempDir, "checks");
        var codebaseDir = Path.Combine(_tempDir, "codebase");
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(codebaseDir);
        Directory.CreateDirectory(Path.Combine(codebaseDir, "src"));
        Directory.CreateDirectory(Path.Combine(codebaseDir, "empty-dir"));
        File.WriteAllText(Path.Combine(codebaseDir, "src", "app.cs"), "class App {}");

        // Write a .cop program that flags empty folders
        var copSource = """
            predicate isEmpty(Folder) => Folder.Empty == true
            foreach Folders:isEmpty => 'WARNING: Empty folder: {item.Path}'
            """;
        File.WriteAllText(Path.Combine(scriptsDir, "no-empty-folders.cop"), copSource);

        // Act
        var result = Engine.Run(scriptsDir, codebaseDir);

        // Assert
        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.HasParseErrors, Is.False, string.Join("; ", result.ParseErrors));
        Assert.That(result.Outputs, Has.Count.EqualTo(1));
        Assert.That(result.Outputs[0].Message, Does.Contain("empty-dir"));
    }

    [Test]
    public void Integration_DerivedCollection_WorksWithGlobal()
    {
        // Arrange
        var scriptsDir = Path.Combine(_tempDir, "checks");
        var codebaseDir = Path.Combine(_tempDir, "codebase");
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(codebaseDir);
        Directory.CreateDirectory(Path.Combine(codebaseDir, "empty1"));
        Directory.CreateDirectory(Path.Combine(codebaseDir, "empty2"));
        Directory.CreateDirectory(Path.Combine(codebaseDir, "notempty"));
        File.WriteAllText(Path.Combine(codebaseDir, "notempty", "file.txt"), "content");

        // Write a .cop program using a derived collection
        var copSource = """
            predicate isEmpty(Folder) => Folder.Empty == true
            let EmptyFolders = Folders:isEmpty
            foreach EmptyFolders => 'Empty: {item.Path}'
            """;
        File.WriteAllText(Path.Combine(scriptsDir, "test.cop"), copSource);

        // Act
        var result = Engine.Run(scriptsDir, codebaseDir);

        // Assert
        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.Outputs, Has.Count.EqualTo(2));
        var messages = result.Outputs.Select(d => d.Message).OrderBy(m => m).ToList();
        Assert.That(messages[0], Does.Contain("empty1"));
        Assert.That(messages[1], Does.Contain("empty2"));
    }

    [Test]
    public void GlobalCollection_IsGlobal_ReturnsTrue()
    {
        var registry = new TypeRegistry();
        registry.RegisterGlobalCollection("TestCollection", []);
        Assert.That(registry.IsGlobalCollection("TestCollection"), Is.True);
        Assert.That(registry.IsGlobalCollection("Unknown"), Is.False);
    }
}
