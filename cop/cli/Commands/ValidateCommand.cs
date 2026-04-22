using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using Cop.Core;

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
        var nameArgument = new Argument<string>("name");
        var command = new Command("validate", "Validate a package structure")
        {
            nameArgument
        };

        command.SetAction(parseResult => Execute(parseResult.GetValue(nameArgument)));

        return command;
    }

    /// <summary>
    /// Executes validations on a package.
    /// </summary>
    public static int Execute(string name)
    {
        var packagePath = LocalPackageSource.FindPackagePath("packages", name)
            ?? Path.Combine("packages", name);
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

        var metadataFile = Path.Combine(packagePath, $"{name}.md");

        // Step 2: Metadata file exists
        results.Add(new ValidationResult(
            "Metadata file exists",
            File.Exists(metadataFile)
        ));

        PackageMetadata? metadata = null;

        // Step 3: Metadata YAML front-matter parses successfully
        if (File.Exists(metadataFile))
        {
            try
            {
                string content = File.ReadAllText(metadataFile);
                metadata = PackageMetadata.ParseFromMarkdown(content);
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

        // Step 9: checks/ directory exists
        var checksPath = Path.Combine(packagePath, "checks");
        results.Add(new ValidationResult(
            "checks/ directory exists",
            Directory.Exists(checksPath)
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
}
