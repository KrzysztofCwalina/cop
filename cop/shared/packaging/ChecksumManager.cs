using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cop.Core
{
    /// <summary>
    /// Represents a checksum violation found during verification
    /// </summary>
    public record ChecksumViolation(string FilePath, string ViolationType, string? Expected, string? Actual);

    /// <summary>
    /// Manages SHA256 checksums of generated files and stores manifests in ~/.cop/checksums/
    /// </summary>
    public class ChecksumManager
    {
        private readonly string _projectFilePath;
        private readonly string _repoRoot;
        private readonly string _checksumsDirectory;

        /// <summary>
        /// Initialize ChecksumManager with project file path and repo root
        /// </summary>
        /// <param name="projectFilePath">Absolute path to the .cop source file</param>
        /// <param name="repoRoot">Absolute path to the repository root for resolving relative paths</param>
        public ChecksumManager(string projectFilePath, string repoRoot)
        {
            _projectFilePath = Path.GetFullPath(projectFilePath);
            _repoRoot = Path.GetFullPath(repoRoot);
            
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _checksumsDirectory = Path.Combine(userProfile, ".cop", "checksums");
        }

        /// <summary>
        /// Compute SHA256 checksum of a file
        /// </summary>
        public static string ComputeSha256(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var fileStream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(fileStream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Compute SHA256 checksum from byte array
        /// </summary>
        public static string ComputeSha256FromBytes(byte[] data)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Get the full path to the manifest file for this project
        /// </summary>
        public string GetManifestPath()
        {
            var projectPathBytes = Encoding.UTF8.GetBytes(_projectFilePath);
            var projectHash = ComputeSha256FromBytes(projectPathBytes);
            return Path.Combine(_checksumsDirectory, $"{projectHash}.json");
        }

        /// <summary>
        /// Save the file checksums manifest to disk
        /// </summary>
        public void SaveManifest(Dictionary<string, string> fileChecksums)
        {
            Directory.CreateDirectory(_checksumsDirectory);

            var manifest = new
            {
                projectFile = _projectFilePath,
                generatedAt = DateTime.UtcNow.ToString("O"),
                files = fileChecksums
            };

            var manifestPath = GetManifestPath();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(manifest, options);
            File.WriteAllText(manifestPath, json);
        }

        /// <summary>
        /// Load the file checksums manifest from disk
        /// </summary>
        /// <returns>Dictionary of file paths to checksums, or null if manifest doesn't exist</returns>
        public Dictionary<string, string>? LoadManifest()
        {
            var manifestPath = GetManifestPath();
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(manifestPath);
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("files", out var filesElement) && 
                        filesElement.ValueKind == JsonValueKind.Object)
                    {
                        var files = new Dictionary<string, string>();
                        foreach (var property in filesElement.EnumerateObject())
                        {
                            if (property.Value.ValueKind == JsonValueKind.String)
                            {
                                files[property.Name] = property.Value.GetString() ?? string.Empty;
                            }
                        }
                        return files;
                    }
                }
            }
            catch (JsonException)
            {
                // Invalid JSON, treat as no manifest
                return null;
            }

            return null;
        }

        /// <summary>
        /// Verify all files in the manifest by recomputing their checksums
        /// </summary>
        /// <returns>List of checksum violations found, empty list if all files are valid</returns>
        public List<ChecksumViolation> Verify()
        {
            var violations = new List<ChecksumViolation>();
            var manifest = LoadManifest();

            if (manifest == null)
            {
                return violations;
            }

            foreach (var kvp in manifest)
            {
                var relativePath = kvp.Key;
                var expectedChecksum = kvp.Value;
                var fullPath = Path.Combine(_repoRoot, relativePath);

                if (!File.Exists(fullPath))
                {
                    violations.Add(new ChecksumViolation(
                        FilePath: relativePath,
                        ViolationType: "Missing",
                        Expected: expectedChecksum,
                        Actual: null
                    ));
                }
                else
                {
                    try
                    {
                        var actualChecksum = ComputeSha256(fullPath);
                        if (actualChecksum != expectedChecksum)
                        {
                            violations.Add(new ChecksumViolation(
                                FilePath: relativePath,
                                ViolationType: "Modified",
                                Expected: expectedChecksum,
                                Actual: actualChecksum
                            ));
                        }
                    }
                    catch (Exception ex)
                    {
                        violations.Add(new ChecksumViolation(
                            FilePath: relativePath,
                            ViolationType: "Error",
                            Expected: expectedChecksum,
                            Actual: ex.Message
                        ));
                    }
                }
            }

            return violations;
        }

        /// <summary>
        /// Add or update a file in the manifest
        /// </summary>
        public void AddFile(string relativePath, string checksum)
        {
            var manifest = LoadManifest() ?? new Dictionary<string, string>();
            manifest[relativePath] = checksum;
            SaveManifest(manifest);
        }

        /// <summary>
        /// Remove a file from the manifest
        /// </summary>
        public void RemoveFile(string relativePath)
        {
            var manifest = LoadManifest();
            if (manifest != null && manifest.ContainsKey(relativePath))
            {
                manifest.Remove(relativePath);
                SaveManifest(manifest);
            }
        }
    }
}
