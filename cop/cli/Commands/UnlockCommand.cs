using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using Cop.Core;

namespace Cop.Cli.Commands;

public static class UnlockCommand
{
    public static Command Create()
    {
        var filesArg = new Argument<string[]>("files")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "Files to unlock (repo-relative or absolute paths)"
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
        if (files == null || files.Length == 0)
        {
            Console.Error.WriteLine("Error: Specify files to unlock");
            Environment.Exit(1);
        }

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

        foreach (var file in files)
        {
            var relativePath = ResolveRelativePath(rootDirectory, file);
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
