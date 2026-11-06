// Finds package metadata and source content in local directories.

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
    /// Recursively searches group folders (directories without cop.json).
    /// </summary>
    public List<string> ListPackages(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return new List<string>();
        }

        List<string> packages = new List<string>();
        CollectPackageNames(directoryPath, packages);
        return packages.OrderBy(name => name).ToList();
    }

    private static void CollectPackageNames(string dirPath, List<string> packages)
    {
        foreach (string subDir in Directory.GetDirectories(dirPath))
        {
            string name = Path.GetFileName(subDir);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
            {
                continue;
            }

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
        string? packageDir = FindPackagePath(feedDirectory, packageName);
        Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();

        if (packageDir is null)
        {
            throw new PackageNotFoundException($"Package '{packageName}' not found under '{feedDirectory}'");
        }

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
        string? packageDir = FindPackagePath(feedDirectory, packageName);
        if (packageDir is null)
        {
            throw new PackageNotFoundException($"Package '{packageName}' not found under '{feedDirectory}'");
        }

        // Prefer cop.json
        string jsonPath = Path.Combine(packageDir, PackageMetadata.MetadataFileName);
        if (File.Exists(jsonPath))
        {
            string json = await File.ReadAllTextAsync(jsonPath, ct);
            return PackageMetadata.ParseFromJson(json);
        }

        // Legacy fallback: {name}.md
        string metadataPath = Path.Combine(packageDir, $"{packageName}.md");
        if (!File.Exists(metadataPath))
        {
            throw new PackageNotFoundException($"Metadata file not found: {metadataPath}");
        }

        string content = await File.ReadAllTextAsync(metadataPath, ct);
        return PackageMetadata.ParseFromMarkdown(content);
    }

    private static async Task CollectFilesRecursiveAsync(
        string dirPath,
        string relativePath,
        Dictionary<string, byte[]> files,
        CancellationToken ct)
    {
        foreach (string filePath in Directory.GetFiles(dirPath))
        {
            ct.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(filePath);
            string relPath = string.IsNullOrEmpty(relativePath)
                ? fileName
                : $"{relativePath}/{fileName}";
            files[relPath] = await File.ReadAllBytesAsync(filePath, ct);
        }

        foreach (string subDir in Directory.GetDirectories(dirPath))
        {
            ct.ThrowIfCancellationRequested();
            string dirName = Path.GetFileName(subDir);
            if (dirName.StartsWith('.'))
            {
                continue;
            }

            string relPath = string.IsNullOrEmpty(relativePath)
                ? dirName
                : $"{relativePath}/{dirName}";
            await CollectFilesRecursiveAsync(subDir, relPath, files, ct);
        }
    }

    /// <summary>
    /// Finds a package directory by name, searching recursively through group folders.
    /// A directory is a package if it contains cop.json. Non-package directories are group folders.
    /// </summary>
    public static string? FindPackagePath(string feedDirectory, string packageName)
    {
        if (!Directory.Exists(feedDirectory))
        {
            return null;
        }

        string direct = Path.Combine(feedDirectory, packageName);
        if (Directory.Exists(direct) && IsPackageDir(direct))
        {
            return direct;
        }

        // Recurse into group folders (non-package subdirectories)
        foreach (string subDir in Directory.GetDirectories(feedDirectory))
        {
            string dirName = Path.GetFileName(subDir);
            if (string.IsNullOrEmpty(dirName) || dirName.StartsWith('.'))
            {
                continue;
            }

            if (IsPackageDir(subDir))
            {
                continue;
            }

            string? result = FindPackagePath(subDir, packageName);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if a directory is a package (contains cop.json).
    /// </summary>
    public static bool IsPackageDir(string dirPath)
    {
        return File.Exists(Path.Combine(dirPath, PackageMetadata.MetadataFileName));
    }
}
