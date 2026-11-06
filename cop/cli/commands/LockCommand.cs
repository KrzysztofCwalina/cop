// Protects selected project files with signed content hashes.

using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Text;
using Cop.Core;

namespace Cop.Cli.Commands;

/// <summary>Locks project files against undetected content changes.</summary>
public static class LockCommand
{
    /// <summary>Creates the file-lock command and its listing option.</summary>
    public static Command Create()
    {
        Argument<string[]> filesArg = new Argument<string[]>("files")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Files to lock (repo-relative or absolute paths)"
        };
        Option<bool> listOption = new Option<bool>("--list") { Description = "Show locked files (no key needed)" };
        Command command = new Command("lock", "Lock files for tamper protection")
        {
            filesArg,
            listOption
        };

        command.SetAction(parseResult =>
        {
            if (parseResult.GetValue(listOption))
            {
                List();
            }
            else
            {
                Execute(parseResult.GetValue(filesArg));
            }
        });

        return command;
    }

    /// <summary>
    /// Signs selected files, or re-signs existing locks when no files are supplied.
    /// </summary>
    public static void Execute(string[]? files)
    {
        string rootDirectory = Directory.GetCurrentDirectory();

        if (files == null || files.Length == 0)
        {
            ExecuteResign(rootDirectory);
            return;
        }

        ExecuteLock(rootDirectory, files);
    }

    /// <summary>Lists content locks in the current repository without requesting a key.</summary>
    public static void List()
    {
        string rootDirectory = Directory.GetCurrentDirectory();
        LockFile? lockFile = LockFile.Load(rootDirectory);
        if (lockFile == null || lockFile.Files.Count == 0)
        {
            Console.WriteLine("No locked files.");
            return;
        }

        Console.WriteLine($"Locked files ({lockFile.Files.Count}):");
        foreach ((string? path, LockEntry _) in lockFile.Files)
        {
            Console.WriteLine($"  {path}");
        }
    }

    private static void ExecuteResign(string rootDirectory)
    {
        LockFile? lockFile = LockFile.Load(rootDirectory);
        if (lockFile == null || lockFile.Files.Count == 0)
        {
            Console.Error.WriteLine("No .cop-lock found or no files locked. Specify files to lock.");
            Environment.Exit(1);
        }

        // Show what will be re-signed
        List<LockVerification> verifications = lockFile.VerifyChecksums(rootDirectory);
        Console.WriteLine($"Re-signing {lockFile.Files.Count} locked file(s):");
        foreach (LockVerification v in verifications)
        {
            Console.WriteLine($"  {v.RelativePath} [{v.Status}]");
        }

        string key = ReadKey("Signing key: ");
        lockFile.Resign(key, rootDirectory);
        lockFile.Save(rootDirectory);
        Console.WriteLine("Re-signed .cop-lock");
    }

    private static void ExecuteLock(string rootDirectory, string[] files)
    {
        // Validate all files first
        List<string> relativePaths = new List<string>();
        foreach (string file in files)
        {
            try
            {
                string relativePath = ResolveRelativePath(rootDirectory, file);
                relativePaths.Add(relativePath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or
                UnauthorizedAccessException or NotSupportedException)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }

        string key = ReadKey("Signing key: ");

        LockFile lockFile = LockFile.Load(rootDirectory) ?? new LockFile();

        foreach (string relativePath in relativePaths)
        {
            try
            {
                lockFile.Lock(relativePath, rootDirectory, key);
                Console.WriteLine($"Locked: {relativePath}");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or
                UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                Console.Error.WriteLine($"Error locking '{relativePath}': {ex.Message}");
                Environment.Exit(1);
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

    internal static string ReadKey(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            // Piped input (CI): read from stdin
            string? line = Console.ReadLine();
            if (string.IsNullOrEmpty(line))
            {
                Console.Error.WriteLine("Error: Signing key cannot be empty");
                Environment.Exit(1);
            }
            return line!;
        }

        // Interactive: prompt with hidden input
        Console.Write(prompt);
        StringBuilder sb = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo keyInfo = Console.ReadKey(intercept: true);
            if (keyInfo.Key == ConsoleKey.Enter)
            {
                break;
            }

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

        string key = sb.ToString();
        if (string.IsNullOrEmpty(key))
        {
            Console.Error.WriteLine("Error: Signing key cannot be empty");
            Environment.Exit(1);
        }
        return key;
    }
}
