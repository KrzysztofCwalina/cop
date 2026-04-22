using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using Cop.Core;

namespace Cop.Cli.Commands;

/// <summary>
/// Publishes a package by validating it and creating a git tag.
/// </summary>
public static class PublishCommand
{
    /// <summary>
    /// Creates the publish command.
    /// </summary>
    /// <returns>A System.CommandLine.Command configured for the publish subcommand.</returns>
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name");
        var command = new Command("publish", "Validate and publish a package version")
        {
            nameArgument
        };

        command.SetAction(parseResult => Execute(parseResult.GetValue(nameArgument)));

        return command;
    }

    /// <summary>
    /// Executes the publish command: validates package and creates git tag.
    /// </summary>
    private static int Execute(string name)
    {
        var packagePath = LocalPackageSource.FindPackagePath("packages", name)
            ?? Path.Combine("packages", name);

        // Step 1: Directory exists
        if (!Directory.Exists(packagePath))
        {
            Console.Error.WriteLine($"Error: Package '{name}' not found under 'packages/'.");
            return 1;
        }

        var metadataFile = Path.Combine(packagePath, $"{name}.md");

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
            metadata = PackageMetadata.ParseFromMarkdown(content);
        }
        catch (Exception ex)
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
        if (!Regex.IsMatch(metadata.Version, @"^\d+\.\d+\.\d+$"))
        {
            Console.Error.WriteLine($"Error: Version '{metadata.Version}' is not valid semver format (expected X.Y.Z).");
            return 1;
        }

        // Step 7: Required directories exist
        var requiredDirs = new[] { "instructions", "skills", "checks", "tests" };
        foreach (var dir in requiredDirs)
        {
            var dirPath = Path.Combine(packagePath, dir);
            if (!Directory.Exists(dirPath))
            {
                Console.Error.WriteLine($"Error: Required directory '{dir}/' does not exist.");
                return 1;
            }
        }

        // Step 8: No circular self-dependency
        if (metadata.Dependencies.Count > 0)
        {
            foreach (var dep in metadata.Dependencies)
            {
                try
                {
                    var packageRef = PackageReference.Parse(dep);
                    if (packageRef.PackageName == name)
                    {
                        Console.Error.WriteLine($"Error: Package has circular dependency on itself: {dep}");
                        return 1;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: Invalid dependency '{dep}': {ex.Message}");
                    return 1;
                }
            }
        }

        var tagName = $"{name}/{metadata.Version}";

        // Check if tag already exists
        if (TagExists(tagName))
        {
            Console.Error.WriteLine($"Error: Version {metadata.Version} already published (tag '{tagName}' exists).");
            return 1;
        }

        // Create git tag
        try
        {
            var result = RunGitCommand($"tag {tagName}");
            if (result != 0)
            {
                Console.Error.WriteLine($"Error: Failed to create git tag '{tagName}'.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to create git tag: {ex.Message}");
            return 1;
        }

        // Success messages
        Console.WriteLine($"Published {name} v{metadata.Version} (tag: {tagName})");
        Console.WriteLine($"Run 'git push origin {tagName}' to push the tag");
        return 0;
    }

    /// <summary>
    /// Tests if a git tag exists.
    /// </summary>
    private static bool TagExists(string tagName)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"tag -l \"{tagName}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a git command and returns the exit code.
    /// </summary>
    private static int RunGitCommand(string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        process.WaitForExit();

        return process.ExitCode;
    }
}
