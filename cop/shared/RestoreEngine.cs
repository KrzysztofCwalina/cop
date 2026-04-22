using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
        var placedFiles = new List<string>();
        var warnings = new List<string>();

        try
        {
            if (packages.Count == 0)
            {
                return new RestoreResult
                {
                    PlacedFiles = placedFiles,
                    Warnings = warnings,
                    Success = true,
                    PackageCount = 0,
                    FileCount = 0
                };
            }

            // Resolve dependencies
            var resolver = new DependencyResolver(_source);
            var resolvedPackages = await resolver.ResolveAsync(packages, ct);

            var nugetAnalyzers = new List<(string Id, string Version)>();
            var diagnosticRules = new Dictionary<string, string>(); // diagnosticId -> severity

            // 3. & 4. Download and place files for each package
            foreach (var package in resolvedPackages)
            {
                ct.ThrowIfCancellationRequested();

                var packageFiles = await _source.DownloadPackageFilesAsync(package, ct);
                await PlacePackageFilesAsync(packageFiles, package, repoRoot, placedFiles, warnings, nugetAnalyzers, diagnosticRules);
            }

            // 5. Generate Directory.Build.targets
            await GenerateDirectoryBuildTargetsAsync(repoRoot, placedFiles, warnings);

            // 6. Generate analyzers.globalconfig
            await GenerateAnalyzersGlobalConfigAsync(repoRoot, nugetAnalyzers, diagnosticRules, placedFiles, warnings);

            // 7. Compute checksums and save manifest
            var checksumManager = new ChecksumManager(sourceFilePath, repoRoot);
            var manifest = new Dictionary<string, string>();

            // Add checksums for all placed files
            foreach (var filePath in placedFiles)
            {
                try
                {
                    var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
                    var checksum = ChecksumManager.ComputeSha256FromBytes(fileBytes);
                    var relativePath = Path.GetRelativePath(repoRoot, filePath);
                    manifest[relativePath] = checksum;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to compute checksum for {filePath}: {ex.Message}");
                }
            }

            checksumManager.SaveManifest(manifest);

            return new RestoreResult
            {
                Success = true,
                PackageCount = resolvedPackages.Count,
                FileCount = placedFiles.Count,
                PlacedFiles = placedFiles,
                Warnings = warnings
            };
        }
        catch (OperationCanceledException)
        {
            warnings.Add("Restore operation was cancelled.");
            return new RestoreResult { PlacedFiles = placedFiles, Warnings = warnings, Success = false };
        }
        catch (Exception ex)
        {
            warnings.Add($"Restore failed: {ex.Message}");
            return new RestoreResult { PlacedFiles = placedFiles, Warnings = warnings, Success = false };
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
        foreach (var (relativePath, content) in packageFiles)
        {
            try
            {
                // Skip metadata file
                if (relativePath.Equals($"{package.PackageName}.md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string targetPath = null;

                // Instructions: instructions/*.md → .github/instructions/{packageName}.instructions.md
                if (relativePath.StartsWith("instructions/", StringComparison.OrdinalIgnoreCase) &&
                    relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    var instructionsDir = Path.Combine(repoRoot, ".github", "instructions");
                    Directory.CreateDirectory(instructionsDir);
                    targetPath = Path.Combine(instructionsDir, $"{package.PackageName}.instructions.md");

                    // Append to existing file if it exists
                    if (File.Exists(targetPath))
                    {
                        var existingContent = await File.ReadAllBytesAsync(targetPath);
                        var newContent = Encoding.UTF8.GetString(existingContent) + "\n\n" + Encoding.UTF8.GetString(content);
                        await File.WriteAllBytesAsync(targetPath, Encoding.UTF8.GetBytes(newContent));
                    }
                    else
                    {
                        await File.WriteAllBytesAsync(targetPath, content);
                    }
                }
                // Skills: skills/*.md → .github/skills/{packageName}/SKILL.md
                else if (relativePath.StartsWith("skills/", StringComparison.OrdinalIgnoreCase) &&
                         relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    var skillsDir = Path.Combine(repoRoot, ".github", "skills", package.PackageName);
                    Directory.CreateDirectory(skillsDir);
                    targetPath = Path.Combine(skillsDir, "SKILL.md");

                    // Append to existing file if it exists
                    if (File.Exists(targetPath))
                    {
                        var existingContent = await File.ReadAllBytesAsync(targetPath);
                        var newContent = Encoding.UTF8.GetString(existingContent) + "\n\n" + Encoding.UTF8.GetString(content);
                        await File.WriteAllBytesAsync(targetPath, Encoding.UTF8.GetBytes(newContent));
                    }
                    else
                    {
                        await File.WriteAllBytesAsync(targetPath, content);
                    }
                }
                // Analyzers: checks/analyzers/*.dll → .cop/analyzers/{filename}
                else if (relativePath.StartsWith("checks/analyzers/", StringComparison.OrdinalIgnoreCase) &&
                         relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    var analyzersDir = Path.Combine(repoRoot, ".cop", "analyzers");
                    Directory.CreateDirectory(analyzersDir);
                    var fileName = Path.GetFileName(relativePath);
                    targetPath = Path.Combine(analyzersDir, fileName);
                    await File.WriteAllBytesAsync(targetPath, content);
                }
                // NuGet analyzers: checks/nuget-analyzers.yaml
                else if (relativePath.Equals("checks/nuget-analyzers.yaml", StringComparison.OrdinalIgnoreCase))
                {
                    var yamlContent = Encoding.UTF8.GetString(content);
                    ParseNuGetAnalyzersYaml(yamlContent, nugetAnalyzers);
                    continue; // Don't place this file
                }
                // Rules: checks/rules.yaml → .cop/rules/{packageName}.rules.yaml
                else if (relativePath.Equals("checks/rules.yaml", StringComparison.OrdinalIgnoreCase))
                {
                    var rulesDir = Path.Combine(repoRoot, ".cop", "rules");
                    Directory.CreateDirectory(rulesDir);
                    targetPath = Path.Combine(rulesDir, $"{package.PackageName}.rules.yaml");
                    await File.WriteAllBytesAsync(targetPath, content);
                    
                    // Parse rules to extract diagnostic IDs
                    var rulesContent = Encoding.UTF8.GetString(content);
                    ParseRulesYaml(rulesContent, diagnosticRules);
                }
                // Check files: checks/*.cop → .cop/checks/{packageName}.{filename}
                else if (relativePath.StartsWith("checks/", StringComparison.OrdinalIgnoreCase) &&
                         relativePath.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
                {
                    var checksDir = Path.Combine(repoRoot, ".cop", "checks");
                    Directory.CreateDirectory(checksDir);
                    var fileName = Path.GetFileName(relativePath);
                    targetPath = Path.Combine(checksDir, $"{package.PackageName}.{fileName}");
                    await File.WriteAllBytesAsync(targetPath, content);
                }
                // Other files → .cop/packages/{packageName}/{relativePath}
                else
                {
                    var packageDir = Path.Combine(repoRoot, ".cop", "packages", package.PackageName);
                    targetPath = Path.Combine(packageDir, relativePath);
                    var targetDir = Path.GetDirectoryName(targetPath);
                    Directory.CreateDirectory(targetDir);
                    await File.WriteAllBytesAsync(targetPath, content);
                }

                if (targetPath != null && !placedFiles.Contains(targetPath))
                {
                    placedFiles.Add(targetPath);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to place file {relativePath} from package {package.PackageName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Generates Directory.Build.targets with analyzer references.
    /// </summary>
    private async Task GenerateDirectoryBuildTargetsAsync(string repoRoot, List<string> placedFiles, List<string> warnings)
    {
        try
        {
            var targetsDir = Path.Combine(repoRoot, ".cop");
            Directory.CreateDirectory(targetsDir);
            var targetsPath = Path.Combine(targetsDir, "Directory.Build.targets");

            var sb = new StringBuilder();
            sb.AppendLine("<Project>");
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <!-- Analyzer references from cop packages -->");

            // Find all DLL files in .cop/analyzers
            var analyzersDir = Path.Combine(repoRoot, ".cop", "analyzers");
            if (Directory.Exists(analyzersDir))
            {
                foreach (var dllFile in Directory.GetFiles(analyzersDir, "*.dll"))
                {
                    var relativePath = Path.GetRelativePath(targetsDir, dllFile);
                    // Normalize to forward slashes for MSBuild
                    relativePath = relativePath.Replace("\\", "/");
                    sb.AppendLine($"    <Analyzer Include=\"{relativePath}\" />");
                }
            }

            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("</Project>");

            var content = sb.ToString();
            await File.WriteAllTextAsync(targetsPath, content);
            placedFiles.Add(targetsPath);
        }
        catch (Exception ex)
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
            var copDir = Path.Combine(repoRoot, ".cop");
            Directory.CreateDirectory(copDir);
            var globalConfigPath = Path.Combine(copDir, "analyzers.globalconfig");

            var sb = new StringBuilder();
            sb.AppendLine("is_global = true");
            sb.AppendLine();
            sb.AppendLine("# Analyzer severity overrides from cop packages");

            foreach (var (diagnosticId, severity) in diagnosticRules.OrderBy(x => x.Key))
            {
                sb.AppendLine($"dotnet_diagnostic.{diagnosticId}.severity = {severity}");
            }

            var content = sb.ToString();
            await File.WriteAllTextAsync(globalConfigPath, content);
            placedFiles.Add(globalConfigPath);
        }
        catch (Exception ex)
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
    private void ParseNuGetAnalyzersYaml(string yamlContent, List<(string Id, string Version)> nugetAnalyzers)
    {
        try
        {
            var deserializer = new DeserializerBuilder().Build();
            var yaml = deserializer.Deserialize<Dictionary<object, object>>(yamlContent);

            if (yaml != null && yaml.TryGetValue("analyzers", out var analyzersObj))
            {
                if (analyzersObj is List<object> analyzersList)
                {
                    foreach (var analyzer in analyzersList)
                    {
                        if (analyzer is Dictionary<object, object> analyzerDict)
                        {
                            if (analyzerDict.TryGetValue("id", out var idObj) &&
                                analyzerDict.TryGetValue("version", out var versionObj))
                            {
                                var id = idObj?.ToString();
                                var version = versionObj?.ToString();
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
        catch
        {
            // Silently fail on parse errors, continue with other processing
        }
    }

    /// <summary>
    /// Parses rules.yaml to extract diagnostic IDs and their severity levels.
    /// Looks for patterns like "dotnet_diagnostic.{id}.severity = {severity}".
    /// </summary>
    private void ParseRulesYaml(string yamlContent, Dictionary<string, string> diagnosticRules)
    {
        try
        {
            // Simple regex-based parsing for diagnostic severity rules
            // Pattern: dotnet_diagnostic.{id}.severity = {severity}
            var pattern = @"dotnet_diagnostic\.([A-Z0-9]+)\s*=\s*(.+)";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);

            foreach (Match match in regex.Matches(yamlContent))
            {
                var id = match.Groups[1].Value.Trim();
                var severity = match.Groups[2].Value.Trim();

                if (!diagnosticRules.ContainsKey(id))
                {
                    diagnosticRules[id] = severity;
                }
            }
        }
        catch
        {
            // Silently fail on parse errors, continue with other processing
        }
    }
}

/// <summary>
/// Result of a restore operation.
/// </summary>
public class RestoreResult
{
    /// <summary>
    /// Number of packages processed.
    /// </summary>
    public int PackageCount { get; init; }

    /// <summary>
    /// Number of files placed.
    /// </summary>
    public int FileCount { get; init; }

    /// <summary>
    /// List of files placed during restore.
    /// </summary>
    public List<string> PlacedFiles { get; init; } = [];

    /// <summary>
    /// Warnings encountered during restore.
    /// </summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>
    /// Whether the restore succeeded.
    /// </summary>
    public bool Success { get; init; }

    public override string ToString()
    {
        return $"Restored {PackageCount} packages, placed {FileCount} files.";
    }
}
