using System.CommandLine;
using System.CommandLine.Help;
using System.Diagnostics;
using System.Linq;
using Cop.Cli.Commands;
using Cop.Repl;

// Diagnostic verbosity from -d / -dd / -ddd (replaces the former COP_*_DIAG / COP_TRACE / COP_PARSE_DIAG env vars).
int diagLevel = args.Contains("-ddd") ? 3 : args.Contains("-dd") ? 2 : args.Contains("-d") ? 1 : 0;
Cop.Core.CopDiagnostics.Level = diagLevel;
bool diag = diagLevel >= 1;

// --ai-log <path> replaces the former COP_AI_LOG env var.
int aiLogIdx = Array.IndexOf(args, "--ai-log");
if (aiLogIdx >= 0 && aiLogIdx + 1 < args.Length)
    Cop.Core.CopDiagnostics.AiLogPath = args[aiLogIdx + 1];

// --no-color replaces the former NO_COLOR env var (color is also auto-disabled for non-terminal output).
if (args.Contains("--no-color"))
    ConsoleMarkdown.NoColor = true;

// --no-user-checks replaces the former COP_NO_USER_CHECKS env var.
if (args.Contains("--no-user-checks"))
    RunCommand.NoUserChecks = true;

// Known verbs (subcommands) — anything else is treated as a program to run
var knownVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "run", "test", "syntax", "verify", "lock", "unlock", "help", "package", "repl", "init", "update", "vscode"
};

// Once-a-day "new version available" notice + a "what's new" summary after an update.
// Interactive only and fail-silent, so it never blocks or pollutes scripted/CI output. Also
// suppressed for help/version/update and for an unknown/misspelled command, so a typo prints
// only the "unknown command" error instead of a stale post-update summary.
if (!Console.IsErrorRedirected && StartupNotices.ShouldShow(args, knownVerbs, ResolvesToRunnable))
    VersionNotifier.Notify();

long clrStartupMs = 0;
if (diag)
{
    var process = Process.GetCurrentProcess();
    clrStartupMs = (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
    Console.Error.WriteLine($"[diag] Process startup: {clrStartupMs}ms");
}

// Bare invocation (no arguments): look for local .cop files to run or show getting-started
if (args.Length == 0)
{
    return ExecuteDefault();
}

// Intercept help flags before System.CommandLine to show clean single-section help
if (args.Length == 1 && (args[0] == "-h" || args[0] == "-help" || args[0] == "--help"))
{
    Console.WriteLine($"""
        {HelpCommand.Banner}

        Usage:
          cop run <package>                  Run a package from a feed (auto-restores)
          cop <file.cop>                     Run a local .cop file
          cop                                Run local .cop files in the current directory
          cop package list                   Browse available packages
          cop help language                  Full language reference
          cop help <package>                 Package documentation
          cop init                           Generate Copilot instructions (--claude for Claude Code)
          cop init --checks                  Generate cop checks from existing instructions (via a coding agent)
          cop update                         Update cop to the latest release
          cop vscode                         Install VS Code extension
          cop test [<file>]                  Run tests
          cop verify [<path>]                Verify program correctness
          cop repl                           Interactive REPL

        Options:
          -t <dir>      Target directory
          -p <provider> Load a provider package (multiple allowed)
          -c <commands> Filter to specific commands (comma-separated)
          -f <format>   Output format: text or json
          -h            Show help
          -v            Show version
        """);
    return 0;
}

// System.CommandLine reserves 'help' as a directive, so intercept it before parsing
if (args[0] == "help")
{
    string? helpArg = args.Length >= 2 ? args[1] : null;
    return HelpCommand.Execute(helpArg);
}

// cop init — generate agent instruction files
if (args[0] == "init")
{
    var knownInitOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--claude", "--al", "--ag", "--ch", "--checks", "--cop-cmd" };

    // Extract the --cop-cmd value (supports "--cop-cmd <value>" and "--cop-cmd=<value>")
    // before the unknown-option scan, so its value is never mistaken for an option.
    string? copCmd = null;
    var initArgs = args.Skip(1).ToArray();
    for (int i = 0; i < initArgs.Length; i++)
    {
        var arg = initArgs[i];
        if (arg.Equals("--cop-cmd", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= initArgs.Length)
            {
                Console.Error.WriteLine("Option '--cop-cmd' requires a value, e.g. --cop-cmd \"mise exec -- cop\".");
                return 1;
            }
            copCmd = initArgs[++i];
            continue;
        }
        if (arg.StartsWith("--cop-cmd=", StringComparison.OrdinalIgnoreCase))
        {
            copCmd = arg["--cop-cmd=".Length..];
            continue;
        }
        if (arg.StartsWith('-') && !knownInitOptions.Contains(arg))
        {
            Console.Error.WriteLine($"Unknown option '{arg}'. Known options: --checks, --claude, --al, --ag, --ch, --cop-cmd");
            return 1;
        }
    }
    // cop init --checks — shell out to a coding agent to convert existing instructions into cop rules
    if (args.Contains("--checks"))
    {
        return ChecksCommand.Execute(args.Contains("--claude"));
    }
    bool localHook = args.Contains("--al");
    bool globalHook = args.Contains("--ag");
    bool copilotHook = args.Contains("--ch");
    // Default generates GitHub Copilot instructions; --claude (or a Claude hook flag) switches to Claude Code.
    bool claude = args.Contains("--claude") || localHook || globalHook;
    return InitCommand.Execute(claude, localHook, globalHook, copilotHook, copCmd);
}

// cop update — self-update from GitHub releases
if (args[0] == "update")
{
    return UpdateCommand.Execute(args.Contains("--force") || args.Contains("-f"));
}

// cop vscode — install VS Code extension
if (args[0] == "vscode")
{
    return VscodeCommand.Execute();
}

// Explicit run: `cop run <package|file.cop|url|command>` resolves and runs anything,
// including auto-restoring a package from a feed.
if (args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
{
    return ResolveAndRun(args.Length > 1 ? args[1..] : Array.Empty<string>(), diag, allowPackages: true);
}

// Bare `cop <name>` for a non-verb, non-switch token: run a local .cop file, URL, or a
// local UPPERCASE command, but DON'T auto-restore packages. Requiring explicit `cop run`
// for packages means a mistyped or non-existent verb surfaces as a clear error instead of
// a confusing "package not found" feed restore.
if (!knownVerbs.Contains(args[0]) && !args[0].StartsWith('-') && !args[0].StartsWith('/'))
{
    return ResolveAndRun(args, diag, allowPackages: false);
}

var rootCommand = new RootCommand
{
    Description = $"""
        {HelpCommand.Banner}

        Quick reference:
          cop run <package>                Run a package from a feed (auto-restores)
          cop <file.cop>                   Run a local .cop file
          cop package list                 Browse available packages
          cop help <package>               Package documentation
          cop test [<file>]               Run tests
          cop repl                        Interactive REPL
          cop <command> -h for details
        """
};

// Replace built-in --help/-?/-h with just -h
var defaultHelp = rootCommand.Options.FirstOrDefault(o => o is HelpOption);
if (defaultHelp != null) rootCommand.Options.Remove(defaultHelp);
rootCommand.Options.Add(new HelpOption("-h"));

// Replace built-in --version with -v/--version
var defaultVersion = rootCommand.Options.FirstOrDefault(o => o is VersionOption);
if (defaultVersion != null) rootCommand.Options.Remove(defaultVersion);
rootCommand.Options.Add(new VersionOption("-v", "--version"));

rootCommand.Add(TestCommand.Create());
rootCommand.Add(SyntaxCommand.Create());
rootCommand.Add(VerifyCommand.Create());
rootCommand.Add(LockCommand.Create());
rootCommand.Add(UnlockCommand.Create());

// cop repl — launch interactive REPL
var replCommand = new Command("repl", "Launch interactive REPL");
replCommand.SetAction(_ =>
{
    var scriptsDir = Directory.GetCurrentDirectory();
    var session = new ReplSession(scriptsDir, scriptsDir);
    return session.Run();
});
rootCommand.Add(replCommand);

var packageCommand = new Command("package", "Manage cop packages");
packageCommand.Add(PackageListCommand.Create());
packageCommand.Add(ListCommand.Create());
packageCommand.Add(RestoreCommand.Create());
packageCommand.Add(NewCommand.Create());
packageCommand.Add(ValidateCommand.Create());
packageCommand.Add(PublishCommand.Create());
packageCommand.Add(SearchCommand.Create());
packageCommand.Add(FeedCommand.Create());
rootCommand.Add(packageCommand);

return rootCommand.Parse(args).Invoke();

/// <summary>
/// Resolves and runs a target: a local .cop file, an HTTPS URL, a local UPPERCASE command,
/// or (only when <paramref name="allowPackages"/> is true) one or more package names that are
/// auto-restored from a feed. When packages aren't allowed and nothing else matches, prints a
/// clear "Unknown command" error instead of attempting a package restore.
/// </summary>
static int ResolveAndRun(string[] runArgs, bool diag, bool allowPackages)
{
    if (runArgs.Length == 0)
    {
        // `cop run` with no target behaves like a bare `cop`: run local .cop files.
        return ExecuteDefault();
    }

    var firstArg = runArgs[0];

    // 1. Explicit .cop file → run it directly
    if (firstArg.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
    {
        // Extract known flags (-t, -f, -d, -c, -om, -p) from remaining args
        string? copTarget = null;
        string? copFormat = null;
        string? copCommands = null;
        bool copDiag = diag;
        bool copOnlyIfModified = false;
        bool copProfile = false;
        var copProviders = new List<string>();
        var programArgs = new List<string>();
        var remaining = runArgs.Length > 1 ? runArgs[1..] : Array.Empty<string>();
        for (int i = 0; i < remaining.Length; i++)
        {
            if (remaining[i] == "-t" && i + 1 < remaining.Length) copTarget = remaining[++i];
            else if (remaining[i] == "-f" && i + 1 < remaining.Length) copFormat = remaining[++i];
            else if (remaining[i] == "-c" && i + 1 < remaining.Length) copCommands = remaining[++i];
            else if (remaining[i] == "-p" && i + 1 < remaining.Length) copProviders.Add(remaining[++i]);
            else if (remaining[i] == "-d" || remaining[i] == "-dd" || remaining[i] == "-ddd") copDiag = true;
            else if (remaining[i] == "-om") copOnlyIfModified = true;
            else if (remaining[i] == "-rp") copProfile = true;
            else if (remaining[i] == "--no-color" || remaining[i] == "--no-user-checks") { /* handled globally */ }
            else if (remaining[i] == "--ai-log" && i + 1 < remaining.Length) i++; // value consumed globally
            else programArgs.Add(remaining[i]);
        }
        return RunCommand.Execute(firstArg, programArgs.Count > 0 ? programArgs.ToArray() : null, copTarget, copFormat, copCommands, copDiag, copOnlyIfModified, copProviders.Count > 0 ? copProviders.ToArray() : null, copProfile);
    }

    // 2. URL → run remotely
    if (firstArg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        firstArg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        return RunCommand.Execute(firstArg, runArgs.Length > 1 ? runArgs[1..] : null);
    }

    // 3. Check if local .cop files define a command with this name
    var cwd = Directory.GetCurrentDirectory();
    var localCopFiles = Directory.GetFiles(cwd, "*.cop", SearchOption.TopDirectoryOnly);
    if (localCopFiles.Length > 0 && IsLocalCommand(firstArg, localCopFiles))
    {
        return RunCommand.Execute(firstArg, runArgs.Length > 1 ? runArgs[1..] : null);
    }

    // 4. Otherwise → treat all leading non-switch args as package names
    var packages = runArgs.TakeWhile(a => !a.StartsWith('-') && !a.StartsWith('/')).ToArray();

    if (packages.Length == 0)
    {
        // `cop run` followed only by options/flags — nothing to run.
        PrintRunUsage();
        return 2;
    }

    if (!allowPackages)
    {
        // Bare `cop <name>`: never silently treat a (mis)typed command as a package.
        return UnknownCommandError(packages);
    }

    var remainingArgs = runArgs.Skip(packages.Length).ToArray();

    // Parse common options from remaining args
    string? target = null;
    string? rules = null;
    string? format = "text";
    bool isDiag = diag;
    bool isOnlyIfModified = false;
    var pkgProviders = new List<string>();
    for (int i = 0; i < remainingArgs.Length; i++)
    {
        if (remainingArgs[i] == "-t" && i + 1 < remainingArgs.Length) target = remainingArgs[++i];
        else if (remainingArgs[i] == "-c" && i + 1 < remainingArgs.Length) rules = remainingArgs[++i];
        else if (remainingArgs[i] == "-f" && i + 1 < remainingArgs.Length) format = remainingArgs[++i];
        else if (remainingArgs[i] == "-p" && i + 1 < remainingArgs.Length) pkgProviders.Add(remainingArgs[++i]);
        else if (remainingArgs[i] == "-d" || remainingArgs[i] == "-dd" || remainingArgs[i] == "-ddd") isDiag = true;
        else if (remainingArgs[i] == "-om") isOnlyIfModified = true;
    }

    string rootPath = target != null ? Path.GetFullPath(target) : Directory.GetCurrentDirectory();
    string[]? rulesFilter = null;
    if (!string.IsNullOrEmpty(rules))
        rulesFilter = rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    return RunCommand.ExecutePackages(packages, rootPath, rulesFilter, format, isDiag, isOnlyIfModified, pkgProviders.Count > 0 ? pkgProviders.ToArray() : null);
}

/// <summary>
/// Prints an "Unknown command" error for a bare invocation, suggesting a close known verb
/// (if any) and the explicit `cop run` form for actually running a package.
/// </summary>
static int UnknownCommandError(string[] tokens)
{
    var name = tokens.Length > 0 ? tokens[0] : "";
    Console.Error.WriteLine($"Error: Unknown command '{name}'.");

    var suggestion = SuggestVerb(name);
    if (suggestion != null)
        Console.Error.WriteLine($"Did you mean 'cop {suggestion}'?");

    Console.Error.WriteLine($"To run a package, use: cop run {string.Join(' ', tokens)}");
    Console.Error.WriteLine("Run 'cop -h' to see available commands.");
    return 2;
}

static void PrintRunUsage()
{
    Console.Error.WriteLine("Usage: cop run <package|file.cop|url> [args] [options]");
    Console.Error.WriteLine("Run 'cop -h' for more options.");
}

/// <summary>
/// Returns the known verb closest to <paramref name="input"/> by edit distance, or null when
/// nothing is close enough to be a helpful suggestion.
/// </summary>
static string? SuggestVerb(string input)
{
    string[] verbs = { "run", "test", "syntax", "verify", "lock", "unlock", "help", "package", "repl", "init", "update", "vscode" };
    string? best = null;
    int bestDist = int.MaxValue;
    var lower = input.ToLowerInvariant();
    foreach (var v in verbs)
    {
        int d = Levenshtein(lower, v);
        if (d < bestDist) { bestDist = d; best = v; }
    }
    // Scale tolerance with length so short tokens don't match unrelated verbs.
    return best != null && bestDist <= 3 && bestDist * 2 <= input.Length ? best : null;
}

static int Levenshtein(string a, string b)
{
    var d = new int[a.Length + 1, b.Length + 1];
    for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
    for (int j = 0; j <= b.Length; j++) d[0, j] = j;
    for (int i = 1; i <= a.Length; i++)
        for (int j = 1; j <= b.Length; j++)
        {
            int cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
        }
    return d[a.Length, b.Length];
}

/// <summary>
/// True when a bare leading token names something cop can actually run — a .cop file, an HTTPS
/// URL, or a command defined in a local .cop file. Used to decide whether to show the startup
/// version notices: an unknown token returns false so a typo shows only the "unknown command"
/// error (and not a stale post-update summary).
/// </summary>
static bool ResolvesToRunnable(string token)
{
    if (token.EndsWith(".cop", StringComparison.OrdinalIgnoreCase)) return true;
    if (token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        token.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
    try
    {
        var cwd = Directory.GetCurrentDirectory();
        var localCopFiles = Directory.GetFiles(cwd, "*.cop", SearchOption.TopDirectoryOnly);
        return localCopFiles.Length > 0 && IsLocalCommand(token, localCopFiles);
    }
    catch
    {
        return false;
    }
}

/// <summary>
/// Checks if any local .cop file defines a command with the given name.
/// </summary>
static bool IsLocalCommand(string name, string[] copFiles)
{
    foreach (var file in copFiles)
    {
        try
        {
            var source = File.ReadAllText(file);
            var module = Cop.Lang.Parser.CopParser.Parse(source, file);
            foreach (var decl in module.Declarations)
            {
                if (decl is Cop.Lang.Ast.CommandDecl cmd && cmd.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (decl is Cop.Lang.Ast.FunctionDecl func && char.IsUpper(func.Name[0]) && func.Body is Cop.Lang.Ast.BlockBody
                    && func.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
    }
    return false;
}

/// <summary>
/// Bare invocation: run local .cop files if present, or show getting-started message.
/// </summary>
static int ExecuteDefault()
{
    var cwd = Directory.GetCurrentDirectory();
    var copFiles = Directory.GetFiles(cwd, "*.cop", SearchOption.TopDirectoryOnly);

    if (copFiles.Length == 0)
    {
        Console.WriteLine(HelpCommand.Banner);
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  cop run <package>      Run a package from a feed");
        Console.WriteLine("  cop <file.cop>         Run a local .cop file");
        Console.WriteLine("  cop package list       Browse available packages");
        Console.WriteLine("  cop repl              Launch interactive REPL");
        Console.WriteLine();
        Console.WriteLine("Getting started:");
        Console.WriteLine("  1. Run a package:  cop run <package-name>");
        Console.WriteLine("  2. Customize:      Create a .cop file with 'import <package>'");
        Console.WriteLine("                     then just run 'cop' with no arguments");
        Console.WriteLine();
        Console.WriteLine("  cop -h for more options");
        return 0;
    }

    // Parse .cop files to understand what they contain
    var imports = new List<string>();
    bool hasOwnLogic = false;

    foreach (var file in copFiles)
    {
        try
        {
            var source = File.ReadAllText(file);
            var module = Cop.Lang.Parser.CopParser.Parse(source, file);
            foreach (var decl in module.Declarations)
            {
                if (decl is Cop.Lang.Ast.ImportDecl imp)
                    imports.Add(imp.ModuleName);
                else if (decl is Cop.Lang.Ast.FunctionDecl || decl is Cop.Lang.Ast.LetDecl || decl is Cop.Lang.Ast.CommandDecl)
                    hasOwnLogic = true;
            }
        }
        catch { /* skip unparseable files */ }
    }

    if (hasOwnLogic)
    {
        // Local .cop files define their own logic — run as a program
        return RunCommand.Execute(null);
    }

    if (imports.Count > 0)
    {
        // Config-only: just imports — run those packages
        return RunCommand.ExecutePackages(imports.ToArray(), cwd, null, "text", false);
    }

    return 0;
}

