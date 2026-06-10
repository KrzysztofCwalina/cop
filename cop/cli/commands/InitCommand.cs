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
    private const string SectionStart = "<!-- BEGIN COP INSTRUCTIONS -->";
    private const string SectionEnd = "<!-- END COP INSTRUCTIONS -->";

    public static Command Create()
    {
        var localHookOption = new Option<bool>("--al", "Generate local Claude Code hook (.claude/settings.local.json)");
        var globalHookOption = new Option<bool>("--ag", "Generate shared Claude Code hook (.claude/settings.json)");
        var command = new Command("init", "Generate agent instruction files for writing cop rules")
        {
            localHookOption,
            globalHookOption
        };

        command.SetAction(ctx => Execute(
            ctx.GetValue(localHookOption),
            ctx.GetValue(globalHookOption)));

        return command;
    }

    public static int Execute(bool localHook = false, bool globalHook = false)
    {
        var cwd = Directory.GetCurrentDirectory();
        int filesUpdated = 0;

        // Generate .github/copilot-instructions.md (merge cop section)
        var githubDir = Path.Combine(cwd, ".github");
        var copilotPath = Path.Combine(githubDir, "copilot-instructions.md");
        Directory.CreateDirectory(githubDir);
        var copilotResult = MergeCopSection(copilotPath);
        Console.WriteLine($"{copilotResult}: {GetRelativePath(cwd, copilotPath)}");
        filesUpdated++;

        // Generate AGENTS.md (merge cop section)
        var agentsPath = Path.Combine(cwd, "AGENTS.md");
        var agentsResult = MergeCopSection(agentsPath);
        Console.WriteLine($"{agentsResult}: AGENTS.md");
        filesUpdated++;

        // Generate .claude/commands/cop.md (cop-specific file, always write)
        var claudeCommandsDir = Path.Combine(cwd, ".claude", "commands");
        var copCommandPath = Path.Combine(claudeCommandsDir, "cop.md");
        Directory.CreateDirectory(claudeCommandsDir);
        File.WriteAllText(copCommandPath, GetCopCommandContent());
        Console.WriteLine($"Updated: {GetRelativePath(cwd, copCommandPath)}");
        filesUpdated++;

        // Generate Claude Code hook settings
        if (localHook)
        {
            int result = GenerateClaudeHook(cwd, "settings.local.json");
            if (result < 0) return 1;
            filesUpdated += result;
        }
        if (globalHook)
        {
            int result = GenerateClaudeHook(cwd, "settings.json");
            if (result < 0) return 1;
            filesUpdated += result;
        }

        Console.WriteLine($"\n{filesUpdated} file(s) updated. Agents will now discover cop language context automatically.");

        return 0;
    }

    /// <summary>
    /// Merges the cop instruction section into a markdown file.
    /// If the file doesn't exist, creates it with the cop section.
    /// If the file exists but has no cop section, appends it.
    /// If the file exists and already has the cop section, updates it in-place.
    /// Returns a status string: "Created", "Updated", or "Up-to-date".
    /// </summary>
    private static string MergeCopSection(string filePath)
    {
        var wrappedContent = $"{SectionStart}\n{GetInstructionContent()}\n{SectionEnd}\n";

        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, wrappedContent);
            return "Created";
        }

        var existing = File.ReadAllText(filePath);
        var startIdx = existing.IndexOf(SectionStart, StringComparison.Ordinal);
        var endIdx = existing.IndexOf(SectionEnd, StringComparison.Ordinal);

        if (startIdx >= 0 && endIdx > startIdx)
        {
            // Replace existing section (include the end marker + trailing newline)
            var endOfSection = endIdx + SectionEnd.Length;
            if (endOfSection < existing.Length && existing[endOfSection] == '\n')
                endOfSection++;

            var before = existing[..startIdx];
            var after = existing[endOfSection..];
            var merged = before + wrappedContent + after;

            if (merged == existing)
                return "Up-to-date";

            File.WriteAllText(filePath, merged);
            return "Updated";
        }

        // No existing section — append with a blank line separator
        var separator = existing.Length > 0 && !existing.EndsWith("\n\n") ? "\n" : "";
        File.WriteAllText(filePath, existing + separator + wrappedContent);
        return "Updated";
    }

    private static int GenerateClaudeHook(string cwd, string fileName)
    {
        var claudeDir = Path.Combine(cwd, ".claude");
        var settingsPath = Path.Combine(claudeDir, fileName);
        var fullPath = Path.GetFullPath(settingsPath);

        JsonObject root;

        if (File.Exists(settingsPath))
        {
            // Always merge into existing file — never overwrite
            string existingJson;
            try
            {
                existingJson = File.ReadAllText(settingsPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: Cannot read {fullPath}: {ex.Message}");
                return -1;
            }

            try
            {
                root = JsonNode.Parse(existingJson)?.AsObject()
                    ?? throw new JsonException("File is not a JSON object");
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Error: {fullPath} contains invalid JSON: {ex.Message}");
                return -1;
            }

            // Remove existing cop hook and re-add (ensures it's up-to-date)
            if (HasCopStopHook(root))
                RemoveCopStopHook(root);

            MergeStopHook(root);
        }
        else
        {
            // Create fresh
            try
            {
                Directory.CreateDirectory(claudeDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: Cannot create directory {claudeDir}: {ex.Message}");
                return -1;
            }
            root = CreateFreshHookSettings();
        }

        // Write file
        try
        {
            WriteJson(settingsPath, root);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Cannot write {fullPath}: {ex.Message}");
            return -1;
        }

        // Verify the write by reading back
        try
        {
            var written = File.ReadAllText(settingsPath);
            var verified = JsonNode.Parse(written)?.AsObject();
            if (verified == null || verified["hooks"] is not JsonObject)
            {
                Console.Error.WriteLine($"Error: Verification failed — {fullPath} does not contain valid hooks JSON after write");
                return -1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Verification failed — cannot read back {fullPath}: {ex.Message}");
            return -1;
        }

        // Print success with full path and content
        Console.WriteLine($"Wrote: {fullPath}");
        Console.WriteLine(File.ReadAllText(settingsPath));

        // Verify cop is in PATH (hook will fail silently if it's not)
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cop", "-v")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch
        {
            Console.Error.WriteLine("Warning: 'cop' not found in PATH. The hook will fail unless cop is accessible.");
        }

        Console.WriteLine($"Verify in Claude Code: type /hooks then select Stop");
        return 1;
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

    private static void RemoveCopStopHook(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        if (hooks["Stop"] is not JsonArray stopArray) return;
        for (int i = stopArray.Count - 1; i >= 0; i--)
        {
            if (stopArray[i] is not JsonObject entryObj) continue;
            if (entryObj["hooks"] is not JsonArray innerHooks) continue;
            foreach (var hook in innerHooks)
            {
                if (hook is not JsonObject hookObj) continue;
                var command = hookObj["command"]?.GetValue<string>();
                if (command != null && command.Contains("cop cop-checks/main.cop"))
                {
                    stopArray.RemoveAt(i);
                    break;
                }
            }
        }
        if (stopArray.Count == 0)
            hooks.Remove("Stop");
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
                ["command"] = "cop cop-checks/main.cop -t . -om"
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

    private static string GetCopCommandContent()
    {
        return """
            Run cop static analysis on this repository.

            Execute the following command:
            ```
            cop cop-checks/main.cop -t .
            ```

            This runs all cop checks defined in `cop-checks/main.cop` against the repository root.
            If there are violations, fix them before continuing.

            If `cop-checks/` doesn't exist, tell the user they need to create cop check files first.
            Run `cop help language` for the full language reference if you need to write or fix cop rules.
            """.Replace("            ", "");
    }
}
