using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using Cop.Core;

namespace Cop.Cli.Commands;

public static class UnlockCommand
{
    public static Command Create()
    {
        var filesArg = new Argument<string[]>("files")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Files to unlock (or omit to unlock all locked files)"
        };
        var command = new Command("unlock", "Unlock previously locked files")
        {
            filesArg
        };

        command.SetAction(parseResult => Execute(parseResult.GetValue(filesArg)));

        return command;
    }

    public static void Execute(string[]? files)
    {
        var rootDirectory = Directory.GetCurrentDirectory();
        var lockFile = LockFile.Load(rootDirectory);

        if (lockFile == null || lockFile.Files.Count == 0)
        {
            Console.Error.WriteLine("Error: No .cop-lock found or no files locked");
            Environment.Exit(1);
        }

        // Verify key
        var key = LockCommand.ReadKey("Signing key: ");

        // Verify at least one existing signature to confirm key is correct
        var sigResults = lockFile.VerifySignatures(key);
        if (sigResults.Count > 0)
        {
            Console.Error.WriteLine("Error: Signing key does not match");
            Environment.Exit(1);
        }

        // If no files specified, unlock all locked files
        var filesToUnlock = (files == null || files.Length == 0)
            ? lockFile.Files.Select(f => f.Key).ToArray()
            : files.Select(f => ResolveRelativePath(rootDirectory, f)).ToArray();

        foreach (var relativePath in filesToUnlock)
        {
            if (lockFile.Unlock(relativePath))
                Console.WriteLine($"Unlocked: {relativePath}");
            else
                Console.Error.WriteLine($"Warning: '{relativePath}' was not locked");
        }

        lockFile.Save(rootDirectory);
    }

    private static string ResolveRelativePath(string rootDirectory, string file)
    {
        var fullPath = Path.IsPathRooted(file)
            ? Path.GetFullPath(file)
            : Path.GetFullPath(Path.Combine(rootDirectory, file));
        return LockFile.NormalizePath(rootDirectory, fullPath);
    }
}
