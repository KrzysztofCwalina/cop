// Validates and publishes package versions from the CLI.

using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Cop.Core;

namespace Cop.Cli.Commands;

/// <summary>
/// Publishes a package by validating it and creating a git tag.
/// </summary>
public static class PublishCommand
{
    // True if version is exactly three dot-separated numeric parts (X.Y.Z).
    private static bool IsSemVer3(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        string[] parts = version.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        foreach (string p in parts)
        {
            if (p.Length == 0)
            {
                return false;
            }

            foreach (char c in p)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Creates the publish command.
    /// </summary>
    /// <returns>A System.CommandLine.Command configured for the publish subcommand.</returns>
    /// <summary>
    /// Creates the package-publication command and its version options.
    /// </summary>
    public static Command Create()
    {
        Argument<string> nameArgument = new Argument<string>("name");
        Command command = new Command("publish", "Validate and publish a package version")
        {
            nameArgument
        };

        command.SetAction(parseResult => ExecuteAsync(parseResult.GetValue(nameArgument)
            ?? throw new InvalidOperationException("The required package name is missing.")));

        return command;
    }

    /// <summary>
    /// Executes the publish command: validates package and creates git tag.
    /// </summary>
    private static async Task<int> ExecuteAsync(string name)
    {
        string packagePath = LocalPackageSource.FindPackagePath("packages", name)
            ?? Path.Combine("packages", name);

        // Step 1: Directory exists
        if (!Directory.Exists(packagePath))
        {
            Console.Error.WriteLine($"Error: Package '{name}' not found under 'packages/'.");
            return 1;
        }

        string metadataFile = Path.Combine(packagePath, PackageMetadata.MetadataFileName);

        // Step 2: Metadata file exists
        if (!File.Exists(metadataFile))
        {
            Console.Error.WriteLine($"Error: Metadata file '{metadataFile}' not found.");
            return 1;
        }

        // Step 3: Parse metadata
        PackageMetadata? metadata = null;
        try
        {
            string content = File.ReadAllText(metadataFile);
            metadata = PackageMetadata.ParseFromJson(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Error: Failed to parse metadata: {ex.Message}");
            return 1;
        }

        // Step 4: Required fields populated
        if (metadata == null
            || string.IsNullOrWhiteSpace(metadata.Name)
            || metadata.Name == "TODO"
            || string.IsNullOrWhiteSpace(metadata.Version)
            || metadata.Version == "TODO"
            || string.IsNullOrWhiteSpace(metadata.Title)
            || metadata.Title == "TODO"
            || string.IsNullOrWhiteSpace(metadata.Description)
            || metadata.Description == "TODO"
            || string.IsNullOrWhiteSpace(metadata.Authors)
            || metadata.Authors == "TODO")
        {
            Console.Error.WriteLine("Error: Required fields are not populated (Name, Version, Title, Description, Authors must not be empty or TODO).");
            return 1;
        }

        // Step 5: Name matches directory name
        if (metadata.Name != name)
        {
            Console.Error.WriteLine($"Error: Package name in metadata ('{metadata.Name}') does not match directory name ('{name}').");
            return 1;
        }

        // Step 6: Version is valid semver format (X.Y.Z)
        if (!IsSemVer3(metadata.Version))
        {
            Console.Error.WriteLine($"Error: Version '{metadata.Version}' is not valid semver format (expected X.Y.Z).");
            return 1;
        }

        // Step 7: Required directories exist
        string[] requiredDirs = new[] { "instructions", "skills", "src", "tests" };
        foreach (string? dir in requiredDirs)
        {
            string dirPath = Path.Combine(packagePath, dir);
            if (!Directory.Exists(dirPath))
            {
                Console.Error.WriteLine($"Error: Required directory '{dir}/' does not exist.");
                return 1;
            }
        }

        // Step 8: No circular self-dependency
        if (metadata.Dependencies.Count > 0)
        {
            foreach (string dep in metadata.Dependencies)
            {
                try
                {
                    PackageReference packageRef = PackageReference.Parse(dep);
                    if (packageRef.PackageName == name)
                    {
                        Console.Error.WriteLine($"Error: Package has circular dependency on itself: {dep}");
                        return 1;
                    }
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    Console.Error.WriteLine($"Error: Invalid dependency '{dep}': {ex.Message}");
                    return 1;
                }
            }
        }

        string tagName = $"{name}/{metadata.Version}";

        string? git = ToolchainProcess.FindExecutable("git");
        if (git is null)
        {
            Console.Error.WriteLine("Error: Cannot publish because git is not installed.");
            return 1;
        }

        string workingDirectory = Directory.GetCurrentDirectory();
        ToolchainResult inspection = await ToolchainProcess.RunAsync(
            new ToolchainCommand(git, ["tag", "-l", tagName], workingDirectory));
        if (inspection.Status != ToolchainStatus.Success)
        {
            Console.Error.WriteLine($"Error: Cannot inspect existing git tags: {inspection.Diagnostic} {inspection.StandardError}".TrimEnd());
            return 1;
        }
        if (!string.IsNullOrWhiteSpace(inspection.StandardOutput))
        {
            Console.Error.WriteLine($"Error: Version {metadata.Version} already published (tag '{tagName}' exists).");
            return 1;
        }

        ToolchainResult creation = await ToolchainProcess.RunAsync(
            new ToolchainCommand(git, ["tag", tagName], workingDirectory));
        if (creation.Status != ToolchainStatus.Success)
        {
            Console.Error.WriteLine($"Error: Failed to create git tag '{tagName}': {creation.Diagnostic} {creation.StandardError}".TrimEnd());
            return 1;
        }

        // Success messages
        Console.WriteLine($"Published {name} v{metadata.Version} (tag: {tagName})");
        Console.WriteLine($"Run 'git push origin {tagName}' to push the tag");
        return 0;
    }

}
