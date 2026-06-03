using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Reflection;

namespace Cop.Cli.Commands;

public static class InitCommand
{
    public static Command Create()
    {
        var forceOption = new Option<bool>("--force", "Overwrite existing instruction files");
        var command = new Command("init", "Generate agent instruction files for writing cop rules")
        {
            forceOption
        };

        command.SetAction(ctx => Execute(ctx.GetValue(forceOption)));

        return command;
    }

    public static int Execute(bool force = false)
    {
        var cwd = Directory.GetCurrentDirectory();
        int filesCreated = 0;

        // Generate .github/copilot-instructions.md
        var githubDir = Path.Combine(cwd, ".github");
        var copilotPath = Path.Combine(githubDir, "copilot-instructions.md");
        if (File.Exists(copilotPath) && !force)
        {
            Console.Error.WriteLine($"Skipped: {GetRelativePath(cwd, copilotPath)} already exists (use --force to overwrite)");
        }
        else
        {
            Directory.CreateDirectory(githubDir);
            File.WriteAllText(copilotPath, GetInstructionContent());
            Console.WriteLine($"{(force && File.Exists(copilotPath) ? "Updated" : "Created")}: {GetRelativePath(cwd, copilotPath)}");
            filesCreated++;
        }

        // Generate AGENTS.md
        var agentsPath = Path.Combine(cwd, "AGENTS.md");
        if (File.Exists(agentsPath) && !force)
        {
            Console.Error.WriteLine($"Skipped: AGENTS.md already exists (use --force to overwrite)");
        }
        else
        {
            File.WriteAllText(agentsPath, GetInstructionContent());
            Console.WriteLine($"{(force && File.Exists(agentsPath) ? "Updated" : "Created")}: AGENTS.md");
            filesCreated++;
        }

        if (filesCreated > 0)
            Console.WriteLine($"\n{filesCreated} file(s) created. Agents will now discover cop language context automatically.");
        else
            Console.WriteLine("\nNo files created (all already exist). Use --force to overwrite.");

        return 0;
    }

    private static string GetRelativePath(string basePath, string fullPath)
    {
        return Path.GetRelativePath(basePath, fullPath);
    }

    internal static string GetInstructionContent()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Cop.Cli.InitInstructions.md");
        if (stream == null)
            throw new InvalidOperationException("Embedded resource 'Cop.Cli.InitInstructions.md' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
