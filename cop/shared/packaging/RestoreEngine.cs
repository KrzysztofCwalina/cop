// Restores package graphs from configured sources into the local cache.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace Cop.Core;

/// <summary>
/// Core engine that restores cop packages into a repository.
/// Resolves dependencies, downloads packages, and places files
/// according to cop's file placement rules.
/// </summary>
public class RestoreEngine
{
    private readonly GitHubPackageSource _source;

    /// <summary>
    /// Creates a restore engine backed by the supplied GitHub package source.
    /// </summary>
    public RestoreEngine(GitHubPackageSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// Main restore operation. Resolves dependencies, downloads packages,
    /// places files, and generates configuration.
    /// </summary>
    /// <param name="packages">Package references to restore</param>
    /// <param name="sourceFilePath">Path to the source .cop file (used for checksum manifest identity)</param>
    /// <param name="repoRoot">Repository root directory</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<RestoreResult> RestoreAsync(List<PackageReference> packages, string sourceFilePath, string repoRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        List<string> placedFiles = new List<string>();
        List<string> warnings = new List<string>();

        try
        {
            if (packages.Count == 0)
            {
                return new RestoreResult
                {
                    PlacedFiles = placedFiles,
                    Warnings = warnings,
                    IsSuccessful = true,
                    PackageCount = 0,
                    FileCount = 0
                };
            }

            // Resolve dependencies
            DependencyResolver resolver = new DependencyResolver(_source);
            List<PackageReference> resolvedPackages = await resolver.ResolveAsync(packages, ct);

            List<(string Id, string Version)> nugetAnalyzers = new List<(string Id, string Version)>();
            Dictionary<string, string> diagnosticRules = new Dictionary<string, string>(); // diagnosticId -> severity

            // 3. & 4. Download and place files for each package
            foreach (PackageReference package in resolvedPackages)
            {
                ct.ThrowIfCancellationRequested();

                Dictionary<string, byte[]> packageFiles = await _source.DownloadPackageFilesAsync(package, ct);
                await PlacePackageFilesAsync(packageFiles, package, repoRoot, placedFiles, warnings, nugetAnalyzers, diagnosticRules);
            }

            // 5. Generate Directory.Build.targets
            await GenerateDirectoryBuildTargetsAsync(repoRoot, placedFiles, warnings);

            // 6. Generate analyzers.globalconfig
            await GenerateAnalyzersGlobalConfigAsync(repoRoot, nugetAnalyzers, diagnosticRules, placedFiles, warnings);

            // 7. Compute checksums and save manifest
            ChecksumManager checksumManager = new ChecksumManager(sourceFilePath, repoRoot);
            Dictionary<string, string> manifest = new Dictionary<string, string>();

            // Add checksums for all placed files
            foreach (string filePath in placedFiles)
            {
                try
                {
                    byte[] fileBytes = await File.ReadAllBytesAsync(filePath, ct);
                    string checksum = ChecksumManager.ComputeSha256FromBytes(fileBytes);
                    string relativePath = Path.GetRelativePath(repoRoot, filePath);
                    manifest[relativePath] = checksum;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or SecurityException or CryptographicException)
                {
                    warnings.Add($"Failed to compute checksum for {filePath}: {ex.Message}");
                }
            }

            checksumManager.SaveManifest(manifest);

            return new RestoreResult
            {
                IsSuccessful = true,
                PackageCount = resolvedPackages.Count,
                FileCount = placedFiles.Count,
                PlacedFiles = placedFiles,
                Warnings = warnings
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or SecurityException or HttpRequestException or PackageNotFoundException
            or CircularDependencyException or InvalidOperationException or ArgumentException
            or FormatException or System.Text.Json.JsonException or YamlDotNet.Core.YamlException)
        {
            warnings.Add($"Restore failed: {ex.Message}");
            return new RestoreResult { PlacedFiles = placedFiles, Warnings = warnings, IsSuccessful = false };
        }
    }

    /// <summary>
    /// Places package files according to Cop's file placement rules.
    /// </summary>
    private async Task PlacePackageFilesAsync(
        Dictionary<string, byte[]> packageFiles,
        PackageReference package,
        string repoRoot,
        List<string> placedFiles,
        List<string> warnings,
        List<(string Id, string Version)> nugetAnalyzers,
        Dictionary<string, string> diagnosticRules)
    {
        // Place package instructions once (gather-then-write) so repeated restores are idempotent
        // and the placed file carries an applyTo front-matter block. Instruction files are skipped
        // in the per-file loop below.
        PlaceInstructions(packageFiles, package, repoRoot, placedFiles, warnings);

        foreach ((string? relativePath, byte[]? content) in packageFiles)
        {
            try
            {
                // Skip metadata files
                if (relativePath.Equals(PackageMetadata.MetadataFileName, StringComparison.OrdinalIgnoreCase)
                    || relativePath.Equals($"{package.PackageName}.md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? targetPath = null;

                // Instructions are placed once before this loop (see PlaceInstructions). Skip here.
                if (relativePath.StartsWith("instructions/", StringComparison.OrdinalIgnoreCase) &&
                    relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // Skills: skills/*.md → .github/skills/{packageName}/SKILL.md
                else if (relativePath.StartsWith("skills/", StringComparison.OrdinalIgnoreCase) &&
                         relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    string skillsDir = Path.Combine(repoRoot, ".github", "skills", package.PackageName);
                    Directory.CreateDirectory(skillsDir);
                    targetPath = Path.Combine(skillsDir, "SKILL.md");

                    // Append to existing file if it exists
                    if (File.Exists(targetPath))
                    {
                        byte[] existingContent = await File.ReadAllBytesAsync(targetPath);
                        string newContent = Encoding.UTF8.GetString(existingContent) + "\n\n" + Encoding.UTF8.GetString(content);
                        await File.WriteAllBytesAsync(targetPath, Encoding.UTF8.GetBytes(newContent));
                    }
                    else
                    {
                        await File.WriteAllBytesAsync(targetPath, content);
                    }
                }
                // Analyzers: src/analyzers/*.dll or checks/analyzers/*.dll → .cop/analyzers/{filename}
                else if ((relativePath.StartsWith("src/analyzers/", StringComparison.OrdinalIgnoreCase) ||
                          relativePath.StartsWith("checks/analyzers/", StringComparison.OrdinalIgnoreCase)) &&
                         relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    string analyzersDir = Path.Combine(repoRoot, ".cop", "analyzers");
                    Directory.CreateDirectory(analyzersDir);
                    string fileName = Path.GetFileName(relativePath);
                    targetPath = Path.Combine(analyzersDir, fileName);
                    await File.WriteAllBytesAsync(targetPath, content);
                }
                // NuGet analyzers: src/nuget-analyzers.yaml or checks/nuget-analyzers.yaml
                else if (relativePath.Equals("src/nuget-analyzers.yaml", StringComparison.OrdinalIgnoreCase) ||
                         relativePath.Equals("checks/nuget-analyzers.yaml", StringComparison.OrdinalIgnoreCase))
                {
                    string yamlContent = Encoding.UTF8.GetString(content);
                    ParseNuGetAnalyzersYaml(yamlContent, nugetAnalyzers, warnings);
                    continue; // Don't place this file
                }
                // Rules: src/rules.yaml or checks/rules.yaml → .cop/rules/{packageName}.rules.yaml
                else if (relativePath.Equals("src/rules.yaml", StringComparison.OrdinalIgnoreCase) ||
                         relativePath.Equals("checks/rules.yaml", StringComparison.OrdinalIgnoreCase))
                {
                    string rulesDir = Path.Combine(repoRoot, ".cop", "rules");
                    Directory.CreateDirectory(rulesDir);
                    targetPath = Path.Combine(rulesDir, $"{package.PackageName}.rules.yaml");
                    await File.WriteAllBytesAsync(targetPath, content);

                    // Parse rules to extract diagnostic IDs
                    string rulesContent = Encoding.UTF8.GetString(content);
                    ParseRulesYaml(rulesContent, diagnosticRules, warnings);
                }
                // Check files: checks/*.cop → .cop/checks/{packageName}.{filename} (legacy)
                else if (relativePath.StartsWith("checks/", StringComparison.OrdinalIgnoreCase) &&
                         relativePath.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
                {
                    string checksDir = Path.Combine(repoRoot, ".cop", "checks");
                    Directory.CreateDirectory(checksDir);
                    string fileName = Path.GetFileName(relativePath);
                    targetPath = Path.Combine(checksDir, $"{package.PackageName}.{fileName}");
                    await File.WriteAllBytesAsync(targetPath, content);
                }
                // Other files → .cop/packages/{packageName}/{relativePath}
                else
                {
                    string packageDir = Path.Combine(repoRoot, ".cop", "packages", package.PackageName);
                    targetPath = ValidatePackagePath(relativePath, packageDir);
                    string? targetDir = Path.GetDirectoryName(targetPath);
                    if (targetDir is not null)
                    {
                        Directory.CreateDirectory(targetDir);
                    }
                    await File.WriteAllBytesAsync(targetPath, content);
                }

                if (targetPath != null && !placedFiles.Contains(targetPath))
                {
                    placedFiles.Add(targetPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or SecurityException or ArgumentException)
            {
                warnings.Add($"Failed to place file {relativePath} from package {package.PackageName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Places a package's instructions/*.md into .github/instructions/{packageName}.instructions.md
    /// once, with an applyTo front-matter block (from the package's cop.json), idempotently.
    /// </summary>
    private static void PlaceInstructions(
        Dictionary<string, byte[]> packageFiles,
        PackageReference package,
        string repoRoot,
        List<string> placedFiles,
        List<string> warnings)
    {
        try
        {
            List<(string, string)> instructionFiles = packageFiles
                .Where(kv => kv.Key.StartsWith("instructions/", StringComparison.OrdinalIgnoreCase)
                          && kv.Key.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .Select(kv => (Path.GetFileName(kv.Key), Encoding.UTF8.GetString(kv.Value)))
                .ToList();

            if (instructionFiles.Count == 0)
            {
                return;
            }

            string applyTo = string.Empty;
            KeyValuePair<string, byte[]> metaEntry = packageFiles.FirstOrDefault(kv =>
                kv.Key.Equals(PackageMetadata.MetadataFileName, StringComparison.OrdinalIgnoreCase));
            if (metaEntry.Value != null)
            {
                try { applyTo = PackageMetadata.ParseFromJson(Encoding.UTF8.GetString(metaEntry.Value)).ApplyTo; }
                catch (Exception exception) when (exception is System.Text.Json.JsonException
                    or ArgumentException)
                {
                    warnings.Add(
                        $"Cannot read instruction metadata for {package.PackageName}: {exception.Message}");
                }
            }

            string? targetPath = InstructionPlacement.Place(
                repoRoot, package.PackageName, applyTo, instructionFiles, out _);

            if (targetPath != null && !placedFiles.Contains(targetPath))
            {
                placedFiles.Add(targetPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or SecurityException or ArgumentException)
        {
            warnings.Add($"Failed to place instructions from package {package.PackageName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates Directory.Build.targets with analyzer references.
    /// </summary>
    private async Task GenerateDirectoryBuildTargetsAsync(string repoRoot, List<string> placedFiles, List<string> warnings)
    {
        try
        {
            string targetsDir = Path.Combine(repoRoot, ".cop");
            Directory.CreateDirectory(targetsDir);
            string targetsPath = Path.Combine(targetsDir, "Directory.Build.targets");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<Project>");
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <!-- Analyzer references from cop packages -->");

            // Find all DLL files in .cop/analyzers
            string analyzersDir = Path.Combine(repoRoot, ".cop", "analyzers");
            if (Directory.Exists(analyzersDir))
            {
                foreach (string dllFile in Directory.GetFiles(analyzersDir, "*.dll"))
                {
                    string relativePath = Path.GetRelativePath(targetsDir, dllFile);
                    // Normalize to forward slashes for MSBuild
                    relativePath = relativePath.Replace("\\", "/");
                    sb.AppendLine($"    <Analyzer Include=\"{relativePath}\" />");
                }
            }

            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("</Project>");

            string content = sb.ToString();
            await File.WriteAllTextAsync(targetsPath, content);
            placedFiles.Add(targetsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or SecurityException)
        {
            warnings.Add($"Failed to generate Directory.Build.targets: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates analyzers.globalconfig with diagnostic severity overrides.
    /// </summary>
    private async Task GenerateAnalyzersGlobalConfigAsync(
        string repoRoot,
        List<(string Id, string Version)> nugetAnalyzers,
        Dictionary<string, string> diagnosticRules,
        List<string> placedFiles,
        List<string> warnings)
    {
        try
        {
            string copDir = Path.Combine(repoRoot, ".cop");
            Directory.CreateDirectory(copDir);
            string globalConfigPath = Path.Combine(copDir, "analyzers.globalconfig");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("is_global = true");
            sb.AppendLine();
            sb.AppendLine("# Analyzer severity overrides from cop packages");

            foreach ((string? diagnosticId, string? severity) in diagnosticRules.OrderBy(x => x.Key))
            {
                sb.AppendLine($"dotnet_diagnostic.{diagnosticId}.severity = {severity}");
            }

            string content = sb.ToString();
            await File.WriteAllTextAsync(globalConfigPath, content);
            placedFiles.Add(globalConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or SecurityException)
        {
            warnings.Add($"Failed to generate analyzers.globalconfig: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses nuget-analyzers.yaml to extract analyzer references.
    /// Expected format:
    /// analyzers:
    ///   - id: NUnit.Analyzers
    ///     version: 4.5.0
    /// </summary>
    private void ParseNuGetAnalyzersYaml(string yamlContent, List<(string Id, string Version)> nugetAnalyzers, List<string> warnings)
    {
        try
        {
            IDeserializer deserializer = new DeserializerBuilder().Build();
            Dictionary<object, object> yaml = deserializer.Deserialize<Dictionary<object, object>>(yamlContent);

            if (yaml != null && yaml.TryGetValue("analyzers", out object? analyzersObj))
            {
                if (analyzersObj is List<object> analyzersList)
                {
                    foreach (object analyzer in analyzersList)
                    {
                        if (analyzer is Dictionary<object, object> analyzerDict)
                        {
                            if (analyzerDict.TryGetValue("id", out object? idObj) &&
                                analyzerDict.TryGetValue("version", out object? versionObj))
                            {
                                string? id = idObj?.ToString();
                                string? version = versionObj?.ToString();
                                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(version))
                                {
                                    nugetAnalyzers.Add((id, version));
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            // A malformed nuget-analyzers.yaml must not silently produce zero analyzers — surface it.
            warnings.Add($"Failed to parse nuget-analyzers.yaml: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses rules.yaml to extract diagnostic IDs and their severity levels.
    /// Looks for patterns like "dotnet_diagnostic.{id}.severity = {severity}".
    /// </summary>
    private void ParseRulesYaml(string yamlContent, Dictionary<string, string> diagnosticRules, List<string> warnings)
    {
        // Parsing is bounds-checked throughout; unexpected implementation errors must
        // propagate rather than turning a broken parser into an empty rule configuration.
        foreach (string rawLine in yamlContent.Split('\n'))
        {
            const string marker = "dotnet_diagnostic.";
            int idx = rawLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            int p = idx + marker.Length;
            int idStart = p;
            while (p < rawLine.Length && rawLine[p] is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                p++;
            }

            if (p == idStart)
            {
                continue;
            }

            string id = rawLine.Substring(idStart, p - idStart).Trim();

            while (p < rawLine.Length && char.IsWhiteSpace(rawLine[p]))
            {
                p++;
            }

            if (p >= rawLine.Length || rawLine[p] != '=')
            {
                continue;
            }

            p++;
            while (p < rawLine.Length && char.IsWhiteSpace(rawLine[p]))
            {
                p++;
            }

            if (p >= rawLine.Length)
            {
                continue;
            }

            string severity = rawLine.Substring(p).Trim();
            if (!diagnosticRules.ContainsKey(id))
            {
                diagnosticRules[id] = severity;
            }
        }
    }

    /// <summary>
    /// Validates that a relative path from a package doesn't escape the intended directory.
    /// Prevents path traversal attacks via sequences like ../../ in package file paths.
    /// </summary>
    internal static string ValidatePackagePath(string relativePath, string baseDir)
    {
        // Anchor on the REAL base directory (not a CWD-relative dummy, which made the check depend
        // on the process working directory). The combined path must stay within baseDir.
        string fullBase = Path.GetFullPath(baseDir);
        string combined = Path.GetFullPath(Path.Combine(fullBase, relativePath));

        if (!combined.StartsWith(fullBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !combined.Equals(fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"Invalid package path (potential path traversal): {relativePath}");
        }

        return Path.Combine(baseDir, relativePath);
    }
}
