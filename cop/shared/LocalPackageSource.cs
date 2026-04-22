using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cop.Core;

/// <summary>
/// Reads packages from a local directory. The directory is expected to contain
/// subdirectories, each representing a package (same layout as packages/{packageName}/ in a repo).
/// </summary>
public class LocalPackageSource
{
    /// <summary>
    /// Lists all packages (subdirectories) in the given local directory.
    /// Recursively searches group folders (directories without a {dirName}.md metadata file).
    /// </summary>
    public List<string> ListPackages(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return new List<string>();

        var packages = new List<string>();
        CollectPackageNames(directoryPath, packages);
        return packages.OrderBy(name => name).ToList();
    }

    private static void CollectPackageNames(string dirPath, List<string> packages)
    {
        foreach (var subDir in Directory.GetDirectories(dirPath))
        {
            var name = Path.GetFileName(subDir);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;

            if (IsPackageDir(subDir))
            {
                packages.Add(name);
            }
            else
            {
                // Group folder — recurse
                CollectPackageNames(subDir, packages);
            }
        }
    }

    /// <summary>
    /// Downloads all files from a local package directory recursively.
    /// Returns relative paths mapped to file contents (mirrors GitHubPackageSource API).
    /// </summary>
    public async Task<Dictionary<string, byte[]>> DownloadPackageFilesAsync(
        string feedDirectory,
        string packageName,
        CancellationToken ct = default)
    {
        var packageDir = FindPackagePath(feedDirectory, packageName);
        var files = new Dictionary<string, byte[]>();

        if (packageDir is null)
            throw new PackageNotFoundException($"Package '{packageName}' not found under '{feedDirectory}'");

        await CollectFilesRecursiveAsync(packageDir, "", files, ct);
        return files;
    }

    /// <summary>
    /// Reads package metadata from the local package directory.
    /// </summary>
    public async Task<PackageMetadata> GetPackageMetadataAsync(
        string feedDirectory,
        string packageName,
        CancellationToken ct = default)
    {
        var packageDir = FindPackagePath(feedDirectory, packageName);
        if (packageDir is null)
            throw new PackageNotFoundException($"Package '{packageName}' not found under '{feedDirectory}'");

        var metadataPath = Path.Combine(packageDir, $"{packageName}.md");

        if (!File.Exists(metadataPath))
            throw new PackageNotFoundException($"Metadata file not found: {metadataPath}");

        var content = await File.ReadAllTextAsync(metadataPath, ct);
        return PackageMetadata.ParseFromMarkdown(content);
    }

    private static async Task CollectFilesRecursiveAsync(
        string dirPath,
        string relativePath,
        Dictionary<string, byte[]> files,
        CancellationToken ct)
    {
        foreach (var filePath in Directory.GetFiles(dirPath))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(filePath);
            var relPath = string.IsNullOrEmpty(relativePath)
                ? fileName
                : $"{relativePath}/{fileName}";
            files[relPath] = await File.ReadAllBytesAsync(filePath, ct);
        }

        foreach (var subDir in Directory.GetDirectories(dirPath))
        {
            ct.ThrowIfCancellationRequested();
            var dirName = Path.GetFileName(subDir);
            if (dirName.StartsWith('.')) continue;
            var relPath = string.IsNullOrEmpty(relativePath)
                ? dirName
                : $"{relativePath}/{dirName}";
            await CollectFilesRecursiveAsync(subDir, relPath, files, ct);
        }
    }

    /// <summary>
    /// Finds a package directory by name, searching recursively through group folders.
    /// A directory is a package if it contains {dirName}.md. Non-package directories are group folders.
    /// </summary>
    public static string? FindPackagePath(string feedDirectory, string packageName)
    {
        if (!Directory.Exists(feedDirectory)) return null;

        var direct = Path.Combine(feedDirectory, packageName);
        if (Directory.Exists(direct) && IsPackageDir(direct))
            return direct;

        // Recurse into group folders (non-package subdirectories)
        foreach (var subDir in Directory.GetDirectories(feedDirectory))
        {
            var dirName = Path.GetFileName(subDir);
            if (string.IsNullOrEmpty(dirName) || dirName.StartsWith('.')) continue;
            if (IsPackageDir(subDir)) continue;

            var result = FindPackagePath(subDir, packageName);
            if (result is not null) return result;
        }

        return null;
    }

    /// <summary>
    /// Returns true if a directory is a package (contains {dirName}.md, src/, or types/).
    /// </summary>
    public static bool IsPackageDir(string dirPath)
    {
        var dirName = Path.GetFileName(dirPath);
        if (string.IsNullOrEmpty(dirName)) return false;
        if (File.Exists(Path.Combine(dirPath, $"{dirName}.md"))) return true;
        if (Directory.Exists(Path.Combine(dirPath, "src"))) return true;
        if (Directory.Exists(Path.Combine(dirPath, "types"))) return true;
        return false;
    }
}
