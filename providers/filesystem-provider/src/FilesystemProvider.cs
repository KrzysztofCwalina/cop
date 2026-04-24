using System.Text.Json;
using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Built-in provider for filesystem data (Folders and DiskFiles).
/// Uses the fast Objects path — stride-based DataTable records with a shared UTF-8 string heap.
/// No per-record CLR objects or CLR strings allocated for the permanent data.
/// </summary>
public class FilesystemProvider : DataProvider
{
    public override DataFormat SupportedFormats => DataFormat.InMemoryDatabase;


    public override ReadOnlyMemory<byte> GetSchema()
    {
        var buffer = new MemoryStream();
        using var w = new Utf8JsonWriter(buffer);
        w.WriteStartObject();

        w.WriteStartArray("types"u8);

        // Folder
        w.WriteStartObject();
        w.WriteString("name"u8, "Folder"u8);
        w.WriteStartArray("properties"u8);
        WriteProp(w, "Path"u8);
        WriteProp(w, "Name"u8);
        WriteProp(w, "Empty"u8, "bool"u8);
        WriteProp(w, "FileCount"u8, "int"u8);
        WriteProp(w, "SubfolderCount"u8, "int"u8);
        WriteProp(w, "Depth"u8, "int"u8);
        WriteProp(w, "MinutesSinceModified"u8, "int"u8);
        WriteProp(w, "Source"u8);
        w.WriteEndArray();
        w.WriteEndObject();

        // DiskFile
        w.WriteStartObject();
        w.WriteString("name"u8, "DiskFile"u8);
        w.WriteStartArray("properties"u8);
        WriteProp(w, "Path"u8);
        WriteProp(w, "Name"u8);
        WriteProp(w, "Extension"u8);
        WriteProp(w, "Size"u8, "int"u8);
        WriteProp(w, "Folder"u8);
        WriteProp(w, "Depth"u8, "int"u8);
        WriteProp(w, "MinutesSinceModified"u8, "int"u8);
        WriteProp(w, "Source"u8);
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteEndArray(); // types

        w.WriteStartArray("collections"u8);
        WriteColl(w, "Folders"u8, "Folder"u8);
        WriteColl(w, "DiskFiles"u8, "DiskFile"u8);
        w.WriteEndArray();

        w.WriteEndObject();
        w.Flush();
        return buffer.TryGetBuffer(out var segment)
            ? segment.AsMemory()
            : buffer.ToArray();
    }

    private static void WriteProp(Utf8JsonWriter w, ReadOnlySpan<byte> name, ReadOnlySpan<byte> type = default)
    {
        w.WriteStartObject();
        w.WriteString("name"u8, name);
        if (type.Length > 0) w.WriteString("type"u8, type);
        w.WriteEndObject();
    }

    private static void WriteColl(Utf8JsonWriter w, ReadOnlySpan<byte> name, ReadOnlySpan<byte> itemType)
    {
        w.WriteStartObject();
        w.WriteString("name"u8, name);
        w.WriteString("itemType"u8, itemType);
        w.WriteEndObject();
    }

    public override DataStore QueryData(ProviderQuery query)
    {
        var rootPath = Path.GetFullPath(query.RootPath
            ?? throw new ArgumentException("RootPath is required for FilesystemProvider."));

        var requested = query.RequestedCollections;
        bool wantFiles = requested is null || requested.Any(c => c.Equals("DiskFiles", StringComparison.OrdinalIgnoreCase));
        bool wantFolders = requested is null || requested.Any(c => c.Equals("Folders", StringComparison.OrdinalIgnoreCase));

        int maxDepth = ExtractMaxDepth(query.Filter);

        var db = new DataStoreBuilder();
        var files = wantFiles ? db.AddTable("DiskFiles", "DiskFile", stride: 8) : null;
        var folders = wantFolders ? db.AddTable("Folders", "Folder", stride: 8) : null;

        ScanDirectory(rootPath, rootPath, files, folders, query.ExcludedDirectories, query.Filter, maxDepth, DateTime.UtcNow, depth: 0);

        return db.Build();
    }

    private static void ScanDirectory(string dir, string root,
        DataTableBuilder? files, DataTableBuilder? folders,
        IReadOnlySet<string>? excludedDirs, FilterExpression? filter, int maxDepth, DateTime now, int depth)
    {
        if (maxDepth >= 0 && depth > maxDepth)
            return;

        IEnumerable<string> childDirs;
        IEnumerable<string> childFiles;

        try
        {
            childDirs = Directory.EnumerateDirectories(dir);
            childFiles = Directory.EnumerateFiles(dir);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        var relativePath = Path.GetRelativePath(root, dir).Replace('\\', '/');
        if (relativePath == ".") relativePath = "";

        // Write files directly to the DataTableBuilder
        int fileCount = 0;
        if (files is not null)
        {
            foreach (var filePath in childFiles)
            {
                fileCount++;
                var fi = new FileInfo(filePath);
                var fileRelPath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
                var minutesSinceModified = (int)(now - fi.LastWriteTimeUtc).TotalMinutes;
                var size = fi.Length;
                var name = fi.Name;
                var ext = fi.Extension;

                if (!FilterEvaluator.Matches(filter, prop => prop switch
                {
                    "Path" => fileRelPath,
                    "Name" => name,
                    "Extension" => ext,
                    "Size" => size,
                    "Folder" => relativePath,
                    "Depth" => depth,
                    "MinutesSinceModified" => minutesSinceModified,
                    "Source" => fileRelPath,
                    _ => throw new ArgumentException($"Unknown DiskFile property in filter: '{prop}'")
                }))
                    continue;

                int row = files.AddRow();
                long pathRef = files.PackString(fileRelPath);
                files.SetSlot(row, 0, pathRef);              // Path
                files.SetString(row, 1, name);               // Name
                files.SetString(row, 2, ext);                // Extension
                files.SetLong(row, 3, size);                 // Size
                files.SetString(row, 4, relativePath);       // Folder
                files.SetInt(row, 5, depth);                 // Depth
                files.SetInt(row, 6, minutesSinceModified);  // MinutesSinceModified
                files.SetSlot(row, 7, pathRef);              // Source = Path
            }
        }
        else
        {
            foreach (var _ in childFiles)
                fileCount++;
        }

        // Enumerate child directories, prune excluded, and recurse
        var includedDirs = new List<string>();
        int subfolderCount = 0;
        foreach (var childDir in childDirs)
        {
            subfolderCount++;
            var dirName = Path.GetFileName(childDir);
            if (excludedDirs is null || !excludedDirs.Contains(dirName))
                includedDirs.Add(childDir);
        }

        // Write folder directly to the DataTableBuilder (skip root)
        if (folders is not null && relativePath.Length > 0)
        {
            var dirName = Path.GetFileName(dir);
            var dirInfo = new DirectoryInfo(dir);
            var minutesSinceModified = (int)(now - dirInfo.LastWriteTimeUtc).TotalMinutes;
            bool empty = subfolderCount == 0 && fileCount == 0;

            if (FilterEvaluator.Matches(filter, prop => prop switch
            {
                "Path" => relativePath,
                "Name" => dirName,
                "Empty" => empty,
                "FileCount" => fileCount,
                "SubfolderCount" => subfolderCount,
                "Depth" => depth,
                "MinutesSinceModified" => minutesSinceModified,
                "Source" => relativePath,
                _ => throw new ArgumentException($"Unknown Folder property in filter: '{prop}'")
            }))
            {
                int row = folders.AddRow();
                long pathRef = folders.PackString(relativePath);
                folders.SetSlot(row, 0, pathRef);            // Path
                folders.SetString(row, 1, dirName);          // Name
                folders.SetBool(row, 2, empty);              // Empty
                folders.SetInt(row, 3, fileCount);           // FileCount
                folders.SetInt(row, 4, subfolderCount);      // SubfolderCount
                folders.SetInt(row, 5, depth);               // Depth
                folders.SetInt(row, 6, minutesSinceModified);// MinutesSinceModified
                folders.SetSlot(row, 7, pathRef);            // Source = Path
            }
        }

        foreach (var childDir in includedDirs)
        {
            ScanDirectory(childDir, root, files, folders, excludedDirs, filter, maxDepth, now, depth + 1);
        }
    }

    /// <summary>
    /// Extracts a max-depth limit from the filter for scan-level pruning.
    /// This is the one structural optimization — it avoids recursing into
    /// directories beyond the requested depth, which no post-hoc filter can do.
    /// Returns -1 if no depth limit is found.
    /// </summary>
    private static int ExtractMaxDepth(FilterExpression? filter)
    {
        if (filter is null) return -1;

        foreach (var condition in Flatten(filter))
        {
            switch (condition)
            {
                case ComparisonFilter { Property: "Depth", Op: CompareOp.LessThan } cf:
                    return Math.Max(0, (int)cf.Value - 1);
                case ComparisonFilter { Property: "Depth", Op: CompareOp.LessOrEqual } cf:
                    return Math.Max(0, (int)cf.Value);
            }
        }
        return -1;
    }

    private static IEnumerable<FilterExpression> Flatten(FilterExpression expr)
    {
        if (expr is AndFilter and)
        {
            foreach (var c in and.Conditions)
                foreach (var inner in Flatten(c))
                    yield return inner;
        }
        else
        {
            yield return expr;
        }
    }
}
