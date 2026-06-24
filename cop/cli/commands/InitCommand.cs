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
    private const string CopCheckCommand = "cop cop-checks/main.cop -t . -om";

    public static Command Create()
    {
        var claudeOption = new Option<bool>("--claude", "Generate Claude Code instructions instead of GitHub Copilot");
        var localHookOption = new Option<bool>("--al", "Generate local Claude Code hook (.claude/settings.local.json); implies --claude");
        var globalHookOption = new Option<bool>("--ag", "Generate shared Claude Code hook (.claude/settings.json); implies --claude");
        var copilotHookOption = new Option<bool>("--ch", "Generate GitHub Copilot CLI hook (.github/hooks/cop-check.json)");
        var command = new Command("init", "Generate agent instruction files for writing cop rules (GitHub Copilot by default, Claude Code with --claude)")
        {
            claudeOption,
            localHookOption,
            globalHookOption,
            copilotHookOption
        };

        command.SetAction(ctx => Execute(
            ctx.GetValue(claudeOption) || ctx.GetValue(localHookOption) || ctx.GetValue(globalHookOption),
            ctx.GetValue(localHookOption),
            ctx.GetValue(globalHookOption),
            ctx.GetValue(copilotHookOption)));

        return command;
    }

    public static int Execute(bool claude = false, bool localHook = false, bool globalHook = false, bool copilotHook = false)
    {
        var cwd = Directory.GetCurrentDirectory();
        int filesUpdated = 0;

        // AGENTS.md — generic, cross-agent instructions. Written in both modes because
        // it is the shared standard read by GitHub Copilot, Claude Code, and others.
        var agentsPath = Path.Combine(cwd, "AGENTS.md");
        var agentsResult = MergeCopSection(agentsPath);
        Console.WriteLine($"{agentsResult}: AGENTS.md");
        filesUpdated++;

        if (claude)
        {
            // ── Claude Code (cop init --claude) ──────────────────────────────
            // .claude/commands/cop.md (Claude Code custom /cop command)
            var claudeCommandsDir = Path.Combine(cwd, ".claude", "commands");
            var copCommandPath = Path.Combine(claudeCommandsDir, "cop.md");
            Directory.CreateDirectory(claudeCommandsDir);
            File.WriteAllText(copCommandPath, GetCopCommandContent());
            Console.WriteLine($"Updated: {GetRelativePath(cwd, copCommandPath)}");
            filesUpdated++;

            // Claude Code hook settings
            if (localHook)
            {
                int result = GenerateClaudeHook(cwd, "settings.local.json");
                if (result < 0) return 1;
                filesUpdated += result;

                result = GenerateCopilotHook(cwd);
                if (result < 0) return 1;
                filesUpdated += result;
            }
            if (globalHook)
            {
                int result = GenerateClaudeHook(cwd, "settings.json");
                if (result < 0) return 1;
                filesUpdated += result;
            }
        }
        else
        {
            // ── GitHub Copilot (cop init, default) ───────────────────────────
            // .github/copilot-instructions.md (merge cop section)
            var githubDir = Path.Combine(cwd, ".github");
            var copilotPath = Path.Combine(githubDir, "copilot-instructions.md");
            Directory.CreateDirectory(githubDir);
            var copilotResult = MergeCopSection(copilotPath);
            Console.WriteLine($"{copilotResult}: {GetRelativePath(cwd, copilotPath)}");
            filesUpdated++;

            // .github/skills/cop/SKILL.md (GitHub Copilot CLI agent skill)
            var copilotSkillDir = Path.Combine(githubDir, "skills", "cop");
            var copilotSkillPath = Path.Combine(copilotSkillDir, "SKILL.md");
            Directory.CreateDirectory(copilotSkillDir);
            File.WriteAllText(copilotSkillPath, GetCopilotSkillContent());
            Console.WriteLine($"Updated: {GetRelativePath(cwd, copilotSkillPath)}");
            filesUpdated++;

            // GitHub Copilot CLI hook settings
            if (copilotHook)
            {
                int result = GenerateCopilotHook(cwd);
                if (result < 0) return 1;
                filesUpdated += result;
            }
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

        // Non-blocking hook: always exits 0 so the agent can finish its turn.
        // Violations are surfaced as informational text (agent sees them but isn't trapped).
        // The '|| true' ensures cop tool errors or pre-existing violations don't
        // create an infinite loop where the agent can never stop.
        var stopEntry = new JsonObject
        {
            ["matcher"] = "",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"{CopCheckCommand} || true"
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

    private static int GenerateCopilotHook(string cwd)
    {
        var hooksDir = Path.Combine(cwd, ".github", "hooks");
        var hookPath = Path.Combine(hooksDir, "cop-check.json");
        var scriptPath = Path.Combine(hooksDir, "cop-check.sh");
        var fullPath = Path.GetFullPath(hookPath);

        JsonObject root;

        if (File.Exists(hookPath))
        {
            // Always merge into existing file — never overwrite unrelated hooks
            string existingJson;
            try
            {
                existingJson = File.ReadAllText(hookPath);
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

            // Remove existing cop agentStop hook and re-add (ensures it's up-to-date)
            RemoveCopAgentStopHook(root);
            MergeAgentStopHook(root);
        }
        else
        {
            try
            {
                Directory.CreateDirectory(hooksDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: Cannot create directory {hooksDir}: {ex.Message}");
                return -1;
            }
            root = CreateFreshCopilotHookSettings();
        }

        try
        {
            WriteJson(hookPath, root);
            File.WriteAllText(scriptPath, GetCopilotHookScriptContent());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Cannot write GitHub Copilot hook files under {hooksDir}: {ex.Message}");
            return -1;
        }

        // Verify the write by reading back
        try
        {
            var written = File.ReadAllText(hookPath);
            var verified = JsonNode.Parse(written)?.AsObject();
            if (verified == null || verified["hooks"] is not JsonObject)
            {
                Console.Error.WriteLine($"Error: Verification failed — {fullPath} does not contain valid hooks JSON after write");
                return -1;
            }
            if (!File.Exists(scriptPath))
            {
                Console.Error.WriteLine($"Error: Verification failed — {Path.GetFullPath(scriptPath)} was not written");
                return -1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Verification failed — cannot read back GitHub Copilot hook files: {ex.Message}");
            return -1;
        }

        Console.WriteLine($"Wrote: {fullPath}");
        Console.WriteLine(File.ReadAllText(hookPath));
        Console.WriteLine($"Wrote: {Path.GetFullPath(scriptPath)}");
        Console.WriteLine(File.ReadAllText(scriptPath));
        Console.WriteLine("GitHub Copilot CLI loads hooks at startup — restart any running session to pick this up.");
        return 2;
    }

    private static void RemoveCopAgentStopHook(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        if (hooks["agentStop"] is not JsonArray arr) return;
        for (int i = arr.Count - 1; i >= 0; i--)
        {
            if (arr[i] is not JsonObject entry) continue;
            var bash = entry["bash"]?.GetValue<string>();
            var powershell = entry["powershell"]?.GetValue<string>();
            var commandStr = entry["command"]?.GetValue<string>();
            if ((bash != null && (bash.Contains("cop cop-checks/main.cop") || bash.Contains(".github/hooks/cop-check.sh")))
                || (powershell != null && powershell.Contains("cop cop-checks/main.cop"))
                || (commandStr != null && commandStr.Contains("cop cop-checks/main.cop")))
            {
                arr.RemoveAt(i);
            }
        }
        if (arr.Count == 0)
            hooks.Remove("agentStop");
    }

    private static void MergeAgentStopHook(JsonObject root)
    {
        if (root["version"] == null)
            root["version"] = 1;

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var hookEntry = new JsonObject
        {
            ["type"] = "command",
            ["bash"] = "bash .github/hooks/cop-check.sh",
            ["cwd"] = ".",
            ["timeoutSec"] = 120
        };

        if (hooks["agentStop"] is JsonArray existing)
        {
            existing.Add(hookEntry);
        }
        else
        {
            hooks["agentStop"] = new JsonArray(hookEntry);
        }
    }

    private static JsonObject CreateFreshCopilotHookSettings()
    {
        var root = new JsonObject { ["version"] = 1 };
        MergeAgentStopHook(root);
        return root;
    }

    private static string GetCopilotHookScriptContent()
    {
        return string.Join("\n", new[]
        {
            $"out=\"$({CopCheckCommand} 2>&1)\"",
            "code=$?",
            "if [ \"$code\" -ne 0 ] && [ -n \"$out\" ]; then",
            "  python3 -c 'import json,sys; print(json.dumps({\"decision\":\"block\",\"reason\":sys.argv[1]}))' \\",
            "    \"cop check FAILED:",
            "$out",
            "Fix the cop check violations, then re-run the check until it passes.\"",
            "fi",
            "exit 0"
        }) + "\n";
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

    private static string GetCopilotSkillContent()
    {
        return """
            ---
            name: cop
            description: Run cop static analysis on this repository. Use this skill whenever asked to run cop, run cop checks, lint or analyze this codebase with cop, or verify the repo against its cop-checks.
            ---

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
