using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Registers filesystem types (Folder, DiskFile) and scans the disk
/// to populate global collections.
/// </summary>
public static class FilesystemTypeRegistrar
{
    /// <summary>
    /// Registers type descriptors, CLR type mappings, and property accessors
    /// for filesystem types. Call once during type registry setup.
    /// </summary>
    public static void Register(TypeRegistry registry)
    {
        RegisterTypeDescriptors(registry);
        RegisterClrTypeMappings(registry);
        RegisterPropertyAccessors(registry);
    }

    /// <summary>
    /// Scans the filesystem from codebasePath and registers Folders and DiskFiles
    /// as global collections. Call after Register().
    /// If a .cop-lock file exists, marks locked files with checksum verification status.
    /// </summary>
    public static void Scan(TypeRegistry registry, string codebasePath)
    {
        codebasePath = Path.GetFullPath(codebasePath);
        var folders = new List<object>();
        var diskFiles = new List<object>();

        ScanDirectory(codebasePath, codebasePath, folders, diskFiles);

        // Apply lock state if .cop-lock exists
        var lockFile = LockFile.Load(codebasePath);
        if (lockFile != null)
            ApplyLockState(lockFile, codebasePath, diskFiles);

        registry.RegisterGlobalCollection("Folders", folders);
        registry.RegisterGlobalCollection("DiskFiles", diskFiles);

        // Register collection declarations so the interpreter knows item types
        if (!registry.HasCollection("Folders"))
            registry.RegisterCollection(new CollectionDeclaration("Folders", "Folder", 0));
        if (!registry.HasCollection("DiskFiles"))
            registry.RegisterCollection(new CollectionDeclaration("DiskFiles", "DiskFile", 0));
    }

    /// <summary>
    /// Applies lock state from .cop-lock to scanned DiskFile entries.
    /// Marks locked files, verifies checksums, injects phantoms for deleted files.
    /// </summary>
    private static void ApplyLockState(LockFile lockFile, string codebasePath, List<object> diskFiles)
    {
        var verifications = lockFile.VerifyChecksums(codebasePath);
        var verificationMap = verifications.ToDictionary(v => v.RelativePath, v => v);

        // Update existing DiskFile entries that are locked
        for (int i = 0; i < diskFiles.Count; i++)
        {
            var file = (DiskFileInfo)diskFiles[i];
            if (verificationMap.TryGetValue(file.Path, out var verification))
            {
                diskFiles[i] = file with
                {
                    IsLocked = true,
                    LockStatus = verification.Status,
                    Checksum = verification.ActualChecksum ?? verification.ExpectedChecksum
                };
                verificationMap.Remove(file.Path);
            }
        }

        // Inject phantom entries for deleted locked files
        foreach (var (path, verification) in verificationMap)
        {
            if (verification.Status == "deleted")
            {
                var folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                var name = Path.GetFileName(path);
                var ext = Path.GetExtension(path);
                var depth = path.Count(c => c == '/');
                if (folder.Length > 0) depth = folder.Count(c => c == '/') + 1;

                diskFiles.Add(new DiskFileInfo(
                    Path: path,
                    Name: name,
                    Extension: ext,
                    Size: 0,
                    Folder: folder,
                    Depth: depth,
                    MinutesSinceModified: 0)
                {
                    IsLocked = true,
                    LockStatus = "deleted",
                    Checksum = verification.ExpectedChecksum
                });
            }
        }
    }

    private static void ScanDirectory(string dir, string root, List<object> folders, List<object> diskFiles)
    {
        var now = DateTime.UtcNow;
        ScanDirectory(dir, root, folders, diskFiles, now);
    }

    /// <summary>
    /// Directories excluded from filesystem scanning. Build artifacts, VCS metadata,
    /// and package caches that contain no user-authored source code.
    /// </summary>
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules",
        ".nuget", ".dotnet", "TestResults",
        "__pycache__", ".mypy_cache", ".pytest_cache",
        "dist", ".next", ".cache"
    };

    private static void ScanDirectory(string dir, string root, List<object> folders, List<object> diskFiles, DateTime now)
    {
        string[] childDirs;
        string[] childFiles;

        try
        {
            childDirs = Directory.GetDirectories(dir);
            childFiles = Directory.GetFiles(dir);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        var relativePath = Path.GetRelativePath(root, dir).Replace('\\', '/');
        if (relativePath == ".") relativePath = "";

        int depth = relativePath.Length == 0 ? 0 : relativePath.Count(c => c == '/') + 1;

        // Add files in this directory
        foreach (var filePath in childFiles)
        {
            var fileRelPath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
            var fi = new FileInfo(filePath);
            var minutesSinceModified = (int)(now - fi.LastWriteTimeUtc).TotalMinutes;
            diskFiles.Add(new DiskFileInfo(
                Path: fileRelPath,
                Name: fi.Name,
                Extension: fi.Extension,
                Size: fi.Length,
                Folder: relativePath,
                Depth: depth,
                MinutesSinceModified: minutesSinceModified));
        }

        // Add this folder (skip root itself)
        if (relativePath.Length > 0)
        {
            var dirInfo = new DirectoryInfo(dir);
            var minutesSinceModified = (int)(now - dirInfo.LastWriteTimeUtc).TotalMinutes;
            folders.Add(new FolderInfo(
                Path: relativePath,
                Name: Path.GetFileName(dir),
                IsEmpty: childDirs.Length == 0 && childFiles.Length == 0,
                FileCount: childFiles.Length,
                SubfolderCount: childDirs.Length,
                Depth: depth,
                MinutesSinceModified: minutesSinceModified));
        }

        // Recurse, pruning excluded directories
        foreach (var childDir in childDirs)
        {
            var dirName = Path.GetFileName(childDir);
            if (ExcludedDirectoryNames.Contains(dirName)) continue;
            ScanDirectory(childDir, root, folders, diskFiles, now);
        }
    }

    private static void RegisterTypeDescriptors(TypeRegistry registry)
    {
        if (!registry.HasType("Folder"))
        {
            RegisterType(registry, "Folder", [
                ("Path", "string", false, false),
                ("Name", "string", false, false),
                ("Empty", "bool", false, false),
                ("FileCount", "int", false, false),
                ("SubfolderCount", "int", false, false),
                ("Depth", "int", false, false),
                ("MinutesSinceModified", "int", false, false),
                ("Source", "string", false, false),
            ]);
        }

        if (!registry.HasType("DiskFile"))
        {
            RegisterType(registry, "DiskFile", [
                ("Path", "string", false, false),
                ("Name", "string", false, false),
                ("Extension", "string", false, false),
                ("Size", "int", false, false),
                ("Folder", "string", false, false),
                ("Depth", "int", false, false),
                ("MinutesSinceModified", "int", false, false),
                ("Source", "string", false, false),
                ("Checksum", "string", true, false),
                ("Locked", "bool", false, false),
                ("LockStatus", "string", false, false),
            ]);
        }
    }

    private static void RegisterType(TypeRegistry registry, string name,
        List<(string Name, string TypeName, bool IsOptional, bool IsCollection)> properties)
    {
        var desc = new TypeDescriptor(name);
        foreach (var (propName, typeName, isOptional, isCollection) in properties)
            desc.Properties[propName] = new PropertyDescriptor(propName, typeName, isOptional, isCollection);
        registry.Register(desc);
    }

    private static void RegisterClrTypeMappings(TypeRegistry registry)
    {
        registry.RegisterClrType(typeof(FolderInfo), "Folder");
        registry.RegisterClrType(typeof(DiskFileInfo), "DiskFile");
    }

    private static void RegisterPropertyAccessors(TypeRegistry registry)
    {
        registry.RegisterAccessors("Folder", new()
        {
            ["Path"] = o => ((FolderInfo)o).Path,
            ["Name"] = o => ((FolderInfo)o).Name,
            ["Empty"] = o => (object)((FolderInfo)o).IsEmpty,
            ["FileCount"] = o => (object)((FolderInfo)o).FileCount,
            ["SubfolderCount"] = o => (object)((FolderInfo)o).SubfolderCount,
            ["Depth"] = o => (object)((FolderInfo)o).Depth,
            ["MinutesSinceModified"] = o => (object)((FolderInfo)o).MinutesSinceModified,
            ["Source"] = o => ((FolderInfo)o).Source,
        });

        registry.RegisterAccessors("DiskFile", new()
        {
            ["Path"] = o => ((DiskFileInfo)o).Path,
            ["Name"] = o => ((DiskFileInfo)o).Name,
            ["Extension"] = o => ((DiskFileInfo)o).Extension,
            ["Size"] = o => (object)((DiskFileInfo)o).Size,
            ["Folder"] = o => ((DiskFileInfo)o).Folder,
            ["Depth"] = o => (object)((DiskFileInfo)o).Depth,
            ["MinutesSinceModified"] = o => (object)((DiskFileInfo)o).MinutesSinceModified,
            ["Source"] = o => ((DiskFileInfo)o).Source,
            ["Checksum"] = o => ((DiskFileInfo)o).Checksum ?? "",
            ["Locked"] = o => (object)((DiskFileInfo)o).IsLocked,
            ["LockStatus"] = o => ((DiskFileInfo)o).LockStatus,
        });
    }
}
