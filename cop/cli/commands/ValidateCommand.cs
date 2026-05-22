using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using Cop.Core;
using Cop.Providers;
using Cop.Lang;
using Cop.Lang.Parser;

namespace Cop.Cli.Commands;

/// <summary>
/// Validates a package directory structure and metadata.
/// </summary>
public static class ValidateCommand
{
    /// <summary>
    /// Creates the validate command.
    /// </summary>
    /// <returns>A System.CommandLine.Command configured for the validate subcommand.</returns>
    public static Command Create()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Package directory path or package name"
        };
        var command = new Command("validate", "Validate a package structure, source, and samples")
        {
            pathArgument
        };

        command.SetAction(parseResult => Execute(parseResult.GetValue(pathArgument)));

        return command;
    }

    /// <summary>
    /// Executes validations on a package.
    /// </summary>
    public static int Execute(string path)
    {
        // Resolve path: if it's an existing directory use it directly,
        // otherwise try name-based lookup for backward compatibility
        string packagePath;
        if (Directory.Exists(path))
        {
            packagePath = Path.GetFullPath(path);
        }
        else
        {
            packagePath = LocalPackageSource.FindPackagePath("packages", path)
                ?? Path.Combine("packages", path);
        }

        var name = Path.GetFileName(packagePath);
        var results = new List<ValidationResult>();

        // Step 1: Directory exists
        results.Add(new ValidationResult(
            "Directory exists",
            Directory.Exists(packagePath)
        ));

        if (!Directory.Exists(packagePath))
        {
            PrintResults(results);
            return 1;
        }

        var metadataFile = Path.Combine(packagePath, PackageMetadata.MetadataFileName);

        // Step 2: Metadata file exists
        results.Add(new ValidationResult(
            "Metadata file exists",
            File.Exists(metadataFile)
        ));

        PackageMetadata? metadata = null;

        // Step 3: Metadata JSON parses successfully
        if (File.Exists(metadataFile))
        {
            try
            {
                string content = File.ReadAllText(metadataFile);
                metadata = PackageMetadata.ParseFromJson(content);
                results.Add(new ValidationResult("Metadata parses successfully", true));
            }
            catch (Exception ex)
            {
                results.Add(new ValidationResult("Metadata parses successfully", false, ex.Message));
                metadata = null;
            }
        }
        else
        {
            results.Add(new ValidationResult("Metadata parses successfully", false, "Metadata file not found"));
        }

        // Step 4: Required fields populated
        bool requiredFieldsValid = false;
        if (metadata != null)
        {
            requiredFieldsValid = !string.IsNullOrWhiteSpace(metadata.Name)
                && metadata.Name != "TODO"
                && !string.IsNullOrWhiteSpace(metadata.Version)
                && metadata.Version != "TODO"
                && !string.IsNullOrWhiteSpace(metadata.Title)
                && metadata.Title != "TODO"
                && !string.IsNullOrWhiteSpace(metadata.Description)
                && metadata.Description != "TODO"
                && !string.IsNullOrWhiteSpace(metadata.Authors)
                && metadata.Authors != "TODO";
        }
        results.Add(new ValidationResult(
            "Required fields populated",
            requiredFieldsValid
        ));

        // Step 5: Name matches directory name
        bool nameMatches = false;
        if (metadata != null)
        {
            nameMatches = metadata.Name == name;
        }
        results.Add(new ValidationResult(
            "Name matches directory name",
            nameMatches,
            metadata != null && !nameMatches ? $"Expected '{name}', got '{metadata.Name}'" : null
        ));

        // Step 6: Version is valid semver format (X.Y.Z)
        bool versionValid = false;
        if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Version))
        {
            // Match semver format X.Y.Z
            versionValid = Regex.IsMatch(metadata.Version, @"^\d+\.\d+\.\d+$");
        }
        results.Add(new ValidationResult(
            "Version is valid semver",
            versionValid,
            metadata != null && !versionValid ? "Version must match X.Y.Z format" : null
        ));

        // Step 7: instructions/ directory exists
        var instructionsPath = Path.Combine(packagePath, "instructions");
        results.Add(new ValidationResult(
            "instructions/ directory exists",
            Directory.Exists(instructionsPath)
        ));

        // Step 8: skills/ directory exists
        var skillsPath = Path.Combine(packagePath, "skills");
        results.Add(new ValidationResult(
            "skills/ directory exists",
            Directory.Exists(skillsPath)
        ));

        // Step 9: src/ directory exists
        var srcPath = Path.Combine(packagePath, "src");
        results.Add(new ValidationResult(
            "src/ directory exists",
            Directory.Exists(srcPath)
        ));

        // Step 10: tests/ directory exists
        var testsPath = Path.Combine(packagePath, "tests");
        results.Add(new ValidationResult(
            "tests/ directory exists",
            Directory.Exists(testsPath)
        ));

        // Step 11: Dependencies are valid package reference format
        bool dependenciesValid = true;
        string? dependencyError = null;
        if (metadata != null && metadata.Dependencies.Count > 0)
        {
            foreach (var dep in metadata.Dependencies)
            {
                try
                {
                    PackageReference.Parse(dep);
                }
                catch (Exception ex)
                {
                    dependenciesValid = false;
                    dependencyError = $"Invalid dependency '{dep}': {ex.Message}";
                    break;
                }
            }
        }
        results.Add(new ValidationResult(
            "Dependencies are valid",
            dependenciesValid,
            dependencyError
        ));

        // Step 12: No circular dependencies (self-reference validation)
        bool noCircularDeps = true;
        string? circularError = null;
        if (metadata != null && metadata.Dependencies.Count > 0)
        {
            foreach (var dep in metadata.Dependencies)
            {
                try
                {
                    var packageRef = PackageReference.Parse(dep);
                    if (packageRef.PackageName == name)
                    {
                        noCircularDeps = false;
                        circularError = $"Package has circular dependency on itself: {dep}";
                        break;
                    }
                }
                catch
                {
                    // Already caught in step 11
                }
            }
        }
        results.Add(new ValidationResult(
            "No circular dependencies",
            noCircularDeps,
            circularError
        ));

        // Step 13: Source files parse (src/*.cop)
        if (Directory.Exists(srcPath))
        {
            var srcFiles = Directory.GetFiles(srcPath, "*.cop");
            var srcErrors = new List<string>();
            foreach (var file in srcFiles)
            {
                try
                {
                    var source = File.ReadAllText(file);
                    CopParser.Parse(source, file);
                }
                catch (ParseException ex)
                {
                    srcErrors.Add(ex.Message);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    srcErrors.Add($"{file}: {ex.Message}");
                }
            }
            results.Add(new ValidationResult(
                $"Source files parse ({srcFiles.Length} file(s))",
                srcErrors.Count == 0,
                srcErrors.Count > 0 ? string.Join("; ", srcErrors) : null
            ));
        }

        // Step 14: Samples compile (samples/*.cop)
        var samplesPath = Path.Combine(packagePath, "samples");
        if (Directory.Exists(samplesPath))
        {
            var sampleFiles = Directory.GetFiles(samplesPath, "*.cop");
            var sampleErrors = new List<string>();

            // Find packages directory for import resolution
            var packagesDir = FindPackagesDir(packagePath);

            foreach (var file in sampleFiles)
            {
                var sampleSource = File.ReadAllText(file);

                var sampleResult = ValidateSample(file, sampleSource, packagesDir);
                if (sampleResult != null)
                    sampleErrors.Add(sampleResult);
            }

            results.Add(new ValidationResult(
                $"Samples compile ({sampleFiles.Length} file(s))",
                sampleErrors.Count == 0,
                sampleErrors.Count > 0 ? string.Join("; ", sampleErrors) : null
            ));
        }

        PrintResults(results);

        // Return 0 if all passed, 1 if any failed
        return results.All(r => r.Passed) ? 0 : 1;
    }

    /// <summary>
    /// Prints validation results in a formatted way.
    /// </summary>
    private static void PrintResults(List<ValidationResult> results)
    {
        foreach (var result in results)
        {
            string status = result.Passed ? "✓ PASS" : "✗ FAIL";
            Console.WriteLine($"{status}: {result.Name}");
            if (!string.IsNullOrEmpty(result.Details))
            {
                Console.WriteLine($"         {result.Details}");
            }
        }

        int passed = results.Count(r => r.Passed);
        int failed = results.Count(r => !r.Passed);

        Console.WriteLine();
        Console.WriteLine($"Summary: {passed} passed, {failed} failed");
    }

    /// <summary>
    /// Represents a single validation result.
    /// </summary>
    private class ValidationResult
    {
        public string Name { get; set; }
        public bool Passed { get; set; }
        public string? Details { get; set; }

        public ValidationResult(string name, bool passed, string? details = null)
        {
            Name = name;
            Passed = passed;
            Details = details;
        }
    }

    /// <summary>
    /// Validates a single sample file by running it through the engine.
    /// Returns an error message or null if valid.
    /// </summary>
    private static string? ValidateSample(string filePath, string source, string? packagesDir)
    {
        // First check if it parses
        try
        {
            CopParser.Parse(source, filePath);
        }
        catch (ParseException ex)
        {
            return $"{Path.GetFileName(filePath)}: {ex.Message}";
        }

        // Run through engine to validate imports and bindings
        var tempDir = Path.Combine(Path.GetTempPath(), "cop-validate", Path.GetFileNameWithoutExtension(filePath));
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);
        try
        {
            File.Copy(filePath, Path.Combine(tempDir, Path.GetFileName(filePath)));
            var feedPaths = packagesDir != null ? new[] { packagesDir } : null;
            var result = Engine.Run(tempDir, tempDir, additionalFeedPaths: feedPaths);

            if (result.HasParseErrors)
                return $"{Path.GetFileName(filePath)}: {string.Join("; ", result.ParseErrors)}";
            if (result.HasFatalErrors)
            {
                // "Command 'main' not found" is expected for snippet-style samples
                var realErrors = result.Errors
                    .Where(e => !e.Contains("Command 'main' not found"))
                    .ToList();
                if (realErrors.Count > 0)
                    return $"{Path.GetFileName(filePath)}: {string.Join("; ", realErrors)}";
            }

            return null;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Walks up from a package path to find the packages/ root directory.
    /// </summary>
    private static string? FindPackagesDir(string packagePath)
    {
        var dir = Path.GetDirectoryName(packagePath);
        while (dir != null)
        {
            // Check if this is a packages directory (contains multiple package dirs with src/)
            if (Path.GetFileName(dir).Equals("packages", StringComparison.OrdinalIgnoreCase))
                return dir;
            // Also check if parent is packages
            var parent = Path.GetDirectoryName(dir);
            if (parent != null && Path.GetFileName(parent).Equals("packages", StringComparison.OrdinalIgnoreCase))
                return parent;
            dir = parent;
        }
        return null;
    }
}
