// Generates project instructions and initial configuration for authoring Cop checks.

using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cop.Cli.Commands;

/// <summary>Installs project instructions and optional agent check hooks.</summary>
public static class InitCommand
{
    private const string SectionStart = "<!-- BEGIN COP INSTRUCTIONS -->";
    private const string SectionEnd = "<!-- END COP INSTRUCTIONS -->";
    private const string DefaultCopCommand = "cop";

    // The full check-run command, parameterized by how cop is invoked (default `cop`,
    // or a custom prefix like `mise exec -- cop` when cop isn't on PATH).
    private static string CheckCommand(string copCmd) => $"{copCmd} cop-checks/main.cop -t . -om";

    /// <summary>
    /// Creates the initialization command and its agent integration options.
    /// </summary>
    public static Command Create()
    {
        Option<bool> claudeOption = new Option<bool>("--claude", "Generate Claude Code instructions instead of GitHub Copilot");
        Option<bool> localHookOption = new Option<bool>("--al", "Generate local Claude Code hook (.claude/settings.local.json); implies --claude");
        Option<bool> globalHookOption = new Option<bool>("--ag", "Generate shared Claude Code hook (.claude/settings.json); implies --claude");
        Option<bool> copilotHookOption = new Option<bool>("--ch", "Generate GitHub Copilot CLI hook (.github/hooks/cop-check.json)");
        Option<string?> copCmdOption = new Option<string?>("--cop-cmd", "Command used to invoke cop in generated files and hooks when cop is not on PATH (e.g. \"mise exec -- cop\"). Default: cop");
        Command command = new Command("init", "Generate agent instruction files for writing cop rules (GitHub Copilot by default, Claude Code with --claude)")
        {
            claudeOption,
            localHookOption,
            globalHookOption,
            copilotHookOption,
            copCmdOption
        };

        command.SetAction(ctx => Execute(new InitializationOptions
        {
            Agent = ctx.GetValue(claudeOption) || ctx.GetValue(localHookOption) ||
                ctx.GetValue(globalHookOption) ? InstructionAgent.Claude : InstructionAgent.Copilot,
            IsLocalHookEnabled = ctx.GetValue(localHookOption),
            IsGlobalHookEnabled = ctx.GetValue(globalHookOption),
            IsCopilotHookEnabled = ctx.GetValue(copilotHookOption),
            CopCommand = ctx.GetValue(copCmdOption)
        }));

        return command;
    }

    /// <summary>Generates agent instructions and the requested repository hooks.</summary>
    public static int Execute(InitializationOptions? options = null)
    {
        options ??= new InitializationOptions();
        bool claude = options.Agent == InstructionAgent.Claude;
        bool localHook = options.IsLocalHookEnabled;
        bool globalHook = options.IsGlobalHookEnabled;
        bool copilotHook = options.IsCopilotHookEnabled;
        string? copCmd = options.CopCommand;
        string cwd = Directory.GetCurrentDirectory();
        int filesUpdated = 0;

        copCmd = string.IsNullOrWhiteSpace(copCmd) ? DefaultCopCommand : copCmd.Trim();

        // AGENTS.md — generic, cross-agent instructions. Written in both modes because
        // it is the shared standard read by GitHub Copilot, Claude Code, and others.
        string agentsPath = Path.Combine(cwd, "AGENTS.md");
        string agentsResult = MergeCopSection(agentsPath, copCmd);
        Console.WriteLine($"{agentsResult}: AGENTS.md");
        filesUpdated++;

        if (claude)
        {
            // ── Claude Code (cop init --claude) ──────────────────────────────
            // .claude/commands/cop.md (Claude Code custom /cop command)
            string claudeCommandsDir = Path.Combine(cwd, ".claude", "commands");
            string copCommandPath = Path.Combine(claudeCommandsDir, "cop.md");
            Directory.CreateDirectory(claudeCommandsDir);
            File.WriteAllText(copCommandPath, GetCopCommandContent(copCmd));
            Console.WriteLine($"Updated: {GetRelativePath(cwd, copCommandPath)}");
            filesUpdated++;

            // Claude Code hook settings
            if (localHook)
            {
                int result = GenerateClaudeHook(cwd, "settings.local.json", copCmd);
                if (result < 0)
                {
                    return 1;
                }

                filesUpdated += result;

                result = GenerateCopilotHook(cwd, copCmd);
                if (result < 0)
                {
                    return 1;
                }

                filesUpdated += result;
            }
            if (globalHook)
            {
                int result = GenerateClaudeHook(cwd, "settings.json", copCmd);
                if (result < 0)
                {
                    return 1;
                }

                filesUpdated += result;
            }
        }
        else
        {
            // ── GitHub Copilot (cop init, default) ───────────────────────────
            // .github/copilot-instructions.md (merge cop section)
            string githubDir = Path.Combine(cwd, ".github");
            string copilotPath = Path.Combine(githubDir, "copilot-instructions.md");
            Directory.CreateDirectory(githubDir);
            string copilotResult = MergeCopSection(copilotPath, copCmd);
            Console.WriteLine($"{copilotResult}: {GetRelativePath(cwd, copilotPath)}");
            filesUpdated++;

            // .github/skills/cop/SKILL.md (GitHub Copilot CLI agent skill)
            string copilotSkillDir = Path.Combine(githubDir, "skills", "cop");
            string copilotSkillPath = Path.Combine(copilotSkillDir, "SKILL.md");
            Directory.CreateDirectory(copilotSkillDir);
            File.WriteAllText(copilotSkillPath, GetCopilotSkillContent(copCmd));
            Console.WriteLine($"Updated: {GetRelativePath(cwd, copilotSkillPath)}");
            filesUpdated++;

            // GitHub Copilot CLI hook settings
            if (copilotHook)
            {
                int result = GenerateCopilotHook(cwd, copCmd);
                if (result < 0)
                {
                    return 1;
                }

                filesUpdated += result;
            }
        }

        Console.WriteLine($"\n{filesUpdated} file(s) updated. Agents will now discover cop language context automatically.");

        if (copCmd == DefaultCopCommand && DetectMiseConfig(cwd))
        {
            Console.WriteLine();
            Console.WriteLine("Note: mise config detected. If cop isn't on your PATH, re-run with");
            Console.WriteLine("  cop init --cop-cmd \"mise exec -- cop\"");
            Console.WriteLine("to embed your mise invocation in the generated files and hooks.");
        }

        return 0;
    }

    /// <summary>
    /// Merges the cop instruction section into a markdown file.
    /// If the file doesn't exist, creates it with the cop section.
    /// If the file exists but has no cop section, appends it.
    /// If the file exists and already has the cop section, updates it in-place.
    /// Returns a status string: "Created", "Updated", or "Up-to-date".
    /// </summary>
    private static string MergeCopSection(string filePath, string copCmd)
    {
        string wrappedContent = $"{SectionStart}\n{GetInstructionContent(copCmd)}\n{SectionEnd}\n";

        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, wrappedContent);
            return "Created";
        }

        string existing = File.ReadAllText(filePath);
        int startIdx = existing.IndexOf(SectionStart, StringComparison.Ordinal);
        int endIdx = existing.IndexOf(SectionEnd, StringComparison.Ordinal);

        if (startIdx >= 0 && endIdx > startIdx)
        {
            // Replace existing section (include the end marker + trailing newline)
            int endOfSection = endIdx + SectionEnd.Length;
            if (endOfSection < existing.Length && existing[endOfSection] == '\n')
            {
                endOfSection++;
            }

            string before = existing[..startIdx];
            string after = existing[endOfSection..];
            string merged = before + wrappedContent + after;

            if (merged == existing)
            {
                return "Up-to-date";
            }

            File.WriteAllText(filePath, merged);
            return "Updated";
        }

        // No existing section — append with a blank line separator
        string separator = existing.Length > 0 && !existing.EndsWith("\n\n") ? "\n" : "";
        File.WriteAllText(filePath, existing + separator + wrappedContent);
        return "Updated";
    }

    private static int GenerateClaudeHook(string cwd, string fileName, string copCmd)
    {
        string claudeDir = Path.Combine(cwd, ".claude");
        string settingsPath = Path.Combine(claudeDir, fileName);
        string fullPath = Path.GetFullPath(settingsPath);

        JsonObject root;

        if (File.Exists(settingsPath))
        {
            // Always merge into existing file — never overwrite
            string existingJson;
            try
            {
                existingJson = File.ReadAllText(settingsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Error: Cannot read {fullPath}: {ex.Message}");
                return -1;
            }

            try
            {
                root = JsonNode.Parse(existingJson) as JsonObject
                    ?? throw new JsonException("File is not a JSON object");
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Error: {fullPath} contains invalid JSON: {ex.Message}");
                return -1;
            }

            // Remove existing cop hook and re-add (ensures it's up-to-date)
            if (HasCopStopHook(root))
            {
                RemoveCopStopHook(root);
            }

            MergeStopHook(root, copCmd);
        }
        else
        {
            // Create fresh
            try
            {
                Directory.CreateDirectory(claudeDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Error: Cannot create directory {claudeDir}: {ex.Message}");
                return -1;
            }
            root = CreateFreshHookSettings(copCmd);
        }

        // Write file
        try
        {
            WriteJson(settingsPath, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: Cannot write {fullPath}: {ex.Message}");
            return -1;
        }

        // Verify the write by reading back
        try
        {
            string written = File.ReadAllText(settingsPath);
            JsonObject? verified = JsonNode.Parse(written)?.AsObject();
            if (verified == null || verified["hooks"] is not JsonObject)
            {
                Console.Error.WriteLine($"Error: Verification failed — {fullPath} does not contain valid hooks JSON after write");
                return -1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Error: Verification failed — cannot read back {fullPath}: {ex.Message}");
            return -1;
        }

        // Print success with full path and content
        Console.WriteLine($"Wrote: {fullPath}");
        Console.WriteLine(File.ReadAllText(settingsPath));

        // Verify the cop invocation runs (the hook will fail silently if it's not accessible)
        try
        {
            (string? probeFile, string? probeArgs) = SplitCommand(copCmd);
            string args = string.IsNullOrEmpty(probeArgs) ? "-v" : probeArgs + " -v";
            ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(probeFile, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process? proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or
            IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Warning: '{copCmd}' could not be run. The hook will fail unless cop is accessible.");
        }

        Console.WriteLine($"Verify in Claude Code: type /hooks then select Stop");
        return 1;
    }

    private static bool HasCopStopHook(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        if (hooks["Stop"] is not JsonArray stopArray)
        {
            return false;
        }

        foreach (JsonNode? entry in stopArray)
        {
            if (entry is not JsonObject entryObj)
            {
                continue;
            }

            if (entryObj["hooks"] is not JsonArray innerHooks)
            {
                continue;
            }

            foreach (JsonNode? hook in innerHooks)
            {
                if (hook is not JsonObject hookObj)
                {
                    continue;
                }

                string? command = hookObj["command"]?.GetValue<string>();
                if (command != null && command.Contains("cop-checks/main.cop"))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void RemoveCopStopHook(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            return;
        }

        if (hooks["Stop"] is not JsonArray stopArray)
        {
            return;
        }

        for (int i = stopArray.Count - 1; i >= 0; i--)
        {
            if (stopArray[i] is not JsonObject entryObj)
            {
                continue;
            }

            if (entryObj["hooks"] is not JsonArray innerHooks)
            {
                continue;
            }

            foreach (JsonNode? hook in innerHooks)
            {
                if (hook is not JsonObject hookObj)
                {
                    continue;
                }

                string? command = hookObj["command"]?.GetValue<string>();
                if (command != null && command.Contains("cop-checks/main.cop"))
                {
                    stopArray.RemoveAt(i);
                    break;
                }
            }
        }
        if (stopArray.Count == 0)
        {
            hooks.Remove("Stop");
        }
    }

    private static void MergeStopHook(JsonObject root, string copCmd)
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
        JsonObject stopEntry = new JsonObject
        {
            ["matcher"] = "",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"{CheckCommand(copCmd)} || true"
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

    private static JsonObject CreateFreshHookSettings(string copCmd)
    {
        JsonObject root = new JsonObject();
        MergeStopHook(root, copCmd);
        return root;
    }

    private static int GenerateCopilotHook(string cwd, string copCmd)
    {
        string hooksDir = Path.Combine(cwd, ".github", "hooks");
        string hookPath = Path.Combine(hooksDir, "cop-check.json");
        string scriptPath = Path.Combine(hooksDir, "cop-check.sh");
        string fullPath = Path.GetFullPath(hookPath);

        JsonObject root;

        if (File.Exists(hookPath))
        {
            // Always merge into existing file — never overwrite unrelated hooks
            string existingJson;
            try
            {
                existingJson = File.ReadAllText(hookPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Error: Cannot read {fullPath}: {ex.Message}");
                return -1;
            }

            try
            {
                root = JsonNode.Parse(existingJson) as JsonObject
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Error: Cannot create directory {hooksDir}: {ex.Message}");
                return -1;
            }
            root = CreateFreshCopilotHookSettings();
        }

        try
        {
            WriteJson(hookPath, root);
            File.WriteAllText(scriptPath, GetCopilotHookScriptContent(copCmd));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: Cannot write GitHub Copilot hook files under {hooksDir}: {ex.Message}");
            return -1;
        }

        // Verify the write by reading back
        try
        {
            string written = File.ReadAllText(hookPath);
            JsonObject? verified = JsonNode.Parse(written)?.AsObject();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException)
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
        if (root["hooks"] is not JsonObject hooks)
        {
            return;
        }

        if (hooks["agentStop"] is not JsonArray arr)
        {
            return;
        }

        for (int i = arr.Count - 1; i >= 0; i--)
        {
            if (arr[i] is not JsonObject entry)
            {
                continue;
            }

            string? bash = entry["bash"]?.GetValue<string>();
            string? powershell = entry["powershell"]?.GetValue<string>();
            string? commandStr = entry["command"]?.GetValue<string>();
            if ((bash != null && (bash.Contains("cop-checks/main.cop") || bash.Contains(".github/hooks/cop-check.sh")))
                || (powershell != null && powershell.Contains("cop-checks/main.cop"))
                || (commandStr != null && commandStr.Contains("cop-checks/main.cop")))
            {
                arr.RemoveAt(i);
            }
        }
        if (arr.Count == 0)
        {
            hooks.Remove("agentStop");
        }
    }

    private static void MergeAgentStopHook(JsonObject root)
    {
        if (root["version"] == null)
        {
            root["version"] = 1;
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        JsonObject hookEntry = new JsonObject
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
        JsonObject root = new JsonObject { ["version"] = 1 };
        MergeAgentStopHook(root);
        return root;
    }

    private static string GetCopilotHookScriptContent(string copCmd)
    {
        return string.Join("\n", new[]
        {
            $"out=\"$({CheckCommand(copCmd)} 2>&1)\"",
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
        JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, root.ToJsonString(options));
    }

    private static string GetRelativePath(string basePath, string fullPath)
    {
        return Path.GetRelativePath(basePath, fullPath);
    }

    // Splits a cop invocation into the executable and its leading arguments, e.g.
    // "mise exec -- cop" => ("mise", "exec -- cop"); "cop" => ("cop", "").
    private static (string file, string args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        int idx = cmd.IndexOf(' ');
        return idx < 0 ? (cmd, string.Empty) : (cmd[..idx], cmd[(idx + 1)..]);
    }

    // True when the current directory looks like a mise-managed repo. Used only to print a
    // discovery hint suggesting --cop-cmd; never rewrites anything on its own.
    private static bool DetectMiseConfig(string cwd)
    {
        string[] candidates =
        {
            "mise.toml", ".mise.toml", "mise.local.toml", ".mise.local.toml",
            Path.Combine(".config", "mise", "config.toml"),
            Path.Combine(".config", "mise.toml"),
            Path.Combine(".mise", "config.toml"),
            ".tool-versions",
        };
        foreach (string rel in candidates)
        {
            if (File.Exists(Path.Combine(cwd, rel)))
            {
                return true;
            }
        }
        return false;
    }

    internal static string GetInstructionContent(string copCmd = DefaultCopCommand)
    {
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Cop.Cli.InitInstructions.md");
        if (stream == null)
        {
            throw new InvalidOperationException("Embedded resource 'Cop.Cli.InitInstructions.md' not found.");
        }

        using StreamReader reader = new StreamReader(stream);
        string content = reader.ReadToEnd().Replace("{{COP}}", copCmd);

        if (copCmd != DefaultCopCommand)
        {
            string callout =
                $"> **Invoking cop:** this project runs cop via `{copCmd}` — cop is not assumed to be on `PATH`. " +
                "Use that exact prefix for every `cop` command shown below.\n\n";
            content = callout + content;
        }

        return content;
    }

    private static string GetCopCommandContent(string copCmd)
    {
        return $$"""
            Run cop static analysis on this repository.

            Execute the following command:
            ```
            {{copCmd}} cop-checks/main.cop -t .
            ```

            This runs all cop checks defined in `cop-checks/main.cop` against the repository root.
            If there are violations, fix them before continuing.

            If `cop-checks/` doesn't exist, tell the user they need to create cop check files first.
            Run `{{copCmd}} help language` for the full language reference if you need to write or fix cop rules.
            For structural rules, use parsed facts; discover them with `{{copCmd}} help code`
            and `{{copCmd}} help <language>`. Before writing ANY text-based check, explain its
            limitations and ASK THE USER whether it is okay. No approval means do not write it.
            Verify authored checks with `{{copCmd}} verify cop-checks/ --strict-structure`.
            Use `--allow-text-check <name>` only for a specific check the user explicitly approved.
            """.Replace("            ", "");
    }

    private static string GetCopilotSkillContent(string copCmd)
    {
        return $$"""
            ---
            name: cop
            description: Run cop static analysis on this repository. Use this skill whenever asked to run cop, run cop checks, lint or analyze this codebase with cop, or verify the repo against its cop-checks.
            ---

            Run cop static analysis on this repository.

            Execute the following command:
            ```
            {{copCmd}} cop-checks/main.cop -t .
            ```

            This runs all cop checks defined in `cop-checks/main.cop` against the repository root.
            If there are violations, fix them before continuing.

            If `cop-checks/` doesn't exist, tell the user they need to create cop check files first.
            Run `{{copCmd}} help language` for the full language reference if you need to write or fix cop rules.
            For structural rules, use parsed facts; discover them with `{{copCmd}} help code`
            and `{{copCmd}} help <language>`. Before writing ANY text-based check, explain its
            limitations and ASK THE USER whether it is okay. No approval means do not write it.
            Verify authored checks with `{{copCmd}} verify cop-checks/ --strict-structure`.
            Use `--allow-text-check <name>` only for a specific check the user explicitly approved.
            """.Replace("            ", "");
    }
}
