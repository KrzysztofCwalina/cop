using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Net.Http;
using Cop.Core;
using Cop.Lang;

namespace Cop.Cli.Commands;

/// <summary>
/// Implements the `cop restore` command.
/// Restores packages from a .cop file by parsing feed and import declarations,
/// resolving dependencies, downloading packages, and placing files in the repository.
/// </summary>
public static class RestoreCommand
{
    /// <summary>
    /// Creates the restore command.
    /// </summary>
    /// <returns>A System.CommandLine.Command configured for the restore subcommand.</returns>
    public static Command Create()
    {
        var fileArg = new Argument<string>("file")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "A .cop file (or omit to restore all .cop files in the current directory)"
        };
        var command = new Command("restore", "Restore packages from a .cop file")
        {
            fileArg
        };

        command.SetAction(parseResult => ExecuteAsync(parseResult.GetValue(fileArg)).GetAwaiter().GetResult());

        return command;
    }

    /// <summary>
    /// Executes the restore operation.
    /// </summary>
    public static async Task<int> ExecuteAsync(string? fileArg)
    {
        string[] filePaths;

        if (fileArg != null)
        {
            var fullPath = Path.GetFullPath(fileArg);
            if (!File.Exists(fullPath))
            {
                Console.Error.WriteLine($"Error: File '{fullPath}' does not exist");
                return 1;
            }
            filePaths = [fullPath];
        }
        else
        {
            var dir = Directory.GetCurrentDirectory();
            filePaths = Directory.GetFiles(dir, "*.cop");
            if (filePaths.Length == 0)
            {
                Console.Error.WriteLine("Error: No .cop files found in current directory");
                return 1;
            }
        }

        int exitCode = 0;
        foreach (var filePath in filePaths)
        {
            var result = await RestoreFileAsync(filePath);
            if (result != 0) exitCode = result;
        }
        return exitCode;
    }

    private static async Task<int> RestoreFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"Error: File '{filePath}' does not exist");
            return 1;
        }

        try
        {
            // Parse the .cop file to extract feed and import declarations
            var source = File.ReadAllText(filePath);
            var scriptFile = ScriptParser.Parse(source, filePath);

            if (scriptFile.Imports.Count == 0)
            {
                Console.Error.WriteLine("Error: No import declarations found in the .cop file");
                return 1;
            }

            // Build PackageReference list from feed URLs + import names
            // Only GitHub feeds (github.com/owner/repo) support remote restore
            var githubFeeds = (scriptFile.FeedPaths ?? [])
                .Where(f => f.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (githubFeeds.Count == 0)
            {
                Console.Error.WriteLine("Error: No GitHub feed found. Restore requires a feed like 'github.com/owner/repo'");
                return 1;
            }

            // For each import, use the first GitHub feed to form the full package reference
            var packages = scriptFile.Imports
                .Select(importName => PackageReference.Parse($"{githubFeeds[0]}/{importName}"))
                .ToList();

            // Determine repo root (directory containing the .cop file)
            string repoRoot = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();

            // Get GitHub token from environment variable if available
            string? githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

            // Create HttpClient and GitHubPackageSource
            using var httpClient = new HttpClient();
            var packageSource = new GitHubPackageSource(httpClient, githubToken);

            // Create RestoreEngine
            var engine = new RestoreEngine(packageSource);

            // Perform the restore operation
            Console.WriteLine($"Restoring packages from {Path.GetFileName(filePath)}...");
            var result = await engine.RestoreAsync(packages, filePath, repoRoot, CancellationToken.None);

            // Check if restore succeeded
            if (!result.Success)
            {
                foreach (var warning in result.Warnings)
                    Console.Error.WriteLine($"Warning: {warning}");
                Console.Error.WriteLine("Error: Restore operation failed");
                return 1;
            }

            Console.WriteLine(result.ToString());

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Warnings:");
                foreach (var warning in result.Warnings)
                    Console.WriteLine($"  - {warning}");
            }

            if (result.PlacedFiles.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Placed files:");
                foreach (var file in result.PlacedFiles)
                {
                    var relativePath = Path.GetRelativePath(repoRoot, file);
                    Console.WriteLine($"  - {relativePath}");
                }
            }

            return 0;
        }
        catch (ParseException ex)
        {
            Console.Error.WriteLine($"Error parsing .cop file: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
