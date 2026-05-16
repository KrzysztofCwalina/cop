using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Text.RegularExpressions;

namespace Cop.Cli.Commands;

public static class NewCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name");
        var command = new Command("new", "Scaffold a new package directory")
        {
            nameArg
        };

        command.SetAction(parseResult => Execute(parseResult.GetValue(nameArg)));

        return command;
    }

    public static void Execute(string name)
    {
        // Validate package name format
        if (!Regex.IsMatch(name, @"^[a-z][a-z0-9-]*$"))
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
            Directory.CreateDirectory(Path.Combine(packageDir, "checks"));
            Directory.CreateDirectory(Path.Combine(packageDir, "tests"));

            // Create cop.json metadata file
            var metadata = new Cop.Core.PackageMetadata
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error creating package: {ex.Message}");
            Environment.Exit(1);
        }
    }
}



