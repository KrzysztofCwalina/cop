using System.ComponentModel;
using System.Diagnostics;

namespace Cop.Cli.Commands;

/// <summary>
/// `cop init --checks` — delegates conversion of the repository's natural-language coding
/// guidelines (e.g. .github/copilot-instructions.md) into deterministic static cop checks
/// under cop-checks/, by shelling out to an installed coding agent (GitHub Copilot CLI by
/// default; Claude Code with --claude). The agent writes the .cop files and runs its own
/// `cop verify` fix-loop; cop then runs a final verify gate.
/// </summary>
public static class ChecksCommand
{
    // Markers that wrap cop's own authoring guide inside instruction files. The agent must
    // convert the project's guidelines, NOT this block — keep in sync with InitCommand.
    private const string SectionStart = "<!-- BEGIN COP INSTRUCTIONS -->";
    private const string SectionEnd = "<!-- END COP INSTRUCTIONS -->";

    public static int Execute(bool claude = false)
        => Execute(claude, Directory.GetCurrentDirectory(), RunAgent);

    // Testable core: process execution is injected so the orchestration (guideline discovery,
    // prompt building, launch, final verify gate) can be unit-tested offline.
    internal static int Execute(
        bool claude,
        string cwd,
        Func<ProcessStartInfo, int> runAgent)
    {
        // 1. Require a source of project guidelines to convert.
        var copilotInstructions = Path.Combine(cwd, ".github", "copilot-instructions.md");
        var agentsMd = Path.Combine(cwd, "AGENTS.md");
        if (!File.Exists(copilotInstructions) && !File.Exists(agentsMd))
        {
            Console.Error.WriteLine("cop init --checks: no project guidelines found.");
            Console.Error.WriteLine("Expected .github/copilot-instructions.md (or AGENTS.md) in the current directory.");
            Console.Error.WriteLine("Run 'cop init' first (or add your coding guidelines), then retry.");
            return 1;
        }

        // 2. Build the task prompt and launch the agent in the repo. The agent executable is
        //    resolved against PATH by the OS (UseShellExecute=false); a missing agent surfaces as
        //    a Win32Exception, which we turn into an install hint.
        var agent = claude ? "claude" : "copilot";
        var prompt = BuildPrompt();
        var psi = BuildStartInfo(agent, prompt, cwd);

        Console.WriteLine($"Launching {agent} to generate cop checks from your guidelines...");
        Console.WriteLine($"  working directory: {cwd}");
        Console.WriteLine();

        int agentExit;
        try
        {
            agentExit = runAgent(psi);
        }
        catch (Win32Exception)
        {
            Console.Error.WriteLine($"cop init --checks: '{agent}' CLI not found on PATH.");
            if (claude)
                Console.Error.WriteLine("Install Claude Code: https://docs.anthropic.com/en/docs/claude-code");
            else
                Console.Error.WriteLine("Install GitHub Copilot CLI: https://docs.github.com/copilot/how-tos/set-up/install-copilot-cli");
            return 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"cop init --checks: failed to launch '{agent}': {ex.Message}");
            return 1;
        }

        // 4. Final verify gate — confirm the agent actually produced verifiable checks.
        Console.WriteLine();
        var checksDir = Path.Combine(cwd, "cop-checks");
        if (!Directory.Exists(checksDir) ||
            Directory.GetFiles(checksDir, "*.cop", SearchOption.AllDirectories).Length == 0)
        {
            Console.Error.WriteLine("cop init --checks: the agent did not produce any cop-checks/*.cop files.");
            if (agentExit != 0)
                Console.Error.WriteLine($"(the {agent} CLI exited with code {agentExit})");
            return 1;
        }

        Console.WriteLine("Verifying generated checks: cop verify cop-checks/");
        int verifyExit = VerifyCommand.Execute(checksDir);
        if (verifyExit != 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("cop init --checks: the generated checks do not verify cleanly yet.");
            Console.Error.WriteLine("Review cop-checks/, fix the reported errors (or re-run 'cop init --checks'),");
            Console.Error.WriteLine("then run: cop cop-checks/main.cop -t .");
            return 1;
        }

        // `cop verify` only checks syntax/types — it does NOT catch runtime failures (e.g. a
        // toError anchored on a collection). Actually RUN the checks so a crash is caught here
        // instead of being reported as success. Exit codes: 0 = clean, 1 = violations found
        // (the checks ran fine), 2 = fatal runtime error (the checks are broken).
        var mainCop = Path.Combine(checksDir, "main.cop");
        if (File.Exists(mainCop))
        {
            Console.WriteLine();
            Console.WriteLine("Running generated checks: cop cop-checks/main.cop -t .");
            int runExit = RunCommand.Execute(mainCop, target: cwd);
            if (runExit == 2)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("cop init --checks: the generated checks verify but FAIL at runtime (see the error above).");
                Console.Error.WriteLine("Review cop-checks/ and re-run 'cop init --checks'.");
                return 1;
            }
        }

        Console.WriteLine();
        Console.WriteLine("\u2713 cop-checks/ generated, verified, and run successfully. Run: cop cop-checks/main.cop -t .");
        return 0;
    }

    /// <summary>
    /// Builds the ProcessStartInfo that launches the chosen agent in non-interactive mode.
    /// The agent name is used as-is; the OS resolves it against PATH (UseShellExecute=false).
    /// Pure (no side effects) so the invocation can be asserted in tests.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string agent, string prompt, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = agent,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            // Inherit stdio so the user watches the agent work live.
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };

        if (agent == "claude")
        {
            // Claude Code headless: -p runs then exits; acceptEdits writes files without prompting.
            // (Best-effort: a user wanting fully autonomous shell use may need a broader mode.)
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(prompt);
            psi.ArgumentList.Add("--permission-mode");
            psi.ArgumentList.Add("acceptEdits");
        }
        else
        {
            // GitHub Copilot CLI headless: -p runs then exits; --allow-all-tools is required
            // for non-interactive tool use (write files, run `cop verify`).
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(prompt);
            psi.ArgumentList.Add("--allow-all-tools");
        }

        return psi;
    }

    /// <summary>
    /// The task prompt handed to the coding agent. Embeds cop's own cop-checks authoring guide
    /// (so the agent follows the canonical structure instead of rediscovering it), demands
    /// deterministic static checks, enforces repo cleanliness, and tells the agent to run its
    /// own `cop verify` fix-loop.
    /// </summary>
    internal static string BuildPrompt()
    {
        // The same authoring guide `cop init` writes — canonical templates + DO-NOT rules.
        var authoringGuide = InitCommand.GetInstructionContent();

        return $$"""
            You are setting up cop static-analysis checks for this repository.

            GOAL
            Convert this repository's project coding guidelines into deterministic, STATIC cop
            checks under a `cop-checks/` directory at the repo root. When you are finished,
            `cop verify cop-checks/` MUST pass cleanly.

            SOURCE OF GUIDELINES
            Read `.github/copilot-instructions.md` (and `AGENTS.md` if present). Convert the
            PROJECT'S guidelines only. IGNORE any content between `{{SectionStart}}` and
            `{{SectionEnd}}` — that block is cop's own authoring guide, not a project rule.

            OUTPUT STRUCTURE — FOLLOW EXACTLY
            - Put EVERY generated file under `cop-checks/` at the repo root (create it if needed).
            - ONE focused check per file: `cop-checks/<kebab-name>.cop`, each declaring a single
              violation list, e.g. `let <name>-violations = codebase.Types:isViolating :toError(...)`.
            - `cop-checks/main.cop` is the ONLY file that contains a `command`. It imports the
              packages, builds the codebase, composes every check's violation list with `+`, and
              ends with `command MAIN = CHECK(all-violations)`.
            - Do NOT put a `command` in any other file. Do NOT mash multiple checks into one file.
            - Do NOT create a single `checks.cop`/`check.cop` in the repo root — use the
              `cop-checks/` folder structure described above.

            BE EFFICIENT — YOU HAVE A LIMITED BUDGET
            - You already have the authoring guide AND verified examples below. Do NOT read the
              entire `cop help language` reference end-to-end; consult `cop help <package>` only
              for a specific field you actually need.
            - Do NOT exhaustively probe the model with throwaway scripts. Write the checks by
              adapting the verified examples, then run `cop verify cop-checks/` and fix the real
              errors it reports. Writing-then-verifying beats probing.
            - Your priority is delivering verified `cop-checks/`, not exploration. Finish the task.

            The cop authoring guide below contains the EXACT canonical templates and rules.
            Follow it precisely:
            ====================== COP AUTHORING GUIDE ======================
            {{authoringGuide}}
            =================== END COP AUTHORING GUIDE =====================

            WORKED EXAMPLES (verified cop — adapt these exact patterns)
            # cop-checks/main.cop  (the ONLY file with a command)
            import code
            import code
            import csharp
            let codebase = codebase(csharp.parse())
            let all-violations = public-type-namespace-violations + empty-catch-violations
            command MAIN = CHECK(all-violations)

            # cop-checks/public-types-in-namespace.cop
            predicate isPublicTypeWithoutNamespace(Type:isCSharp) => isPublic && Type.File.Namespace == nic
            let public-type-namespace-violations = codebase.Types:isPublicTypeWithoutNamespace
                :toError('{item.Name} in {item.File.Path} must be declared inside a namespace')

            # cop-checks/no-empty-catch.cop
            predicate isEmptyCatch(Statement) => Statement.Kind:equals('catch') && Statement.Children:empty
            let empty-catch-violations = codebase.Statements:isEmptyCatch
                :toError('empty catch block at {item.File.Path}:{item.Line}')

            Notes from these examples: compose predicate conditions with BARE predicate names
            (`isPublic`, not `Type:isPublic`); a missing value is `nic` (e.g. no namespace =>
            `Type.File.Namespace == nic`); `Statement.Kind` is a string compared with `:equals('catch')`.

            WORKING DISCIPLINE — KEEP THE REPOSITORY CLEAN
            - A folder of `.cop` files compiles as ONE program. NEVER place scratch, probe, or
              exploration `.cop` files inside the repository (including inside `cop-checks/`) —
              they collide with each other and corrupt the build.
            - If you need to experiment, do it in a throwaway directory OUTSIDE this repository
              (e.g. under the system temp directory) and DELETE it when done.
            - When you finish, the repository must contain ONLY the intended `cop-checks/*.cop`
              files you added — no stray `.cop`, no leftover scratch folders.

            HARD RULES
            - STATIC, deterministic checks only, built from the codebase model.
            - NEVER use `ai.judge` or any AI/LLM-based check.
            - Skip guidelines that cannot be expressed as a static check (vague/subjective ones).
            - Prefer semantic codebase elements (`codebase.Types`/`Statements`/`Calls`,
              `Type.Name`, `Statement.TypeName`/`MemberName`, `File.Usings`) over raw text matching.

            SYNTAX GOTCHAS (do not waste time rediscovering these)
            - Strings use SINGLE quotes only. Verbatim `@'...'` strings are NOT supported.
            - A predicate BODY must be a boolean EXPRESSION. A body that is a SOLE bare predicate
              name (`predicate p(Type) => isPublic`) passes `verify` but CRASHES at run — write the
              explicit call `=> isPublic(Type)`, or combine with `&&`/`||`/`!` (e.g. `=> isPublic(Type) && ...`).
            - `:toError`/`:toWarning`/`:toInfo` anchor on a SINGLE position: Statement/Type/
              Method/Line/Region (NOT File, and NOT a collection). A using-directive check
              anchors on Lines. If an anchor can be collection-valued (e.g. a `partial` type
              spanning files), it PASSES `verify` but CRASHES at run — anchor on a single
              element instead.
            - Run `cop help language` for syntax and `cop help <package>` for a package's API.

            ITERATE UNTIL IT COMPILES *AND RUNS*
            `cop verify` only checks syntax/types — it does NOT catch runtime failures. After
            writing the files you MUST do BOTH:
              1. Run `cop verify cop-checks/` and fix every error until it is clean.
              2. Run `cop cop-checks/main.cop -t .` and confirm it RUNS without a fatal/runtime
                 error. Printing violations is fine and expected; a crash or "fatal error" is
                 NOT. Fix until it runs cleanly.
            A check that verifies but crashes when run is NOT done.

            DELIVERABLE
            A clean `cop-checks/` that `cop verify cop-checks/` accepts (and nothing stray left in
            the repo), then print a short summary of which guidelines became checks and which you
            skipped (and why).
            """;
    }

    private static int RunAgent(ProcessStartInfo psi)
    {
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("process did not start");
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
