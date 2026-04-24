using System.Text.Json;
using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Built-in provider for filesystem data (Folders and DiskFiles).
/// Uses the fast Objects path — data packed into DataObject[] with a shared UTF-8 string heap.
/// No per-record CLR objects or CLR strings allocated for the permanent data.
/// </summary>
public class FilesystemProvider : CopProvider
{
    public override ProviderFormat SupportedFormats => ProviderFormat.Objects;

    public override byte[] GetSchema()
    {
        var schema = new
        {
            types = new object[]
            {
                new
                {
                    name = "Folder",
                    properties = new object[]
                    {
                        new { name = "Path", type = "string" },       // slot 0
                        new { name = "Name", type = "string" },       // slot 1
                        new { name = "Empty", type = "bool" },        // slot 2
                        new { name = "FileCount", type = "int" },     // slot 3
                        new { name = "SubfolderCount", type = "int" },// slot 4
                        new { name = "Depth", type = "int" },         // slot 5
                        new { name = "MinutesSinceModified", type = "int" }, // slot 6
                        new { name = "Source", type = "string" },     // slot 7
                    }
                },
                new
                {
                    name = "DiskFile",
                    properties = new object[]
                    {
                        new { name = "Path", type = "string" },       // slot 0
                        new { name = "Name", type = "string" },       // slot 1
                        new { name = "Extension", type = "string" },  // slot 2
                        new { name = "Size", type = "int" },          // slot 3
                        new { name = "Folder", type = "string" },     // slot 4
                        new { name = "Depth", type = "int" },         // slot 5
                        new { name = "MinutesSinceModified", type = "int" }, // slot 6
                        new { name = "Source", type = "string" },     // slot 7
                        new { name = "Checksum", type = "string", optional = true }, // slot 8
                        new { name = "Locked", type = "bool" },       // slot 9
                        new { name = "LockStatus", type = "string" }, // slot 10
                    }
                }
            },
            collections = new object[]
            {
                new { name = "Folders", itemType = "Folder" },
                new { name = "DiskFiles", itemType = "DiskFile" },
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(schema);
    }

    public override Dictionary<string, DataTable> QueryData(ProviderQuery query)
    {
        var rootPath = Path.GetFullPath(query.RootPath
            ?? throw new ArgumentException("RootPath is required for FilesystemProvider."));

        // Phase 1: scan filesystem into temporary typed lists
        var folders = new List<SourceModel.FolderInfo>();
        var diskFiles = new List<SourceModel.DiskFileInfo>();
        ScanDirectory(rootPath, rootPath, folders, diskFiles, DateTime.UtcNow);

        // Apply lock state if .cop-lock exists
        var lockFile = LockFile.Load(rootPath);
        if (lockFile != null)
            ApplyLockState(lockFile, rootPath, diskFiles);

        // Phase 2: pack into DataObject[] with shared string heap
        var builder = new DataObjectBuilder();

        var fileRecords = new DataObject[diskFiles.Count];
        for (int i = 0; i < diskFiles.Count; i++)
        {
            var f = diskFiles[i];
            long pathPacked = builder.PackString(f.Path);
            fileRecords[i].Slots[0] = pathPacked;                      // Path
            fileRecords[i].Slots[1] = builder.PackString(f.Name);      // Name
            fileRecords[i].Slots[2] = builder.PackString(f.Extension); // Extension
            fileRecords[i].Slots[3] = f.Size;                          // Size
            fileRecords[i].Slots[4] = builder.PackString(f.Folder);    // Folder
            fileRecords[i].Slots[5] = f.Depth;                         // Depth
            fileRecords[i].Slots[6] = f.MinutesSinceModified;          // MinutesSinceModified
            fileRecords[i].Slots[7] = pathPacked;                      // Source = Path (same heap entry)
            fileRecords[i].Slots[8] = builder.PackString(f.Checksum);  // Checksum
            fileRecords[i].Slots[9] = f.IsLocked ? 1L : 0L;           // Locked
            fileRecords[i].Slots[10] = builder.PackString(f.LockStatus); // LockStatus
        }

        var folderRecords = new DataObject[folders.Count];
        for (int i = 0; i < folders.Count; i++)
        {
            var f = folders[i];
            long pathPacked = builder.PackString(f.Path);
            folderRecords[i].Slots[0] = pathPacked;                    // Path
            folderRecords[i].Slots[1] = builder.PackString(f.Name);    // Name
            folderRecords[i].Slots[2] = f.IsEmpty ? 1L : 0L;          // Empty
            folderRecords[i].Slots[3] = f.FileCount;                   // FileCount
            folderRecords[i].Slots[4] = f.SubfolderCount;              // SubfolderCount
            folderRecords[i].Slots[5] = f.Depth;                       // Depth
            folderRecords[i].Slots[6] = f.MinutesSinceModified;        // MinutesSinceModified
            folderRecords[i].Slots[7] = pathPacked;                    // Source = Path
        }

        // One shared heap for both collections
        var heap = builder.GetStringHeap();

        return new Dictionary<string, DataTable>
        {
            ["DiskFiles"] = new DataTable(fileRecords, heap, "DiskFile"),
            ["Folders"] = new DataTable(folderRecords, heap, "Folder"),
        };
    }

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules",
        ".nuget", ".dotnet", "TestResults",
        "__pycache__", ".mypy_cache", ".pytest_cache",
        "dist", ".next", ".cache"
    };

    private static void ScanDirectory(string dir, string root,
        List<SourceModel.FolderInfo> folders, List<SourceModel.DiskFileInfo> diskFiles, DateTime now)
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

        foreach (var filePath in childFiles)
        {
            var fileRelPath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
            var fi = new FileInfo(filePath);
            var minutesSinceModified = (int)(now - fi.LastWriteTimeUtc).TotalMinutes;
            diskFiles.Add(new SourceModel.DiskFileInfo(
                Path: fileRelPath,
                Name: fi.Name,
                Extension: fi.Extension,
                Size: fi.Length,
                Folder: relativePath,
                Depth: depth,
                MinutesSinceModified: minutesSinceModified));
        }

        if (relativePath.Length > 0)
        {
            var dirInfo = new DirectoryInfo(dir);
            var minutesSinceModified = (int)(now - dirInfo.LastWriteTimeUtc).TotalMinutes;
            folders.Add(new SourceModel.FolderInfo(
                Path: relativePath,
                Name: Path.GetFileName(dir),
                IsEmpty: childDirs.Length == 0 && childFiles.Length == 0,
                FileCount: childFiles.Length,
                SubfolderCount: childDirs.Length,
                Depth: depth,
                MinutesSinceModified: minutesSinceModified));
        }

        foreach (var childDir in childDirs)
        {
            var dirName = Path.GetFileName(childDir);
            if (ExcludedDirectoryNames.Contains(dirName)) continue;
            ScanDirectory(childDir, root, folders, diskFiles, now);
        }
    }

    private static void ApplyLockState(LockFile lockFile, string rootPath, List<SourceModel.DiskFileInfo> diskFiles)
    {
        var verifications = lockFile.VerifyChecksums(rootPath);
        var verificationMap = verifications.ToDictionary(v => v.RelativePath, v => v);

        for (int i = 0; i < diskFiles.Count; i++)
        {
            var file = diskFiles[i];
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

        foreach (var (path, verification) in verificationMap)
        {
            if (verification.Status == "deleted")
            {
                var folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                var name = Path.GetFileName(path);
                var ext = Path.GetExtension(path);
                var depth = path.Count(c => c == '/');
                if (folder.Length > 0) depth = folder.Count(c => c == '/') + 1;

                diskFiles.Add(new SourceModel.DiskFileInfo(
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
}
