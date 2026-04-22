using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Text;
using Cop.Core;

namespace Cop.Cli.Commands;

public static class LockCommand
{
    public static Command Create()
    {
        var filesArg = new Argument<string[]>("files")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Files to lock (repo-relative or absolute paths)"
        };
        var listOption = new Option<bool>("--list") { Description = "Show locked files (no key needed)" };
        var command = new Command("lock", "Lock files for tamper protection")
        {
            filesArg,
            listOption
        };

        command.SetAction(parseResult => Execute(
            parseResult.GetValue(filesArg),
            parseResult.GetValue(listOption)));

        return command;
    }

    public static void Execute(string[]? files, bool list)
    {
        var rootDirectory = Directory.GetCurrentDirectory();

        if (list)
        {
            ExecuteList(rootDirectory);
            return;
        }

        if (files == null || files.Length == 0)
        {
            ExecuteResign(rootDirectory);
            return;
        }

        ExecuteLock(rootDirectory, files);
    }

    private static void ExecuteList(string rootDirectory)
    {
        var lockFile = LockFile.Load(rootDirectory);
        if (lockFile == null || lockFile.Files.Count == 0)
        {
            Console.WriteLine("No locked files.");
            return;
        }

        Console.WriteLine($"Locked files ({lockFile.Files.Count}):");
        foreach (var (path, _) in lockFile.Files)
            Console.WriteLine($"  {path}");
    }

    private static void ExecuteResign(string rootDirectory)
    {
        var lockFile = LockFile.Load(rootDirectory);
        if (lockFile == null || lockFile.Files.Count == 0)
        {
            Console.Error.WriteLine("No .cop-lock found or no files locked. Specify files to lock.");
            Environment.Exit(1);
        }

        // Show what will be re-signed
        var verifications = lockFile.VerifyChecksums(rootDirectory);
        Console.WriteLine($"Re-signing {lockFile.Files.Count} locked file(s):");
        foreach (var v in verifications)
            Console.WriteLine($"  {v.RelativePath} [{v.Status}]");

        var key = ReadKey("Signing key: ");
        lockFile.Resign(key, rootDirectory);
        lockFile.Save(rootDirectory);
        Console.WriteLine("Re-signed .cop-lock");
    }

    private static void ExecuteLock(string rootDirectory, string[] files)
    {
        // Validate all files first
        var relativePaths = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var relativePath = ResolveRelativePath(rootDirectory, file);
                relativePaths.Add(relativePath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }

        var key = ReadKey("Signing key: ");

        var lockFile = LockFile.Load(rootDirectory) ?? new LockFile();

        foreach (var relativePath in relativePaths)
        {
            try
            {
                lockFile.Lock(relativePath, rootDirectory, key);
                Console.WriteLine($"Locked: {relativePath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error locking '{relativePath}': {ex.Message}");
                Environment.Exit(1);
            }
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

    internal static string ReadKey(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            // Piped input (CI): read from stdin
            var line = Console.ReadLine();
            if (string.IsNullOrEmpty(line))
            {
                Console.Error.WriteLine("Error: Signing key cannot be empty");
                Environment.Exit(1);
            }
            return line!;
        }

        // Interactive: prompt with hidden input
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var keyInfo = Console.ReadKey(intercept: true);
            if (keyInfo.Key == ConsoleKey.Enter)
                break;
            if (keyInfo.Key == ConsoleKey.Backspace && sb.Length > 0)
            {
                sb.Remove(sb.Length - 1, 1);
                Console.Write("\b \b");
            }
            else if (keyInfo.Key != ConsoleKey.Backspace)
            {
                sb.Append(keyInfo.KeyChar);
                Console.Write("*");
            }
        }
        Console.WriteLine();

        var key = sb.ToString();
        if (string.IsNullOrEmpty(key))
        {
            Console.Error.WriteLine("Error: Signing key cannot be empty");
            Environment.Exit(1);
        }
        return key;
    }
}
