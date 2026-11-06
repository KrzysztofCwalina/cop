// Creates the source layout and metadata for a new Cop package.

using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using Cop.Core;

namespace Cop.Cli.Commands;

/// <summary>
/// Scaffolds package directories with their initial manifest and source.
/// </summary>
public static class NewCommand
{
    /// <summary>
    /// Creates the package-scaffolding command with its required package name.
    /// </summary>
    public static Command Create()
    {
        Argument<string> nameArg = new Argument<string>("name");
        Command command = new Command("new", "Scaffold a new package directory")
        {
            nameArg
        };

        command.SetAction(parseResult => Execute(parseResult.GetValue(nameArg)
            ?? throw new InvalidOperationException("The required package name is missing.")));

        return command;
    }

    // Valid package name: starts with a lowercase letter, then lowercase letters, digits, or hyphens.
    private static bool IsValidPackageName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9') && c != '-')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Writes the initial directory layout and files for a named package.
    /// </summary>
    public static void Execute(string name)
    {
        // Validate package name format
        if (!IsValidPackageName(name))
        {
            Console.Error.WriteLine($"Error: Package name '{name}' is invalid. Must start with lowercase letter and contain only lowercase letters, numbers, and hyphens.");
            Environment.Exit(1);
        }

        // Get the current working directory and construct the package path
        string currentDir = Directory.GetCurrentDirectory();
        string packagesDir = Path.Combine(currentDir, "packages");
        string packageDir = Path.Combine(packagesDir, name);

        // Check if package directory already exists
        if (Directory.Exists(packageDir))
        {
            Console.Error.WriteLine($"Error: Package directory '{packageDir}' already exists.");
            Environment.Exit(1);
        }

        try
        {
            // Create packages directory if it doesn't exist
            Directory.CreateDirectory(packagesDir);

            // Create package directory
            Directory.CreateDirectory(packageDir);

            // Create subdirectories
            Directory.CreateDirectory(Path.Combine(packageDir, "instructions"));
            Directory.CreateDirectory(Path.Combine(packageDir, "skills"));
            Directory.CreateDirectory(Path.Combine(packageDir, "src"));
            Directory.CreateDirectory(Path.Combine(packageDir, "tests"));

            // Create cop.json metadata file
            PackageMetadata metadata = new Cop.Core.PackageMetadata
            {
                Name = name,
                Version = "1.0.0",
                Title = $"{name} Package",
                Description = "TODO - Add description",
                Authors = "TODO - Add author"
            };
            string metadataPath = Path.Combine(packageDir, Cop.Core.PackageMetadata.MetadataFileName);
            File.WriteAllText(metadataPath, metadata.ToJson());

            // Create README.md
            string readmeContent = $"""
# {name}

TODO - Add package description.
""";
            string readmePath = Path.Combine(packageDir, "README.md");
            File.WriteAllText(readmePath, readmeContent);

            Console.WriteLine($"Successfully created package '{name}' at {packageDir}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Error creating package: {ex.Message}");
            Environment.Exit(1);
        }
    }
}
