using Cop.Lang;
using Cop.Providers;
using Cop.Providers.SourceModel;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class FilesystemTests
{
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

    [Test]
    public void Scan_EmptyFolder_IsDetected()
    {
        // Arrange: create a directory structure with an empty folder
        Directory.CreateDirectory(Path.Combine(_tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "empty-dir"));
        File.WriteAllText(Path.Combine(_tempDir, "src", "file.txt"), "hello");

        // Act
        var registry = new TypeRegistry();
        FilesystemTypeRegistrar.Register(registry);
        FilesystemTypeRegistrar.Scan(registry, _tempDir);

        var folders = registry.GetGlobalCollectionItems("Folders");

        // Assert
        Assert.That(folders, Is.Not.Null);

        var folderInfos = folders!.Cast<FolderInfo>().ToList();
        Assert.That(folderInfos, Has.Count.EqualTo(2));

        var emptyFolder = folderInfos.Single(f => f.Name == "empty-dir");
        Assert.That(emptyFolder.IsEmpty, Is.True);
        Assert.That(emptyFolder.FileCount, Is.EqualTo(0));
        Assert.That(emptyFolder.SubfolderCount, Is.EqualTo(0));

        var srcFolder = folderInfos.Single(f => f.Name == "src");
        Assert.That(srcFolder.IsEmpty, Is.False);
        Assert.That(srcFolder.FileCount, Is.EqualTo(1));
    }

    [Test]
    public void Scan_DiskFiles_AreEnumerated()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tempDir, "docs"));
        File.WriteAllText(Path.Combine(_tempDir, "root.txt"), "root");
        File.WriteAllText(Path.Combine(_tempDir, "docs", "readme.md"), "# Hi");

        // Act
        var registry = new TypeRegistry();
        FilesystemTypeRegistrar.Register(registry);
        FilesystemTypeRegistrar.Scan(registry, _tempDir);

        var diskFiles = registry.GetGlobalCollectionItems("DiskFiles");

        // Assert
        Assert.That(diskFiles, Is.Not.Null);
        var fileInfos = diskFiles!.Cast<DiskFileInfo>().ToList();
        Assert.That(fileInfos, Has.Count.EqualTo(2));

        var rootFile = fileInfos.Single(f => f.Name == "root.txt");
        Assert.That(rootFile.Extension, Is.EqualTo(".txt"));
        Assert.That(rootFile.Folder, Is.EqualTo(""));
        Assert.That(rootFile.Depth, Is.EqualTo(0));

        var readmeFile = fileInfos.Single(f => f.Name == "readme.md");
        Assert.That(readmeFile.Extension, Is.EqualTo(".md"));
        Assert.That(readmeFile.Folder, Is.EqualTo("docs"));
        Assert.That(readmeFile.Depth, Is.EqualTo(1));
    }

    [Test]
    public void Scan_NestedFolders_DepthIsCorrect()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tempDir, "a", "b", "c"));

        // Act
        var registry = new TypeRegistry();
        FilesystemTypeRegistrar.Register(registry);
        FilesystemTypeRegistrar.Scan(registry, _tempDir);

        var folders = registry.GetGlobalCollectionItems("Folders")!.Cast<FolderInfo>().ToList();

        // Assert
        Assert.That(folders.Single(f => f.Name == "a").Depth, Is.EqualTo(1));
        Assert.That(folders.Single(f => f.Name == "b").Depth, Is.EqualTo(2));
        Assert.That(folders.Single(f => f.Name == "c").Depth, Is.EqualTo(3));
    }

    [Test]
    public void Scan_PathsUseForwardSlashes()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tempDir, "parent", "child"));
        File.WriteAllText(Path.Combine(_tempDir, "parent", "child", "file.cs"), "");

        // Act
        var registry = new TypeRegistry();
        FilesystemTypeRegistrar.Register(registry);
        FilesystemTypeRegistrar.Scan(registry, _tempDir);

        var folders = registry.GetGlobalCollectionItems("Folders")!.Cast<FolderInfo>().ToList();
        var files = registry.GetGlobalCollectionItems("DiskFiles")!.Cast<DiskFileInfo>().ToList();

        // Assert
        Assert.That(folders.Single(f => f.Name == "child").Path, Is.EqualTo("parent/child"));
        Assert.That(files.Single().Path, Is.EqualTo("parent/child/file.cs"));
        Assert.That(files.Single().Folder, Is.EqualTo("parent/child"));
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
            foreach Folders:isEmpty => PRINT('WARNING: Empty folder: {item.Path}')
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
            foreach EmptyFolders => PRINT('Empty: {item.Path}')
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
