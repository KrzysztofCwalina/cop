// Removes signed tamper-protection locks from selected project files.

using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using Cop.Core;

namespace Cop.Cli.Commands;

/// <summary>Removes tamper-protection locks from project files.</summary>
public static class UnlockCommand
{
    /// <summary>Creates the unlock command with optional file selection.</summary>
    public static Command Create()
    {
        Argument<string[]> filesArg = new Argument<string[]>("files")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Files to unlock (or omit to unlock all locked files)"
        };
        Command command = new Command("unlock", "Unlock previously locked files")
        {
            filesArg
        };

        command.SetAction(parseResult => Execute(parseResult.GetValue(filesArg)));

        return command;
    }

    /// <summary>
    /// Unlocks the selected files, or all locked files when none are specified.
    /// </summary>
    public static void Execute(string[]? files)
    {
        string rootDirectory = Directory.GetCurrentDirectory();
        LockFile? lockFile = LockFile.Load(rootDirectory);

        if (lockFile == null || lockFile.Files.Count == 0)
        {
            Console.Error.WriteLine("Error: No .cop-lock found or no files locked");
            Environment.Exit(1);
        }

        // Verify key
        string key = LockCommand.ReadKey("Signing key: ");

        // Verify at least one existing signature to confirm key is correct
        List<LockVerification> sigResults = lockFile.VerifySignatures(key);
        if (sigResults.Count > 0)
        {
            Console.Error.WriteLine("Error: Signing key does not match");
            Environment.Exit(1);
        }

        // If no files specified, unlock all locked files
        string[] filesToUnlock = (files == null || files.Length == 0)
            ? lockFile.Files.Select(f => f.Key).ToArray()
            : files.Select(f => ResolveRelativePath(rootDirectory, f)).ToArray();

        foreach (string? relativePath in filesToUnlock)
        {
            if (lockFile.Unlock(relativePath))
            {
                Console.WriteLine($"Unlocked: {relativePath}");
            }
            else
            {
                Console.Error.WriteLine($"Warning: '{relativePath}' was not locked");
            }
        }

        lockFile.Save(rootDirectory);
    }

    private static string ResolveRelativePath(string rootDirectory, string file)
    {
        string fullPath = Path.IsPathRooted(file)
            ? Path.GetFullPath(file)
            : Path.GetFullPath(Path.Combine(rootDirectory, file));
        return LockFile.NormalizePath(rootDirectory, fullPath);
    }
}
