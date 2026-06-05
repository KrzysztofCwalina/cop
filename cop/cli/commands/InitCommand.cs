using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cop.Cli.Commands;

public static class InitCommand
{
    public static Command Create()
    {
        var forceOption = new Option<bool>("--force", "Overwrite existing instruction files");
        var localHookOption = new Option<bool>("--al", "Generate local Claude Code hook (.claude/settings.local.json)");
        var globalHookOption = new Option<bool>("--ag", "Generate shared Claude Code hook (.claude/settings.json)");
        var command = new Command("init", "Generate agent instruction files for writing cop rules")
        {
            forceOption,
            localHookOption,
            globalHookOption
        };

        command.SetAction(ctx => Execute(
            ctx.GetValue(forceOption),
            ctx.GetValue(localHookOption),
            ctx.GetValue(globalHookOption)));

        return command;
    }

    public static int Execute(bool force = false, bool localHook = false, bool globalHook = false)
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

        // Generate Claude Code hook settings
        if (localHook)
            filesCreated += GenerateClaudeHook(cwd, "settings.local.json", force);
        if (globalHook)
            filesCreated += GenerateClaudeHook(cwd, "settings.json", force);

        if (filesCreated > 0)
            Console.WriteLine($"\n{filesCreated} file(s) created. Agents will now discover cop language context automatically.");
        else
            Console.WriteLine("\nNo files created (all already exist). Use --force to overwrite.");

        return 0;
    }

    private static int GenerateClaudeHook(string cwd, string fileName, bool force)
    {
        var claudeDir = Path.Combine(cwd, ".claude");
        var settingsPath = Path.Combine(claudeDir, fileName);

        if (File.Exists(settingsPath) && !force)
        {
            // Merge hook into existing file
            try
            {
                var existingJson = File.ReadAllText(settingsPath);
                var root = JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();
                if (HasCopStopHook(root))
                {
                    Console.Error.WriteLine($"Skipped: {GetRelativePath(cwd, settingsPath)} already has cop Stop hook");
                    return 0;
                }
                MergeStopHook(root);
                WriteJson(settingsPath, root);
                Console.WriteLine($"Updated: {GetRelativePath(cwd, settingsPath)} (added cop Stop hook)");
                return 1;
            }
            catch (JsonException)
            {
                Console.Error.WriteLine($"Skipped: {GetRelativePath(cwd, settingsPath)} has invalid JSON (use --force to overwrite)");
                return 0;
            }
        }
        else if (File.Exists(settingsPath) && force)
        {
            // Force overwrite: parse and merge, or create fresh
            try
            {
                var existingJson = File.ReadAllText(settingsPath);
                var root = JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();
                MergeStopHook(root);
                WriteJson(settingsPath, root);
            }
            catch (JsonException)
            {
                Directory.CreateDirectory(claudeDir);
                WriteJson(settingsPath, CreateFreshHookSettings());
            }
            Console.WriteLine($"Updated: {GetRelativePath(cwd, settingsPath)}");
            return 1;
        }
        else
        {
            // Create fresh
            Directory.CreateDirectory(claudeDir);
            WriteJson(settingsPath, CreateFreshHookSettings());
            Console.WriteLine($"Created: {GetRelativePath(cwd, settingsPath)}");
            return 1;
        }
    }

    private static bool HasCopStopHook(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return false;
        if (hooks["Stop"] is not JsonArray stopArray) return false;
        foreach (var entry in stopArray)
        {
            if (entry is not JsonObject entryObj) continue;
            if (entryObj["hooks"] is not JsonArray innerHooks) continue;
            foreach (var hook in innerHooks)
            {
                if (hook is not JsonObject hookObj) continue;
                var command = hookObj["command"]?.GetValue<string>();
                if (command != null && command.Contains("cop cop-checks/main.cop"))
                    return true;
            }
        }
        return false;
    }

    private static void MergeStopHook(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var stopEntry = new JsonObject
        {
            ["matcher"] = "",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = "cop cop-checks/main.cop -t .",
                ["timeout"] = 120
            })
        };

        if (hooks["Stop"] is JsonArray existingStop)
        {
            existingStop.Add(stopEntry);
        }
        else
        {
            hooks["Stop"] = new JsonArray(stopEntry);
        }
    }

    private static JsonObject CreateFreshHookSettings()
    {
        var root = new JsonObject();
        MergeStopHook(root);
        return root;
    }

    private static void WriteJson(string path, JsonObject root)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, root.ToJsonString(options));
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
